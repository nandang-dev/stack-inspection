using StackInspection.Domain;

namespace StackInspection.Domain.Tests;

public class SkuMatcherTests
{
    private static readonly string[] Candidates = ["68410975", "68582213", "68140913"];

    [Theory]
    [InlineData("68410975")]
    [InlineData("6841 0975")] // spasi dari OCR dibuang saat normalisasi
    public void ExactMatch_IsMatched(string ocr)
    {
        SkuMatch match = SkuMatcher.Match(ocr, Candidates, 2);

        Assert.Equal(("68410975", ReadStatus.Matched), (match.Sku, match.Status));
    }

    [Theory]
    [InlineData("68410B75")] // huruf tertukar
    [InlineData("68410875")] // 1 digit salah
    [InlineData("68411875")] // 2 digit salah
    public void SmallErrors_AreCorrected(string ocr)
    {
        SkuMatch match = SkuMatcher.Match(ocr, Candidates, 2);

        Assert.Equal(("68410975", ReadStatus.Corrected), (match.Sku, match.Status));
    }

    [Fact]
    public void DistanceAboveLimit_IsUnknown()
    {
        Assert.Equal(ReadStatus.Unknown, SkuMatcher.Match("12345678", Candidates, 2).Status);
    }

    [Fact]
    public void TieBetweenCandidates_IsUnknown()
    {
        SkuMatch match = SkuMatcher.Match("11111110", ["11111111", "11111112"], 2);

        Assert.Equal((SkuCodes.Unknown, ReadStatus.Unknown), (match.Sku, match.Status));
    }

    [Theory]
    [InlineData("68410975", "68410975", ReadStatus.Matched)]
    [InlineData("6841097", "UNKNOWN", ReadStatus.Unknown)]
    [InlineData("684109751", "UNKNOWN", ReadStatus.Unknown)]
    public void WithoutCandidates_OnlyExactlyEightDigitsAccepted(string ocr, string sku, ReadStatus status)
    {
        SkuMatch match = SkuMatcher.Match(ocr, [], 2);

        Assert.Equal((sku, status), (match.Sku, match.Status));
    }
}

public class GridBuilderTests
{
    private static BoundingBox Box(int column, int row, double dy = 0, double width = 100) =>
        new(column * 110, (row * 90) + dy, (column * 110) + width, (row * 90) + 80 + dy);

    [Fact]
    public void ShuffledBoxes_GetRowColumnLevel()
    {
        List<BoundingBox> boxes = [];
        for (int column = 0; column < 3; column++)
        {
            for (int row = 0; row < 4; row++)
            {
                boxes.Add(Box(column, row));
            }
        }

        BoundingBox[] shuffled = [.. boxes.OrderBy(b => (b.CenterX * 7) % 13).ThenBy(b => b.CenterY)];

        GridLayout layout = GridBuilder.Build(shuffled);

        Assert.Equal((4, 3, false), (layout.RowCount, layout.ColumnCount, layout.IsIrregular));
        for (int i = 0; i < shuffled.Length; i++)
        {
            GridPosition position = layout.Positions[i];
            Assert.Equal((int)(shuffled[i].X1 / 110), position.Column);
            Assert.Equal((int)(shuffled[i].Y1 / 90), position.Row);
            Assert.Equal(3 - position.Row, position.Level);
        }
    }

    [Fact]
    public void ColumnsWithDifferentHeights_LevelCountsFromBottom()
    {
        // kolom 0: 4 kardus, kolom 1: 2 kardus di bawah
        BoundingBox[] boxes = [Box(0, 0), Box(0, 1), Box(0, 2), Box(0, 3), Box(1, 2), Box(1, 3)];

        GridLayout layout = GridBuilder.Build(boxes);

        Assert.Equal([3, 2, 1, 0, 1, 0], layout.Positions.Select(p => p.Level));
        Assert.Equal([2, 3], layout.Positions.Skip(4).Select(p => p.Row));
    }

    [Fact]
    public void SlightlyTiltedRows_StillGrouped()
    {
        // CenterY bergeser ±20% tinggi kardus (80 px → ±16 px)
        BoundingBox[] boxes = [Box(0, 0, -16), Box(1, 0, 16), Box(2, 0, 0), Box(0, 1, 16), Box(1, 1, -16), Box(2, 1, 0)];

        GridLayout layout = GridBuilder.Build(boxes);

        Assert.Equal(2, layout.RowCount);
        Assert.Equal([0, 0, 0, 1, 1, 1], layout.Positions.Select(p => p.Row));
    }

    [Fact]
    public void MixedCartonSizes_AreIrregular()
    {
        BoundingBox[] boxes = [Box(0, 0), Box(1, 0), new(0, 90, 250, 170)];

        Assert.True(GridBuilder.Build(boxes).IsIrregular);
    }
}

public class FrontLayerFilterTests
{
    private static CartonBox Carton(double x1, double y1, double x2, double y2) => new(new BoundingBox(x1, y1, x2, y2), 0.9);

    [Fact]
    public void NormalColumn_IsKept()
    {
        CartonBox[] cartons = [Carton(0, 0, 100, 80), Carton(0, 80, 100, 160), Carton(0, 160, 100, 240)];

        FrontLayerResult result = FrontLayerFilter.Apply(cartons, 0.6, 0.35);

        Assert.Equal((3, 0), (result.Kept.Count, result.ExcludedCount));
    }

    [Fact]
    public void NarrowBoxAboveColumn_IsBackLayer()
    {
        // kardus belakang terlihat lebih kecil di atas tumpukan depan karena perspektif
        CartonBox[] cartons = [Carton(0, 80, 100, 160), Carton(0, 160, 100, 240), Carton(20, 40, 70, 78)];

        FrontLayerResult result = FrontLayerFilter.Apply(cartons, 0.6, 0.35);

        Assert.Equal((2, 1), (result.Kept.Count, result.ExcludedCount));
        Assert.DoesNotContain(result.Kept, c => c.Box.Y1 == 40);
    }

    [Fact]
    public void BoxesAboveLargeGap_AreBackLayer()
    {
        // celah 60 px (> 0.35 × 80) antara tumpukan depan dan kardus di atasnya
        CartonBox[] cartons =
        [
            Carton(0, 160, 100, 240), Carton(0, 80, 100, 160), Carton(0, -60, 100, 20), Carton(0, -140, 100, -60),
        ];

        FrontLayerResult result = FrontLayerFilter.Apply(cartons, 0.6, 0.35);

        Assert.Equal((2, 2), (result.Kept.Count, result.ExcludedCount));
    }
}
