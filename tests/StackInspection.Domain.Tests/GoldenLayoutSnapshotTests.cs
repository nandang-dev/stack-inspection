using System.Text.Json;

namespace StackInspection.Domain.Tests;

/// <summary>
/// Snapshot layout 42 foto golden-v3 (kotak label yang lolos filter lapisan depan). Hasil ini sudah dicek visual
/// (golden-set-v2/review-layout-v3). Jika test gagal karena perubahan algoritma yang disengaja, buat ulang gambar
/// review, cek ulang, lalu perbarui <c>fixtures/golden-v3-layout.json</c>.
/// </summary>
public class GoldenLayoutSnapshotTests
{
    private sealed record Snapshot(
        double[][] Boxes, int RowCount, int ColumnCount, int[] Levels, int[][] RestsOn, int[][][] Cells);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static readonly Dictionary<string, Snapshot> Photos = JsonSerializer.Deserialize<Dictionary<string, Snapshot>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "golden-v3-layout.json")), Json)!;

    public static TheoryData<string> Names() => [.. Photos.Keys];

    [Theory]
    [MemberData(nameof(Names))]
    public void Layout_MatchesReviewedSnapshot(string photo)
    {
        Snapshot expected = Photos[photo];

        GridLayout layout = GridBuilder.Build([.. expected.Boxes.Select(b => new BoundingBox(b[0], b[1], b[2], b[3]))]);

        Assert.Equal((expected.RowCount, expected.ColumnCount), (layout.RowCount, layout.ColumnCount));
        Assert.Equal(expected.Levels, layout.Cartons.Select(c => c.Level));
        Assert.Equal(expected.RestsOn, layout.Cartons.Select(c => c.RestsOn.ToArray()));
        Assert.Equal(expected.Cells, layout.Cartons.Select(c => c.Cells.Select(g => new[] { g.Row, g.Column }).ToArray()));
    }

    [Fact]
    public void Snapshot_CoversAllGoldenPhotos()
    {
        Assert.Equal(42, Photos.Count);
    }
}
