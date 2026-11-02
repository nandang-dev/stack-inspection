namespace StackInspection.Domain;

/// <summary>Hasil penyaringan lapisan depan.</summary>
public sealed record FrontLayerResult(IReadOnlyList<CartonBox> Kept, int ExcludedCount);

/// <summary>
/// Membuang kardus yang kemungkinan besar lapisan belakang (terlihat di atas tumpukan depan karena
/// perspektif). Setiap kardus dibandingkan dengan kardus tepat di bawahnya dalam tumpukan yang sama
/// (<see cref="StackBuilder"/>): jauh lebih sempit, atau dipisahkan celah vertikal besar. "Lebar" di sini =
/// gabungan lebar semua kardus yang berdampingan menumpu kardus bawah yang sama (<see cref="SupportGraph"/>),
/// sehingga dua kardus depan berdampingan di atas satu kardus lebar tidak dianggap lapisan belakang. Semua kardus
/// di atas kardus yang dibuang ikut dibuang. Kotak teratas yang jauh lebih pendek dari kardus di bawahnya
/// juga dibuang: itu bagian atas kardus belakang yang menyembul di atas tumpukan depan.
/// </summary>
public static class FrontLayerFilter
{
    /// <param name="minTopHeightRatio">Kotak teratas dengan tinggi &lt; rasio ini × tinggi kardus di bawahnya dibuang (0 = nonaktif).</param>
    public static FrontLayerResult Apply(
        IReadOnlyList<CartonBox> cartons, double minWidthRatio, double maxGapRatio, double minTopHeightRatio = 0)
    {
        ArgumentNullException.ThrowIfNull(cartons);
        if (cartons.Count == 0)
        {
            return new FrontLayerResult([], 0);
        }

        BoundingBox[] boxes = [.. cartons.Select(c => c.Box)];
        StackAssignment stacks = StackBuilder.Assign(boxes);
        IReadOnlyList<IReadOnlyList<int>> restsOn = SupportGraph.RestsOn(boxes);
        HashSet<int> excluded = [];
        foreach (IReadOnlyList<int> column in stacks.BottomToTopPerColumn)
        {
            int? below = null;
            bool backLayer = false;
            List<int> front = [];
            foreach (int index in column)
            {
                BoundingBox box = cartons[index].Box;
                if (!backLayer && below is int underIndex)
                {
                    BoundingBox under = boxes[underIndex];
                    bool tooNarrow = LayerWidth(boxes, restsOn, index, underIndex) < minWidthRatio * under.Width;
                    bool gap = under.Y1 - box.Y2 > maxGapRatio * under.Height;
                    backLayer = tooNarrow || gap;
                }

                if (backLayer)
                {
                    excluded.Add(index);
                    continue;
                }

                front.Add(index);
                below = index;
            }

            if (front.Count >= 2)
            {
                BoundingBox top = cartons[front[^1]].Box;
                BoundingBox under = cartons[front[^2]].Box;
                if (top.Height < minTopHeightRatio * under.Height)
                {
                    excluded.Add(front[^1]);
                }
            }
        }

        CartonBox[] kept = [.. Enumerable.Range(0, cartons.Count).Where(i => !excluded.Contains(i)).Select(i => cartons[i])];
        return new FrontLayerResult(kept, excluded.Count);
    }

    /// <summary>
    /// Lebar gabungan (tepi kiri terkecil sampai tepi kanan terbesar) kardus <paramref name="index"/> dan semua
    /// kardus lain yang juga menumpu <paramref name="under"/>.
    /// </summary>
    private static double LayerWidth(BoundingBox[] boxes, IReadOnlyList<IReadOnlyList<int>> restsOn, int index, int under)
    {
        BoundingBox[] layer = [boxes[index], .. Enumerable.Range(0, boxes.Length)
            .Where(i => i != index && restsOn[i].Contains(under))
            .Select(i => boxes[i])];
        return layer.Max(b => b.X2) - layer.Min(b => b.X1);
    }
}
