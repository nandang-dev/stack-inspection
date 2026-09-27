namespace StackInspection.Domain;

/// <summary>Hasil penyusunan kolom tumpukan.</summary>
public sealed record StackAssignment(
    IReadOnlyList<int> Columns,
    IReadOnlyList<int> Rows,
    IReadOnlyList<IReadOnlyList<int>> BottomToTopPerColumn,
    bool IsIrregular);

/// <summary>
/// Menyusun kolom secara fisik dari bawah ke atas: setiap kardus ditempelkan ke tumpukan yang kardus
/// teratasnya berada di bawahnya dan paling banyak tumpang tindih secara horizontal. Tumpukan yang kardus
/// teratasnya tepat di bawah (tanpa celah) diutamakan; tumpukan bercelah hanya dipakai jika tidak ada,
/// sehingga filter lapisan belakang tetap bisa mendeteksi celah. Dengan cara ini kardus menyamping yang
/// sempit dan kardus depan yang lebar tidak tercampur ke satu kolom seperti pada pengelompokan
/// berdasarkan lebar rata-rata.
/// </summary>
public static class StackBuilder
{
    /// <summary>Minimal tumpang tindih horizontal (terhadap kardus yang lebih sempit) agar dianggap satu tumpukan.</summary>
    public const double MinOverlapRatio = 0.5;

    /// <summary>Kardus dianggap tepat di atas jika celahnya ≤ rasio ini × tinggi kardus di bawah.</summary>
    public const double AdjacentGapRatio = 0.35;

    /// <summary>Tumpang tindih vertikal maksimal (× tinggi kardus yang lebih pendek) agar masih dianggap di atas.</summary>
    public const double MaxVerticalOverlapRatio = 0.5;

    private sealed class Stack
    {
        public List<int> Members { get; } = [];

        public BoundingBox Top { get; set; }
    }

    public static StackAssignment Assign(IReadOnlyList<BoundingBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (boxes.Count == 0)
        {
            return new StackAssignment([], [], [], false);
        }

        double meanHeight = boxes.Average(b => b.Height);
        int[] rows = GridBuilder.Cluster(boxes.Select(b => b.CenterY).ToArray(), GridBuilder.RowToleranceRatio * meanHeight);
        List<Stack> stacks = [];
        bool irregular = false;

        foreach (int index in Enumerable.Range(0, boxes.Count).OrderByDescending(i => boxes[i].CenterY).ThenBy(i => boxes[i].X1))
        {
            BoundingBox box = boxes[index];
            List<(Stack Stack, double Overlap, bool Adjacent)> candidates = [];
            foreach (Stack stack in stacks)
            {
                BoundingBox top = stack.Top;
                double overlap = HorizontalOverlap(box, top);
                if (overlap < MinOverlapRatio || !IsAbove(box, top))
                {
                    continue;
                }

                candidates.Add((stack, overlap, top.Y1 - box.Y2 <= AdjacentGapRatio * top.Height));
            }

            List<(Stack Stack, double Overlap, bool Adjacent)> adjacent = [.. candidates.Where(c => c.Adjacent)];
            if (adjacent.Count > 1)
            {
                irregular = true; // satu kardus menumpu di atas dua tumpukan
            }

            List<(Stack Stack, double Overlap, bool Adjacent)> pool = adjacent.Count > 0 ? adjacent : candidates;
            if (pool.Count == 0)
            {
                Stack created = new() { Top = box };
                created.Members.Add(index);
                stacks.Add(created);
                continue;
            }

            Stack chosen = pool.OrderByDescending(c => c.Overlap).First().Stack;
            chosen.Members.Add(index);
            chosen.Top = box;
        }

        List<Stack> ordered = [.. stacks.OrderBy(s => s.Members.Average(i => boxes[i].CenterX))];
        int[] columns = new int[boxes.Count];
        List<IReadOnlyList<int>> perColumn = [];
        for (int c = 0; c < ordered.Count; c++)
        {
            foreach (int member in ordered[c].Members)
            {
                columns[member] = c;
            }

            perColumn.Add(ordered[c].Members);
        }

        return new StackAssignment(columns, rows, perColumn, irregular);
    }

    /// <summary><paramref name="box"/> berada di atas <paramref name="below"/> (tumpang tindih vertikal kecil masih boleh).</summary>
    private static bool IsAbove(BoundingBox box, BoundingBox below) =>
        box.CenterY < below.CenterY
        && box.Y2 - below.Y1 <= MaxVerticalOverlapRatio * Math.Min(box.Height, below.Height);

    /// <summary>Panjang tumpang tindih horizontal dibagi lebar kardus yang lebih sempit.</summary>
    public static double HorizontalOverlap(BoundingBox a, BoundingBox b)
    {
        double overlap = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
        double narrower = Math.Min(a.Width, b.Width);
        return narrower <= 0 ? 0 : Math.Max(0, overlap) / narrower;
    }
}
