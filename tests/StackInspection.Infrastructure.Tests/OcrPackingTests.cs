using StackInspection.Infrastructure.Vision;

namespace StackInspection.Infrastructure.Tests;

public class OcrPackingTests
{
    [Fact]
    public void ManySmallAreas_ArePackedWithoutOverlapInsideThePage()
    {
        // 40 area label 240 × 90 (dus 400 × 300); dengan celah 16 px: 3 per baris × 8 baris = 24 per halaman
        OcrRect[] areas = [.. Enumerable.Range(0, 40).Select(i => new OcrRect(i * 410, 600, 240, 90))];

        OcrPacking packing = OcrPacker.Pack(areas, 960, 16, 320);

        Assert.Equal(40, packing.Pieces.Count);
        Assert.Equal(2, packing.PageCount);
        Assert.Equal(24, packing.Pieces.Count(p => p.Page == 0));
        Assert.All(packing.Pieces, p => Assert.True(p.X >= 0 && p.Y >= 0 && p.X + p.Source.Width <= 960 && p.Y + p.Source.Height <= 960));
        foreach (OcrPiece a in packing.Pieces)
        {
            foreach (OcrPiece b in packing.Pieces.Where(b => b != a && b.Page == a.Page))
            {
                bool apart = a.X + a.Source.Width + 16 <= b.X || b.X + b.Source.Width + 16 <= a.X
                    || a.Y + a.Source.Height + 16 <= b.Y || b.Y + b.Source.Height + 16 <= a.Y;
                Assert.True(apart, $"{a} menempel {b}");
            }
        }
    }

    [Fact]
    public void AreasThatDoNotFit_GoToNextPage()
    {
        OcrRect[] areas = [.. Enumerable.Range(0, 30).Select(i => new OcrRect(0, i * 400, 400, 300))];

        OcrPacking packing = OcrPacker.Pack(areas, 960, 16, 320);

        // dengan celah 16 px: 2 per baris × 2 baris = 4 per halaman → 8 halaman
        Assert.Equal(8, packing.PageCount);
    }

    [Fact]
    public void AreaLargerThanPage_IsSplitIntoOverlappingTiles()
    {
        OcrPacking packing = OcrPacker.Pack([new OcrRect(100, 200, 2000, 900)], 960, 16, 320);

        Assert.True(packing.Pieces.Count > 1);
        Assert.All(packing.Pieces, p => Assert.True(p.Source.Width <= 944 && p.Source.Height <= 944));
        Assert.Equal(100, packing.Pieces.Min(p => p.Source.X));
        Assert.Equal(2100, packing.Pieces.Max(p => p.Source.X + p.Source.Width));
    }

    [Fact]
    public void NoAreas_NoPages()
    {
        Assert.Equal(0, OcrPacker.Pack([], 960, 16, 320).PageCount);
    }
}
