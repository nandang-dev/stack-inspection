using StackInspection.Domain;

namespace StackInspection.Domain.Tests;

/// <summary>
/// Parameter skenario layout sintetis. Rasio terhadap ukuran dus standar (120 × 80 px). Rentang default
/// dikalibrasi dari golden-v3 (42 foto, 1.673 kotak): lebar p5–p95 0,52–1,54× median, celah vertikal p5–p95
/// −11,5 %…+16,6 % tinggi dus bawah, geser horizontal median 5 % / p95 36 % lebar, berdampingan 6,8 %,
/// menjembatani 6,6 %.
/// </summary>
internal sealed record ScenarioOptions(
    string Name,
    int Columns = 5,
    int Levels = 8,
    double SizeSpread = 0.05,
    double TallProbability = 0,
    double SideBySideProbability = 0,
    double BridgeProbability = 0,
    double Shift = 0.03,
    double GapMin = -0.03,
    double GapMax = 0.03,
    double EdgeJitter = 0,
    double Perspective = 0,
    double MissingProbability = 0);

/// <summary>Satu layout sintetis beserta jawaban benar relasi tumpuan dan level.</summary>
internal sealed record Scenario(BoundingBox[] Boxes, int[][] RestsOn, int[] Levels);

/// <summary>Hasil evaluasi <see cref="GridBuilder"/> terhadap jawaban benar.</summary>
internal sealed record ScenarioScore(double SupportAccuracy, double LevelAccuracy, double GridOrderAccuracy, int Cartons);

/// <summary>
/// Membangun tumpukan dus secara fisik dari lantai ke atas (kolom per kolom, level demi level), sehingga
/// relasi tumpuan diketahui pasti; lalu memberi gangguan geometri (perspektif, jitter tepi, dus hilang).
/// </summary>
internal static class LayoutScenarios
{
    private const double Width = 120;
    private const double Height = 80;
    private const double ColumnGap = 8;
    private const double Floor = 2000;

    public static readonly ScenarioOptions[] All =
    [
        new("rapi"),
        new("ukuran-campur", SizeSpread: 0.25, TallProbability: 0.1),
        new("berdampingan", SideBySideProbability: 0.1),
        new("jembatan", BridgeProbability: 0.1),
        new("geser", Shift: 0.3),
        new("celah-golden", GapMin: -0.115, GapMax: 0.166),
        new("perspektif", Perspective: 0.15, Shift: 0.1),
        new("jitter-deteksi", EdgeJitter: 0.03),
        new("dus-hilang", MissingProbability: 0.05),
        new(
            "campuran-golden",
            SizeSpread: 0.2,
            TallProbability: 0.05,
            SideBySideProbability: 0.07,
            BridgeProbability: 0.07,
            Shift: 0.13,
            GapMin: -0.115,
            GapMax: 0.166,
            EdgeJitter: 0.02),
    ];

    /// <summary>Di luar rentang golden-v3: untuk mencari titik algoritma mulai salah (hanya dilaporkan).</summary>
    public static readonly ScenarioOptions[] Stress =
    [
        new("jitter-6%", EdgeJitter: 0.06),
        new("jitter-10%", EdgeJitter: 0.10),
        new("geser-45%", Shift: 0.45),
        new("perspektif-30%", Perspective: 0.3, Shift: 0.1),
        new("celah-30%", GapMin: -0.2, GapMax: 0.3),
        new("dus-hilang-20%", MissingProbability: 0.2),
        new(
            "campuran-berat",
            SizeSpread: 0.3,
            TallProbability: 0.1,
            SideBySideProbability: 0.15,
            BridgeProbability: 0.15,
            Shift: 0.3,
            GapMin: -0.15,
            GapMax: 0.25,
            EdgeJitter: 0.05,
            Perspective: 0.2,
            MissingProbability: 0.1),
    ];

