using StackInspection.Domain;

namespace StackInspection.Domain.Tests;

/// <summary>Fixture grid dari brief (8 baris × 6 kolom, row 0 = atas).</summary>
internal static class ReferenceGrid
{
    public const string A = "68140913"; // SariWangi, kelas A, maxStack 7
    public const string B = "68410975"; // Royco, kelas B, maxStack 8
    public const string C = "68582213"; // Bango, kelas C, maxStack 12

    private static readonly string[] Rows =
    [
        "A C C B B B",
        "A C C B B B",
        "A C C B B B",
        "A C C B B B",
        "A A C B B B",
        "A A C B A A",
        "A A A B A A",
        "A A A A A A",
    ];

    public static IReadOnlyDictionary<string, MasterSku> Master { get; } = new Dictionary<string, MasterSku>
    {
        [B] = new(B, "Royco Ayam 576 x 8g", 8, 5.2, SkuClass.B),
        [C] = new(C, "Bango Kecap Manis 24 x 210ml", 12, 7.8, SkuClass.C),
        [A] = new(A, "SariWangi Teh Asli 48 x TB30 x 1.85g", 7, 3.1, SkuClass.A),
    };

    public static List<DetectedCell> Cells()
    {
        List<DetectedCell> cells = [];
        for (int row = 0; row < Rows.Length; row++)
        {
            string[] codes = Rows[row].Split(' ');
            for (int column = 0; column < codes.Length; column++)
            {
                string sku = codes[column] switch { "A" => A, "B" => B, _ => C };
                int level = Rows.Length - 1 - row;
                BoundingBox box = new(column * 100, row * 80, (column * 100) + 95, (row * 80) + 75);
                cells.Add(new DetectedCell(row, column, level, sku, sku, 0.98, 0.95, ReadStatus.Matched, box));
            }
        }

        return cells;
    }

    public static DetectedCell At(List<DetectedCell> cells, int row, int column) =>
        cells.Single(c => c.Row == row && c.Column == column);
}
