namespace StackInspection.Domain;

/// <summary>Jalur tumpukan bawah → atas (indeks kardus) hasil penelusuran relasi tumpuan.</summary>
public sealed record StackPaths(IReadOnlyList<IReadOnlyList<int>> BottomToTop, bool Truncated);

/// <summary>
/// Relasi tumpuan fisik: setiap kardus mencatat kardus yang langsung ditumpunya (bisa lebih dari satu,
/// mis. kardus lebar yang menjembatani dua tumpukan). Level = 0 di lantai, selain itu 1 + level tertinggi
/// di antara tumpuannya.
/// </summary>
public static class SupportGraph
{
    /// <summary>
    /// Cadangan tumpuan (kardus di antaranya tidak terdeteksi) hanya dipakai jika celahnya ≤ rasio ini × tinggi
    /// kardus bawah (± satu kardus hilang). Lebih jauh dari itu (mis. kotak di tanah di bawah bak truk) = di lantai.
    /// </summary>
    public const double MaxFallbackGapRatio = 1.5;

    /// <summary>
    /// Tumpuan langsung: kardus di bawah yang tumpang tindih horizontal ≥ <see cref="StackBuilder.MinOverlapRatio"/>
    /// dan celahnya ≤ <see cref="StackBuilder.AdjacentGapRatio"/> × tingginya. Jika tidak ada (mis. kardus di
    /// tengah tidak terdeteksi), dipakai kardus terdekat di bawahnya yang tumpang tindih dalam jarak
    /// <see cref="MaxFallbackGapRatio"/>. Kandidat yang tertutup
    /// kandidat lain (mis. kardus tinggi di bawah kardus pendek, yang toleransi celahnya besar) dibuang.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> RestsOn(IReadOnlyList<BoundingBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        List<IReadOnlyList<int>> result = [];
        for (int i = 0; i < boxes.Count; i++)
        {
            BoundingBox box = boxes[i];
            List<(int Index, double Gap, bool Adjacent)> candidates = [];
            for (int j = 0; j < boxes.Count; j++)
            {
                BoundingBox below = boxes[j];
                if (j == i || StackBuilder.HorizontalOverlap(box, below) < StackBuilder.MinOverlapRatio || !StackBuilder.IsAbove(box, below))
                {
                    continue;
                }

                double gap = below.Y1 - box.Y2;
                candidates.Add((j, gap, gap <= StackBuilder.AdjacentGapRatio * below.Height));
            }

            if (candidates.Exists(c => c.Adjacent))
            {
                result.Add(Uncovered(boxes, [.. candidates.Where(c => c.Adjacent).Select(c => c.Index)]));
                continue;
            }

            candidates.RemoveAll(c => c.Gap > MaxFallbackGapRatio * boxes[c.Index].Height);
            if (candidates.Count == 0)
            {
                result.Add([]);
                continue;
            }

            double nearest = candidates.Min(c => c.Gap);
            result.Add(Uncovered(boxes, [.. candidates
                .Where(c => c.Gap - nearest <= StackBuilder.AdjacentGapRatio * boxes[c.Index].Height)
                .Select(c => c.Index)]));
        }

        return result;
    }

    /// <summary>Kandidat yang tidak berada di bawah kandidat lain (urut indeks).</summary>
    private static int[] Uncovered(IReadOnlyList<BoundingBox> boxes, int[] candidates) =>
        [.. candidates
            .Where(j => !candidates.Any(k => k != j
                && StackBuilder.HorizontalOverlap(boxes[k], boxes[j]) >= StackBuilder.MinOverlapRatio
                && StackBuilder.IsAbove(boxes[k], boxes[j])))
            .Order()];

    /// <summary>Level per kardus dari relasi tumpuan (tumpuan selalu berada di bawah, jadi tidak ada siklus).</summary>
    public static int[] Levels(IReadOnlyList<IReadOnlyList<int>> restsOn)
    {
        ArgumentNullException.ThrowIfNull(restsOn);
        int[] levels = new int[restsOn.Count];
        int[] state = new int[restsOn.Count]; // 0 = belum, 1 = sedang dihitung, 2 = selesai

        int Level(int index)
        {
            if (state[index] == 2)
            {
                return levels[index];
            }

            if (state[index] == 1)
            {
                throw new InvalidOperationException("Relasi tumpuan membentuk siklus.");
            }

            state[index] = 1;
            levels[index] = restsOn[index].Count == 0 ? 0 : 1 + restsOn[index].Max(Level);
            state[index] = 2;
            return levels[index];
        }

        for (int i = 0; i < restsOn.Count; i++)
        {
            Level(i);
        }

        return levels;
    }

    /// <summary>
    /// Semua jalur dari kardus di lantai sampai kardus teratas. Kardus yang dipakai bersama muncul di setiap
    /// jalur yang melewatinya. Jalur diurutkan dengan <paramref name="sortKey"/> (mis. CenterX) di setiap
    /// percabangan; berhenti di <paramref name="maxPaths"/> dan menandai <c>Truncated</c>.
    /// </summary>
    public static StackPaths Paths(IReadOnlyList<IReadOnlyList<int>> restsOn, Func<int, double> sortKey, int maxPaths)
    {
        ArgumentNullException.ThrowIfNull(restsOn);
        ArgumentNullException.ThrowIfNull(sortKey);
        List<int>[] above = [.. Enumerable.Range(0, restsOn.Count).Select(_ => new List<int>())];
        for (int i = 0; i < restsOn.Count; i++)
        {
            foreach (int below in restsOn[i])
            {
                above[below].Add(i);
            }
        }

        List<IReadOnlyList<int>> paths = [];
        bool truncated = false;
        List<int> current = [];

        void Walk(int index)
        {
            if (truncated)
            {
                return;
            }

            current.Add(index);
            if (above[index].Count == 0)
            {
                if (paths.Count < maxPaths)
                {
                    paths.Add([.. current]);
                }
                else
                {
                    truncated = true;
                }
            }
            else
            {
                foreach (int next in above[index].OrderBy(sortKey))
                {
                    Walk(next);
                }
            }

            current.RemoveAt(current.Count - 1);
        }

        foreach (int root in Enumerable.Range(0, restsOn.Count).Where(i => restsOn[i].Count == 0).OrderBy(sortKey))
        {
            Walk(root);
        }

        return new StackPaths(paths, truncated);
    }
}
