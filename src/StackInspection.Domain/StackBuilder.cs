namespace StackInspection.Domain;

/// <summary>Hasil penyusunan kolom tumpukan.</summary>
public sealed record StackAssignment(
    IReadOnlyList<int> Columns,
    IReadOnlyList<int> Rows,
    IReadOnlyList<IReadOnlyList<int>> BottomToTopPerColumn,
    bool IsIrregular);

/// <summary>
/// Menyusun kolom secara fisik dari bawah ke atas: setiap kardus ditempelkan ke kolom yang kardus
/// teratasnya (di baris bawahnya) paling banyak tumpang tindih secara horizontal. Dengan cara ini kardus
/// menyamping yang sempit dan kardus depan yang lebar tidak tercampur ke satu kolom seperti pada
/// pengelompokan berdasarkan lebar rata-rata.
/// </summary>
public static class StackBuilder
{
    /// <summary>Minimal tumpang tindih horizontal (terhadap kardus yang lebih sempit) agar dianggap satu tumpukan.</summary>
    public const double MinOverlapRatio = 0.5;

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

        foreach (IGrouping<int, int> row in Enumerable.Range(0, boxes.Count).GroupBy(i => rows[i]).OrderByDescending(g => g.Key))
        {
            // Pasangan (kardus, tumpukan) diurutkan dari tumpang tindih terbesar; satu tumpukan hanya
            // menerima satu kardus per baris.
            List<(int Box, Stack Stack, double Overlap)> candidates = [];
            foreach (int index in row)
            {
                BoundingBox box = boxes[index];
                int matches = 0;
                foreach (Stack stack in stacks)
                {
                    double overlap = HorizontalOverlap(box, stack.Top);
                    if (overlap >= MinOverlapRatio && stack.Top.CenterY > box.CenterY)
                    {
                        candidates.Add((index, stack, overlap));
                        matches++;
                    }
                }

                if (matches > 1)
                {
                    irregular = true; // satu kardus menumpu di atas dua tumpukan
                }
            }

            HashSet<int> assigned = [];
            HashSet<Stack> used = [];
            List<(int Box, Stack Stack)> placements = [];
            foreach ((int box, Stack stack, _) in candidates.OrderByDescending(c => c.Overlap).ThenBy(c => boxes[c.Box].X1))
            {
                if (assigned.Contains(box) || used.Contains(stack))
                {
                    continue;
                }

                assigned.Add(box);
                used.Add(stack);
                placements.Add((box, stack));
            }

            foreach ((int box, Stack stack) in placements)
            {
                stack.Members.Add(box);
                stack.Top = boxes[box];
            }

            foreach (int index in row.Where(i => !assigned.Contains(i)).OrderBy(i => boxes[i].X1))
            {
                if (candidates.Any(c => c.Box == index))
                {
                    irregular = true; // kalah berebut tumpukan dengan kardus lain di baris yang sama
                }

                Stack created = new() { Top = boxes[index] };
                created.Members.Add(index);
                stacks.Add(created);
            }
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

    /// <summary>Panjang tumpang tindih horizontal dibagi lebar kardus yang lebih sempit.</summary>
    public static double HorizontalOverlap(BoundingBox a, BoundingBox b)
    {
        double overlap = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
        double narrower = Math.Min(a.Width, b.Width);
        return narrower <= 0 ? 0 : Math.Max(0, overlap) / narrower;
    }
}
