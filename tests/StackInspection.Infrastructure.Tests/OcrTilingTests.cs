using StackInspection.Domain;
using StackInspection.Infrastructure.Vision;

namespace StackInspection.Infrastructure.Tests;

public class OcrTilingTests
{
    [Fact]
    public void Tiles_CoverWholeAreaWithOverlap()
    {
        IReadOnlyList<OcrTile> tiles = OcrTiling.Tiles(1900, 3000, 960, 320);

        Assert.Equal([0, 640, 940], tiles.Select(t => t.X).Distinct());
        Assert.Equal([0, 640, 1280, 1920, 2040], tiles.Select(t => t.Y).Distinct());
        Assert.All(tiles, t => Assert.Equal((960, 960), (t.Width, t.Height)));
        Assert.Equal(1900, tiles.Max(t => t.X + t.Width));
        Assert.Equal(3000, tiles.Max(t => t.Y + t.Height));
    }

    [Fact]
    public void SmallArea_IsSingleTile()
    {
        OcrTile tile = Assert.Single(OcrTiling.Tiles(500, 700, 960, 320));

        Assert.Equal(new OcrTile(0, 0, 500, 700), tile);
    }

    [Fact]
    public void Deduplicate_KeepsFullTextOverCutPiece()
    {
        BoundingBox full = new(100, 100, 300, 140);
        BoundingBox cut = new(100, 100, 180, 140); // potongan di tepi tile lain
        BoundingBox other = new(400, 100, 600, 140);

        Assert.Equal([0, 2], OcrTiling.Deduplicate([full, cut, other], 0.6));
    }

    [Fact]
    public void Union_AddsMarginAndClamps()
    {
        BoundingBox union = OcrTiling.Union([new(10, 50, 100, 200), new(300, 20, 500, 400)], 16, 510, 1000);

        Assert.Equal(new BoundingBox(0, 4, 510, 416), union);
    }
}