    public static Scenario Generate(ScenarioOptions o, int seed)
    {
        Random random = new(seed);
        double Uniform(double min, double max) => min + (random.NextDouble() * (max - min));

        List<BoundingBox> boxes = [];
        List<int[]> restsOn = [];
        double[] baseWidth = [.. Enumerable.Range(0, o.Columns).Select(_ => Width * Uniform(1 - o.SizeSpread, 1 + o.SizeSpread))];
        // slot kolom cukup lebar untuk dus terlebar + ruang geser, agar dus dua kolom tidak saling tumpang tindih
        double pitch = Pitch(o);
        double[] centerX = [.. Enumerable.Range(0, o.Columns).Select(c => 50 + (c * pitch) + (pitch / 2))];
        int[][] tops = [.. Enumerable.Range(0, o.Columns).Select(_ => Array.Empty<int>())];
        double[] topY = [.. Enumerable.Repeat(Floor, o.Columns)];

        int Add(BoundingBox box, int[] supports)
        {
            boxes.Add(box);
            restsOn.Add(supports);
            return boxes.Count - 1;
        }

        double LowerHeight(int column) => tops[column].Length == 0 ? Height : tops[column].Max(i => boxes[i].Height);

        // jawaban benar = geometri bersih (sebelum gangguan): dus di bawah yang tertutup ≥ 50 % lebar dus yang
        // lebih sempit; jika tidak ada, dus dengan tumpang tindih terbesar (secara fisik pasti menumpu sesuatu)
        int[] Supports(BoundingBox box, int[] candidates)
        {
            int[] covered = [.. candidates.Where(i => StackBuilder.HorizontalOverlap(box, boxes[i]) >= StackBuilder.MinOverlapRatio).Order()];
            return covered.Length > 0 || candidates.Length == 0
                ? covered
                : [candidates.MaxBy(i => StackBuilder.HorizontalOverlap(box, boxes[i]))];
        }

        double TopCenter(int column) => tops[column].Length == 0 ? centerX[column] : tops[column].Average(i => boxes[i].CenterX);

        for (int level = 0; level < o.Levels; level++)
        {
            int column = 0;
            while (column < o.Columns)
            {
                double height = Height * Uniform(1 - o.SizeSpread, 1 + o.SizeSpread);
                if (random.NextDouble() < o.TallProbability)
                {
                    height *= 2;
                }

                double gap = Uniform(o.GapMin, o.GapMax);
                bool canBridge = column + 1 < o.Columns
                    && tops[column].Length > 0
                    && tops[column + 1].Length > 0
                    && !tops[column].SequenceEqual(tops[column + 1])
                    && tops[column].Concat(tops[column + 1]).All(i => boxes[i].Width < 1.5 * Width) // tidak di atas jembatan lain
                    && Math.Abs(topY[column] - topY[column + 1]) < 0.08 * Height;
                if (canBridge && random.NextDouble() < o.BridgeProbability)
                {
                    // dus lebar menjembatani dua tumpukan yang tingginya hampir sama
                    double x1 = tops[column].Min(i => boxes[i].X1);
                    double x2 = tops[column + 1].Max(i => boxes[i].X2);
                    double y2 = Math.Min(topY[column], topY[column + 1]) - (gap * Math.Min(LowerHeight(column), LowerHeight(column + 1)));
                    BoundingBox bridgeBox = new(x1, y2 - height, x2, y2);
                    int bridge = Add(bridgeBox, Supports(bridgeBox, [.. tops[column], .. tops[column + 1]]));
                    tops[column] = tops[column + 1] = [bridge];
                    topY[column] = topY[column + 1] = y2 - height;
                    column += 2;
                    continue;
                }

                if (column > 0 && tops[column].Length > 0 && tops[column].SequenceEqual(tops[column - 1]))
                {
                    // kolom ini masih tertutup dus jembatan dari kolom kiri; dus berikutnya ditaruh dari kolom kiri
                    column++;
                    continue;
                }

                double bottom = topY[column] - (gap * LowerHeight(column));
                double width = baseWidth[column] * Uniform(1 - o.SizeSpread, 1 + o.SizeSpread);
                // geser relatif terhadap dus di bawahnya (golden: median 5 %, p95 36 %), tetap dekat kolomnya
                double cx = TopCenter(column) + (Uniform(-1, 1) * o.Shift * baseWidth[column]);
                if (tops[column].Length == 1 && boxes[tops[column][0]].Width > 1.5 * baseWidth[column])
                {
                    cx = centerX[column] + (Uniform(-1, 1) * o.Shift * baseWidth[column]); // di atas dus jembatan
                }

                double room = Math.Max(0, ((pitch - ColumnGap) / 2) - (width / 2));
                cx = Math.Clamp(cx, centerX[column] - room, centerX[column] + room);

                if (random.NextDouble() < o.SideBySideProbability)
                {
                    double half = (width / 2) - 2;
                    BoundingBox leftBox = new(cx - (width / 2), bottom - height, cx - (width / 2) + half, bottom);
                    BoundingBox rightBox = new(cx + (width / 2) - half, bottom - height, cx + (width / 2), bottom);
                    int left = Add(leftBox, Supports(leftBox, tops[column]));
                    int right = Add(rightBox, Supports(rightBox, tops[column]));
                    tops[column] = [left, right];
                }
                else
                {
                    BoundingBox box = new(cx - (width / 2), bottom - height, cx + (width / 2), bottom);
                    tops[column] = [Add(box, Supports(box, tops[column]))];
                }

                topY[column] = bottom - height;
                column++;
            }
        }

        BoundingBox[] noisy = [.. boxes.Select(b => Distort(b, o, random, topY.Min()))];
        return RemoveMissing(noisy, [.. restsOn], o.MissingProbability, random);
    }

