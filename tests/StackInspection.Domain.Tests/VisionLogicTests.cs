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
    public void ShortStripOnTopOfColumn_IsBackLayer()
    {
        // koordinat asli: bagian atas kardus belakang (tinggi 81) menyembul di atas kardus depan (tinggi 153)
        CartonBox[] cartons = [Carton(761, 1143, 1002, 1298), Carton(819, 995, 1059, 1148), Carton(819, 916, 971, 997)];

        FrontLayerResult result = FrontLayerFilter.Apply(cartons, 0.6, 0.35, 0.55);

        Assert.Equal((2, 1), (result.Kept.Count, result.ExcludedCount));
        Assert.DoesNotContain(result.Kept, c => c.Box.Y1 == 916);
        Assert.Equal(0, FrontLayerFilter.Apply(cartons, 0.6, 0.35).ExcludedCount); // 0 = nonaktif
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

public class StackBuilderTests
{
    [Fact]
    public void NarrowSidewaysColumnsNextToWideColumns_AreNotMerged()
    {
        // kolom lebar (240 px) di x=0, dua kolom sempit menyamping (130 px) di x=250 dan x=390,
        // kolom lebar lagi di x=530; pusat kolom sempit berdekatan dengan kolom lebar
        List<BoundingBox> boxes = [];
        for (int level = 0; level < 5; level++)
        {
            double y1 = 1000 - ((level + 1) * 100);
            boxes.Add(new BoundingBox(0, y1, 240, y1 + 95));
            boxes.Add(new BoundingBox(250, y1, 380, y1 + 95));
            boxes.Add(new BoundingBox(390, y1, 520, y1 + 95));
            boxes.Add(new BoundingBox(530, y1, 770, y1 + 95));
        }

        StackAssignment result = StackBuilder.Assign(boxes);

        Assert.Equal(4, result.BottomToTopPerColumn.Count);
        Assert.All(result.BottomToTopPerColumn, column => Assert.Equal(5, column.Count));
        for (int i = 0; i < boxes.Count; i++)
        {
            Assert.Equal(i % 4, result.Columns[i]);
        }

        Assert.False(result.IsIrregular);
    }

    [Fact]
    public void ShiftedCartonAbove_StaysInItsStack()
    {
        // kardus atas bergeser 40% ke kanan, masih menumpu di kardus bawahnya
        BoundingBox[] boxes = [new(0, 100, 100, 200), new(40, 0, 140, 100), new(110, 100, 210, 200)];

        StackAssignment result = StackBuilder.Assign(boxes);

        Assert.Equal(result.Columns[0], result.Columns[1]);
        Assert.NotEqual(result.Columns[0], result.Columns[2]);
        Assert.False(result.IsIrregular);
    }

    [Fact]
    public void CartonStraddlingTwoStacks_IsIrregular()
    {
        // kardus atas tepat di tengah dua kardus bawah (tumpukan silang)
        BoundingBox[] boxes = [new(0, 100, 100, 200), new(100, 100, 200, 200), new(50, 0, 150, 100)];

        StackAssignment result = StackBuilder.Assign(boxes);

        Assert.True(result.IsIrregular);
        Assert.Equal(2, result.BottomToTopPerColumn.Count);
    }

    /// <summary>Potongan koordinat asli foto truk: kardus menyamping di bawah, kardus depan lebih lebar di atasnya.</summary>
    private static readonly BoundingBox[] TruckPhoto =
    [
        new(1, 2148, 126, 2268), new(3, 2038, 126, 2162), new(8, 1926, 133, 2042), new(7, 1818, 132, 1930),
        new(19, 1711, 141, 1822), new(21, 1611, 148, 1720), new(32, 1439, 207, 1618), new(43, 1272, 222, 1449),
        new(514, 1800, 650, 1922), new(641, 1797, 775, 1915), new(499, 1624, 694, 1806), new(683, 1619, 871, 1797),
        new(557, 1448, 749, 1629), new(744, 1447, 934, 1623), new(576, 1296, 817, 1454),
    ];

    [Fact]
    public void RealTruckPhoto_SidewaysCartonsStackedDirectly_FormOneColumn()
    {
        StackAssignment result = StackBuilder.Assign(TruckPhoto);

        // kolom kiri: 6 kardus menyamping (termasuk dua yang CenterY-nya berdekatan) + 2 kardus depan
        Assert.Contains(result.BottomToTopPerColumn, c => c.SequenceEqual([0, 1, 2, 3, 4, 5, 6, 7]));
        // kardus depan diutamakan menumpu ke kardus yang tepat di bawahnya, bukan yang dua baris di bawah
        Assert.Equal(result.Columns[10], result.Columns[12]);
        Assert.Equal(result.Columns[12], result.Columns[14]);
    }

    [Fact]
    public void RealTruckPhoto_NothingIsTreatedAsBackLayer()
    {
        CartonBox[] cartons = [.. TruckPhoto.Select(b => new CartonBox(b, 0.9))];

        Assert.Equal(0, FrontLayerFilter.Apply(cartons, 0.6, 0.35).ExcludedCount);
    }

    [Fact]
    public void BoxWithoutSupport_StartsNewColumn()
    {
        BoundingBox[] boxes = [new(0, 100, 100, 200), new(300, 0, 400, 100)];

        StackAssignment result = StackBuilder.Assign(boxes);

        Assert.Equal(2, result.BottomToTopPerColumn.Count);
    }
}

public class ContainmentFilterTests
{
    [Fact]
    public void NestedLowerConfidenceBox_IsRemoved()
    {
        CartonBox outer = new(new BoundingBox(0, 0, 200, 200), 0.9);
        CartonBox inner = new(new BoundingBox(10, 10, 110, 90), 0.7); // IoU kecil, tapi 100% di dalam
        CartonBox neighbour = new(new BoundingBox(210, 0, 400, 200), 0.8);

        IReadOnlyList<CartonBox> kept = ContainmentFilter.Apply([outer, inner, neighbour], 0.8);

        Assert.Equal([outer, neighbour], kept);
    }

    [Fact]
    public void HigherConfidenceInnerBox_IsKeptInstead()
    {
        CartonBox outer = new(new BoundingBox(0, 0, 200, 200), 0.6);
        CartonBox inner = new(new BoundingBox(10, 10, 190, 190), 0.9);

        Assert.Equal([inner], ContainmentFilter.Apply([outer, inner], 0.8));
    }

    [Fact]
    public void PartialOverlapAndDisabledThreshold_KeepAll()
    {
        CartonBox a = new(new BoundingBox(0, 0, 100, 100), 0.9);
        CartonBox b = new(new BoundingBox(50, 0, 150, 100), 0.8); // 50% di dalam

        Assert.Equal(2, ContainmentFilter.Apply([a, b], 0.8).Count);
        Assert.Equal(2, ContainmentFilter.Apply([a, new CartonBox(new BoundingBox(10, 10, 20, 20), 0.5)], 0).Count);
    }
}
