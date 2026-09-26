namespace StackInspection.Domain;

/// <summary>Hasil penyaringan lapisan depan.</summary>
public sealed record FrontLayerResult(IReadOnlyList<CartonBox> Kept, int ExcludedCount);

/// <summary>
/// Membuang kardus yang kemungkinan besar lapisan belakang (terlihat di atas tumpukan depan karena
/// perspektif): terlalu sempit dibanding median kolomnya, atau berada di atas celah vertikal besar.
/// </summary>
public static class FrontLayerFilter
{
    public static FrontLayerResult Apply(IReadOnlyList<CartonBox> cartons, double minWidthRatio, double maxGapRatio)
    {
        ArgumentNullException.ThrowIfNull(cartons);
        if (cartons.Count == 0)
        {
            return new FrontLayerResult([], 0);
        }

        double meanWidth = cartons.Average(c => c.Box.Width);
        int[] columns = GridBuilder.Cluster(
            cartons.Select(c => c.Box.CenterX).ToArray(),
            GridBuilder.ColumnToleranceRatio * meanWidth);

        HashSet<int> excluded = [];
        foreach (IGrouping<int, int> column in Enumerable.Range(0, cartons.Count).GroupBy(i => columns[i]))
        {
            List<int> bottomToTop = [.. column.OrderByDescending(i => cartons[i].Box.Y2)];
            double medianWidth = Median(bottomToTop.Select(i => cartons[i].Box.Width));
            double medianHeight = Median(bottomToTop.Select(i => cartons[i].Box.Height));

            double? topOfBelow = null;
            bool aboveGap = false;
            foreach (int index in bottomToTop)
            {
                BoundingBox box = cartons[index].Box;
                if (aboveGap || box.Width < minWidthRatio * medianWidth)
                {
                    excluded.Add(index);
                    continue;
                }

                if (topOfBelow is double top && top - box.Y2 > maxGapRatio * medianHeight)
                {
                    aboveGap = true; // semua kardus di atas celah dianggap lapisan belakang
                    excluded.Add(index);
                    continue;
                }

                topOfBelow = box.Y1;
            }
        }

        CartonBox[] kept = [.. Enumerable.Range(0, cartons.Count).Where(i => !excluded.Contains(i)).Select(i => cartons[i])];
        return new FrontLayerResult(kept, excluded.Count);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        if (sorted.Length == 0)
        {
            return 0;
        }

        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