    private static double Pitch(ScenarioOptions o) =>
        (Width * (1 + o.SizeSpread) * (1 + o.SizeSpread)) + ColumnGap + (2 * o.Shift * Width);

    /// <summary>Perspektif (dus makin atas makin kecil ke arah tengah) lalu jitter tepi kotak.</summary>
    private static BoundingBox Distort(BoundingBox box, ScenarioOptions o, Random random, double highest)
    {
        double Normal() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

        double centerX = 50 + (o.Columns * Pitch(o) / 2);
        double Scale(double y) => 1 - (o.Perspective * (Floor - y) / Math.Max(1, Floor - highest));
        double sx = Scale(box.CenterY);
        BoundingBox scaled = new(
            centerX + ((box.X1 - centerX) * sx),
            Floor - ((Floor - box.Y1) * Scale(box.Y1)),
            centerX + ((box.X2 - centerX) * sx),
            Floor - ((Floor - box.Y2) * Scale(box.Y2)));
        double jx = o.EdgeJitter * scaled.Width;
        double jy = o.EdgeJitter * scaled.Height;
        return new BoundingBox(
            scaled.X1 + (Normal() * jx),
            scaled.Y1 + (Normal() * jy),
            scaled.X2 + (Normal() * jx),
            scaled.Y2 + (Normal() * jy));
    }

    /// <summary>Dus tidak terdeteksi: yang menumpu padanya dianggap menumpu tumpuannya (jawaban benar ikut disesuaikan).</summary>
    private static Scenario RemoveMissing(BoundingBox[] boxes, int[][] restsOn, double probability, Random random)
    {
        bool[] missing = [.. boxes.Select(_ => random.NextDouble() < probability)];
        IEnumerable<int> Visible(int index) => missing[index] ? restsOn[index].SelectMany(Visible) : [index];

        int[] keep = [.. Enumerable.Range(0, boxes.Length).Where(i => !missing[i])];
        Dictionary<int, int> map = keep.Select((old, i) => (old, i)).ToDictionary(x => x.old, x => x.i);
        int[][] truth = [.. keep.Select(i => restsOn[i].SelectMany(Visible).Distinct().Select(s => map[s]).Order().ToArray())];
        return new Scenario([.. keep.Select(i => boxes[i])], truth, SupportGraph.Levels(truth));
    }

    public static ScenarioScore Evaluate(Scenario scenario)
    {
        GridLayout layout = GridBuilder.Build(scenario.Boxes);
        int n = scenario.Boxes.Length;
        int supportOk = Enumerable.Range(0, n).Count(i => layout.Cartons[i].RestsOn.SequenceEqual(scenario.RestsOn[i]));
        int levelOk = Enumerable.Range(0, n).Count(i => layout.Cartons[i].Level == scenario.Levels[i]);

        // urutan grid: dus yang jelas di kiri / di atas dus lain (dan sejajar) harus berada di kolom / baris lebih kecil
        int pairs = 0;
        int orderOk = 0;
        for (int a = 0; a < n; a++)
        {
            for (int b = 0; b < n; b++)
            {
                BoundingBox p = scenario.Boxes[a];
                BoundingBox q = scenario.Boxes[b];
                double verticalOverlap = Math.Min(p.Y2, q.Y2) - Math.Max(p.Y1, q.Y1);
                double horizontalOverlap = Math.Min(p.X2, q.X2) - Math.Max(p.X1, q.X1);
                if (verticalOverlap > 0.5 * Math.Min(p.Height, q.Height) && p.CenterX < q.CenterX && horizontalOverlap < 0.1 * Math.Min(p.Width, q.Width))
                {
                    pairs++;
                    orderOk += layout.Cartons[a].Cells.Max(c => c.Column) < layout.Cartons[b].Cells.Min(c => c.Column) ? 1 : 0;
                }

                if (horizontalOverlap > 0.5 * Math.Min(p.Width, q.Width) && p.CenterY < q.CenterY && verticalOverlap < 0.1 * Math.Min(p.Height, q.Height))
                {
                    pairs++;
                    orderOk += layout.Cartons[a].Cells.Max(c => c.Row) < layout.Cartons[b].Cells.Min(c => c.Row) ? 1 : 0;
                }
            }
        }

        return new ScenarioScore(
            (double)supportOk / n, (double)levelOk / n, pairs == 0 ? 1 : (double)orderOk / pairs, n);
    }
}
