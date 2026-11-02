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
        Assert.All(packing.Pieces, p => Assert.True(p.X >= 0 && p.Y >= 0 && p.X + p.Width <= 960 && p.Y + p.Height <= 960));
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
    public void NarrowAreas_AreUpscaledToMinWidth_UpToMaxScale()
    {
        // area label dus kecil (136 px) → ×2,35; area 60 px → dibatasi ×3; area lebar tidak diubah
        OcrPacking packing = OcrPacker.Pack(
            [new OcrRect(0, 0, 136, 72), new OcrRect(500, 0, 60, 30), new OcrRect(0, 500, 400, 120)], 960, 16, 320, 320, 3.0);

        OcrPiece small = packing.Pieces.Single(p => p.Source.X == 0 && p.Source.Y == 0);
        OcrPiece tiny = packing.Pieces.Single(p => p.Source.X == 500);
        OcrPiece wide = packing.Pieces.Single(p => p.Source.Y == 500);
        Assert.Equal((320, 169), (small.Width, small.Height));
        Assert.Equal((3.0, 180, 90), (tiny.Scale, tiny.Width, tiny.Height));
        Assert.Equal((1.0, 400, 120), (wide.Scale, wide.Width, wide.Height));
        Assert.All(packing.Pieces, p => Assert.True(p.X + p.Width <= 960 && p.Y + p.Height <= 960));
    }

    [Fact]
    public void UpscaledAreaTallerThanPage_IsTiledInSourcePixels()
    {
        // 200 × 800 diperbesar ×1,6 → 320 × 1280 (> halaman) → dipotong di koordinat foto, tiap tile muat setelah diperbesar
        OcrPacking packing = OcrPacker.Pack([new OcrRect(10, 20, 200, 800)], 960, 16, 320, 320, 3.0);

        Assert.True(packing.Pieces.Count > 1);
        Assert.All(packing.Pieces, p => Assert.True(p.Scale == 1.6 && p.Height <= 928 && p.Y + p.Height <= 960));
        Assert.Equal(20, packing.Pieces.Min(p => p.Source.Y));
        Assert.Equal(820, packing.Pieces.Max(p => p.Source.Y + p.Source.Height));
    }

    [Fact]
    public void NoAreas_NoPages()
    {
        Assert.Equal(0, OcrPacker.Pack([], 960, 16, 320).PageCount);
    }
}
