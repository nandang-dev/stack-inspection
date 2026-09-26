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
/// Menyusun baris dan kolom dari koordinat kardus: kolom dikelompokkan berdasarkan kedekatan
/// CenterX, baris berdasarkan CenterY, lalu level dihitung per kolom dari bawah (0 = paling bawah).
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

        double meanWidth = boxes.Average(b => b.Width);
        double meanHeight = boxes.Average(b => b.Height);
        int[] columns = Cluster(boxes.Select(b => b.CenterX).ToArray(), ColumnToleranceRatio * meanWidth);
        int[] rows = Cluster(boxes.Select(b => b.CenterY).ToArray(), RowToleranceRatio * meanHeight);

        int[] levels = new int[boxes.Count];
        bool irregular = false;
        foreach (IGrouping<int, int> column in Enumerable.Range(0, boxes.Count).GroupBy(i => columns[i]))
        {
            List<int> bottomToTop = [.. column.OrderByDescending(i => boxes[i].CenterY)];
            for (int level = 0; level < bottomToTop.Count; level++)
            {
                levels[bottomToTop[level]] = level;
            }

            // Dua kardus di baris yang sama dalam satu kolom = pengelompokan tidak konsisten.
            if (bottomToTop.Select(i => rows[i]).Distinct().Count() != bottomToTop.Count)
            {
                irregular = true;
            }
        }

        double minWidth = boxes.Min(b => b.Width);
        double maxWidth = boxes.Max(b => b.Width);
        if (minWidth > 0 && maxWidth / minWidth > 2.0)
        {
            irregular = true; // ukuran kardus sangat campur
        }

        GridPosition[] positions = [.. Enumerable.Range(0, boxes.Count).Select(i => new GridPosition(rows[i], columns[i], levels[i]))];
        return new GridLayout(positions, rows.Max() + 1, columns.Max() + 1, irregular);
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
