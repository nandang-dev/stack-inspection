namespace StackInspection.Domain;

/// <summary>Posisi satu kardus di grid.</summary>
public readonly record struct GridPosition(int Row, int Column, int Level);

/// <summary>Hasil penyusunan grid.</summary>
public sealed record GridLayout(
    IReadOnlyList<GridPosition> Positions,
    int RowCount,
    int ColumnCount,
    bool IsIrregular);

/// <summary>
/// Menyusun baris dan kolom dari koordinat kardus. Kolom disusun secara fisik dari bawah ke atas
/// (<see cref="StackBuilder"/>), baris dikelompokkan berdasarkan CenterY, lalu level dihitung per
/// kolom dari bawah (0 = paling bawah).
/// </summary>
public static class GridBuilder
{
    public const double ColumnToleranceRatio = 0.5;
    public const double RowToleranceRatio = 0.5;

    /// <summary>Posisi dikembalikan dengan urutan yang sama seperti <paramref name="boxes"/>.</summary>
    public static GridLayout Build(IReadOnlyList<BoundingBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (boxes.Count == 0)
        {
            return new GridLayout([], 0, 0, false);
        }

        StackAssignment stacks = StackBuilder.Assign(boxes);
        int[] levels = new int[boxes.Count];
        foreach (IReadOnlyList<int> column in stacks.BottomToTopPerColumn)
        {
            for (int level = 0; level < column.Count; level++)
            {
                levels[column[level]] = level;
            }
        }

        bool irregular = stacks.IsIrregular;
        double minWidth = boxes.Min(b => b.Width);
        double maxWidth = boxes.Max(b => b.Width);
        if (minWidth > 0 && maxWidth / minWidth > 2.0)
        {
            irregular = true; // ukuran kardus sangat campur
        }

        GridPosition[] positions = [.. Enumerable.Range(0, boxes.Count).Select(i => new GridPosition(stacks.Rows[i], stacks.Columns[i], levels[i]))];
        return new GridLayout(positions, stacks.Rows.Max() + 1, stacks.Columns.Max() + 1, irregular);
    }

    /// <summary>
    /// Mengelompokkan nilai 1 dimensi: nilai diurutkan, grup baru dimulai jika jarak ke rata-rata
    /// grup berjalan lebih dari <paramref name="tolerance"/>. Indeks grup berurutan dari nilai terkecil.
    /// </summary>
    internal static int[] Cluster(IReadOnlyList<double> values, double tolerance)
    {
        int[] order = [.. Enumerable.Range(0, values.Count).OrderBy(i => values[i])];
        int[] groups = new int[values.Count];
        int group = 0;
        double sum = 0;
        int count = 0;
        foreach (int index in order)
        {
            double value = values[index];
            if (count > 0 && value - (sum / count) > tolerance)
            {
                group++;
                sum = 0;
                count = 0;
            }

            groups[index] = group;
            sum += value;
            count++;
        }

        return groups;
    }
}
