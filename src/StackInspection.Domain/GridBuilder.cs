namespace StackInspection.Domain;

/// <summary>Satu sel grid tampilan (row 0 = atas, column 0 = kiri).</summary>
public readonly record struct GridCell(int Row, int Column);

/// <summary>
/// Posisi satu kardus. <c>Cells</c> = sel grid tampilan yang ditempati (kardus besar/lebar &gt; 1 sel, urut
/// row lalu column); <c>Level</c> dan <c>RestsOn</c> = posisi fisik di tumpukan.
/// </summary>
public sealed record CartonPlacement(IReadOnlyList<GridCell> Cells, int Level, IReadOnlyList<int> RestsOn);

/// <summary>Hasil penyusunan layout. <c>Cartons</c> berurutan sama dengan input.</summary>
public sealed record GridLayout(
    IReadOnlyList<CartonPlacement> Cartons,
    int RowCount,
    int ColumnCount,
    StackPaths Paths,
    bool IsIrregular);

/// <summary>
/// Menyusun dua lapis informasi dari kotak kardus:
/// grid tampilan (tepi-tepi kotak yang berdekatan dikelompokkan menjadi garis grid, lalu setiap kardus
/// menempati sel di antara garis terdekat dengan tepinya) dan relasi tumpuan fisik (<see cref="SupportGraph"/>).
/// </summary>
public static class GridBuilder
{
    /// <summary>Tepi yang jaraknya ≤ rasio ini × median lebar/tinggi kardus dianggap satu garis grid.</summary>
    public const double EdgeToleranceRatio = 0.3;

    /// <summary>Batas jumlah jalur tumpukan.</summary>
    public const int MaxPaths = 200;

    /// <summary>Toleransi pengelompokan baris (× tinggi rata-rata) yang dipakai <see cref="StackBuilder"/> untuk filter lapisan depan.</summary>
    public const double RowToleranceRatio = 0.5;

    public static GridLayout Build(IReadOnlyList<BoundingBox> boxes, int maxPaths = MaxPaths)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (boxes.Count == 0)
        {
            return new GridLayout([], 0, 0, new StackPaths([], false), false);
        }

        IReadOnlyList<IReadOnlyList<int>> restsOn = SupportGraph.RestsOn(boxes);
        int[] levels = SupportGraph.Levels(restsOn);
        StackPaths paths = SupportGraph.Paths(restsOn, i => boxes[i].CenterX, maxPaths);

        double[] columnLines = Lines([.. boxes.SelectMany(b => new[] { b.X1, b.X2 })], EdgeToleranceRatio * Median(boxes.Select(b => b.Width)));
        double[] rowLines = Lines([.. boxes.SelectMany(b => new[] { b.Y1, b.Y2 })], EdgeToleranceRatio * Median(boxes.Select(b => b.Height)));
        IReadOnlyList<GridCell>[] cells = AssignCells(boxes, columnLines, rowLines);

        CartonPlacement[] cartons = [.. Enumerable.Range(0, boxes.Count).Select(i => new CartonPlacement(cells[i], levels[i], restsOn[i]))];
        return new GridLayout(cartons, rowLines.Length - 1, columnLines.Length - 1, paths, IsIrregular(boxes, restsOn));
    }

    /// <summary>
    /// Setiap kardus menempati sel antara garis terdekat dengan tepi kiri-kanan dan atas-bawahnya (minimal satu sel).
    /// Sel yang diklaim dua kardus diberikan ke kardus yang paling banyak menutupinya; kardus yang kehilangan
    /// semua selnya tetap mendapat sel terbaiknya.
    /// </summary>
    private static IReadOnlyList<GridCell>[] AssignCells(IReadOnlyList<BoundingBox> boxes, double[] columnLines, double[] rowLines)
    {
        (int From, int To)[] columns = [.. boxes.Select(b => Span(columnLines, b.X1, b.X2))];
        (int From, int To)[] rows = [.. boxes.Select(b => Span(rowLines, b.Y1, b.Y2))];
        Dictionary<GridCell, (int Box, double Area)> owners = [];
        for (int i = 0; i < boxes.Count; i++)
        {
            foreach (GridCell cell in CellsOf(rows[i], columns[i]))
            {
                double area = Coverage(boxes[i], cell, columnLines, rowLines);
                if (!owners.TryGetValue(cell, out (int Box, double Area) owner) || area > owner.Area)
                {
                    owners[cell] = (i, area);
                }
            }
        }

        IReadOnlyList<GridCell>[] result = new IReadOnlyList<GridCell>[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            List<GridCell> owned = [.. CellsOf(rows[i], columns[i]).Where(c => owners[c].Box == i)];
            if (owned.Count == 0)
            {
                int index = i;
                owned.Add(CellsOf(rows[i], columns[i]).MaxBy(c => Coverage(boxes[index], c, columnLines, rowLines)));
            }

            result[i] = owned;
        }

        return result;
    }

    private static IEnumerable<GridCell> CellsOf((int From, int To) rows, (int From, int To) columns)
    {
        for (int row = rows.From; row < rows.To; row++)
        {
            for (int column = columns.From; column < columns.To; column++)
            {
                yield return new GridCell(row, column);
            }
        }
    }

    private static double Coverage(BoundingBox box, GridCell cell, double[] columnLines, double[] rowLines)
    {
        double width = Math.Min(box.X2, columnLines[cell.Column + 1]) - Math.Max(box.X1, columnLines[cell.Column]);
        double height = Math.Min(box.Y2, rowLines[cell.Row + 1]) - Math.Max(box.Y1, rowLines[cell.Row]);
        return Math.Max(0, width) * Math.Max(0, height);
    }

    /// <summary>Indeks garis terdekat dengan kedua tepi; minimal satu sel.</summary>
    private static (int From, int To) Span(double[] lines, double start, double end)
    {
        int from = Nearest(lines, start);
        int to = Nearest(lines, end);
        if (to > from)
        {
            return (from, to);
        }

        return from + 1 < lines.Length ? (from, from + 1) : (from - 1, from);
    }

    private static int Nearest(double[] lines, double value)
    {
        int best = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            if (Math.Abs(lines[i] - value) < Math.Abs(lines[best] - value))
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>Garis grid = rata-rata setiap kelompok tepi (urut menaik).</summary>
    private static double[] Lines(double[] edges, double tolerance)
    {
        int[] groups = Cluster(edges, tolerance);
        return [.. edges
            .Select((value, i) => (value, group: groups[i]))
            .GroupBy(e => e.group)
            .OrderBy(g => g.Key)
            .Select(g => g.Average(e => e.value))];
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>
    /// Tidak beraturan: ada kardus yang menumpu di lebih dari satu kardus, satu kardus menanggung lebih dari satu
    /// kardus berdampingan, atau ukuran kardus sangat campur.
    /// </summary>
    private static bool IsIrregular(IReadOnlyList<BoundingBox> boxes, IReadOnlyList<IReadOnlyList<int>> restsOn)
    {
        if (restsOn.Any(r => r.Count > 1) || restsOn.SelectMany(r => r).GroupBy(i => i).Any(g => g.Count() > 1))
        {
            return true;
        }

        double minWidth = boxes.Min(b => b.Width);
        double maxWidth = boxes.Max(b => b.Width);
        return minWidth > 0 && maxWidth / minWidth > 2.0;
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
