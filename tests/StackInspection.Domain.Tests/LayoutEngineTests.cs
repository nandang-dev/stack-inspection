using StackInspection.Domain;

namespace StackInspection.Domain.Tests;

/// <summary>
/// Dua layout simulasi dari diskusi (brief/PLAN-stack-layout-engine.md §8). Nomor kardus = nomor di gambar;
/// koordinat diperkirakan dari gambar (±5 px).
/// </summary>
public class LayoutEngineTests
{
    private static Dictionary<int, BoundingBox> Layout1()
    {
        Dictionary<int, BoundingBox> boxes = [];
        void Column(double x1, double x2, params (int No, double Y1, double Y2)[] cartons)
        {
            foreach ((int no, double y1, double y2) in cartons)
            {
                boxes[no] = new BoundingBox(x1, y1, x2, y2);
            }
        }

        Column(30, 153, (2, 53, 90), (3, 96, 133), (6, 184, 221), (7, 228, 265), (8, 272, 309), (9, 315, 352), (10, 358, 395), (11, 401, 438));
        boxes[4] = new BoundingBox(32, 139, 90, 176);
        boxes[5] = new BoundingBox(94, 139, 153, 176);
        Column(163, 285, (12, 53, 139), (13, 147, 233), (14, 238, 308), (15, 316, 352), (16, 358, 396), (17, 401, 438));
        Column(296, 419, (18, 73, 111), (19, 119, 156), (20, 164, 201), (21, 207, 245), (22, 250, 288), (23, 294, 364), (24, 373, 442));
        Column(430, 552, (25, 56, 93), (26, 98, 136), (27, 142, 179), (28, 186, 223), (29, 230, 268), (30, 273, 311), (31, 317, 354), (32, 360, 397), (33, 402, 439));
        Column(563, 687, (34, 54, 92), (35, 100, 138), (36, 146, 183), (37, 190, 261), (38, 275, 345), (39, 358, 396), (40, 401, 438));
        return boxes;
    }

    private static Dictionary<int, BoundingBox> Layout2()
    {
        Dictionary<int, BoundingBox> boxes = [];
        void Column(double x1, double x2, params (int No, double Y1, double Y2)[] cartons)
        {
            foreach ((int no, double y1, double y2) in cartons)
            {
                boxes[no] = new BoundingBox(x1, y1, x2, y2);
            }
        }

        Column(43, 165, (2, 38, 76), (3, 81, 119), (6, 170, 208), (7, 213, 251), (8, 257, 295), (9, 300, 338), (10, 343, 381), (11, 386, 424));
        boxes[4] = new BoundingBox(44, 124, 102, 162);
        boxes[5] = new BoundingBox(106, 124, 165, 162);
        Column(175, 298, (12, 38, 124), (13, 132, 218), (14, 224, 294), (15, 301, 339));
        boxes[24] = new BoundingBox(175, 357, 432, 425); // menjembatani tumpukan B dan C
        Column(309, 432, (18, 59, 97), (19, 104, 142), (20, 150, 188), (21, 193, 231), (22, 236, 274), (23, 280, 349));
        Column(443, 565, (25, 41, 79), (26, 84, 122), (28, 172, 211), (29, 216, 254), (30, 259, 297), (31, 302, 340), (32, 345, 383), (33, 388, 426));
        boxes[27] = new BoundingBox(443, 127, 697, 165); // menjembatani tumpukan D dan E
        Column(576, 699, (34, 40, 78), (35, 85, 123), (37, 176, 244), (38, 261, 330), (39, 344, 382), (40, 387, 425));
        return boxes;
    }

    /// <summary>Hasil layout dengan nomor kardus gambar (bukan indeks).</summary>
    private sealed record Result(GridLayout Layout, int[] Numbers)
    {
        public CartonPlacement this[int no] => Layout.Cartons[Array.IndexOf(Numbers, no)];

        public int[] RestsOn(int no) => [.. this[no].RestsOn.Select(i => Numbers[i]).Order()];

        public int[] Columns(int no) => [.. this[no].Cells.Select(c => c.Column).Distinct().Order()];

        public List<int[]> Paths => [.. Layout.Paths.BottomToTop.Select(p => p.Select(i => Numbers[i]).ToArray())];
    }

    private static Result Build(Dictionary<int, BoundingBox> boxes)
    {
        int[] numbers = [.. boxes.Keys];
        return new Result(GridBuilder.Build([.. numbers.Select(n => boxes[n])]), numbers);
    }

    [Fact]
    public void Layout1_SideBySideCartons_RestOnSameCartonAndShareTheCartonAbove()
    {
        Result r = Build(Layout1());

        Assert.Equal([6], r.RestsOn(4));
        Assert.Equal([6], r.RestsOn(5));
        Assert.Equal([4, 5], r.RestsOn(3));
        Assert.Equal((6, 6, 7, 8), (r[4].Level, r[5].Level, r[3].Level, r[2].Level));
        Assert.True(r.Layout.IsIrregular);
    }

    [Fact]
    public void Layout1_DisplayGrid_SplitsColumnAIntoTwoSubColumns()
    {
        Result r = Build(Layout1());

        Assert.Equal(6, r.Layout.ColumnCount);
        Assert.Equal([0], r.Columns(4));
        Assert.Equal([1], r.Columns(5));
        Assert.Equal([0, 1], r.Columns(2)); // kardus kolom A lainnya menempati 2 sub-kolom
        Assert.Equal([2], r.Columns(12));
        Assert.Equal([5], r.Columns(40));
        Assert.True(r[12].Cells.Count > 1); // kardus tinggi menempati beberapa baris
    }

    [Fact]
    public void Layout1_PathsFollowBothSideBySideCartons()
    {
        Result r = Build(Layout1());

        Assert.Equal(6, r.Paths.Count);
        Assert.Equal([11, 10, 9, 8, 7, 6, 4, 3, 2], r.Paths[0]);
        Assert.Equal([11, 10, 9, 8, 7, 6, 5, 3, 2], r.Paths[1]);
        Assert.False(r.Layout.Paths.Truncated);
    }

    [Fact]
    public void Layout2_BridgingCartons_SupportBothStacks()
    {
        Result r = Build(Layout2());

        Assert.Empty(r.RestsOn(24));
        Assert.Equal([24], r.RestsOn(15));
        Assert.Equal([24], r.RestsOn(23));
        Assert.Equal([28, 37], r.RestsOn(27));
        Assert.Equal([27], r.RestsOn(26));
        Assert.Equal([27], r.RestsOn(35));
        Assert.Equal((5, 3, 6, 7, 8), (r[28].Level, r[37].Level, r[27].Level, r[35].Level, r[34].Level));
        Assert.Equal((1, 6), (r[23].Level, r[18].Level));
    }

    [Fact]
    public void Layout2_DisplayGrid_BridgingCartonsSpanTwoColumns()
    {
        Result r = Build(Layout2());

        Assert.Equal(6, r.Layout.ColumnCount);
        Assert.Equal([2, 3], r.Columns(24));
        Assert.Equal([4, 5], r.Columns(27));
        Assert.Equal([3], r.Columns(23));
        Assert.Equal([4], r.Columns(26));
        Assert.Equal([5], r.Columns(35));
    }

    [Fact]
    public void Layout2_EveryGridCellHasAtMostOneCarton()
    {
        Result r = Build(Layout2());

        GridCell[] all = [.. r.Layout.Cartons.SelectMany(c => c.Cells)];
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.All(r.Layout.Cartons, c => Assert.NotEmpty(c.Cells));
    }

    [Fact]
    public void Layout2_PathsGoThroughBridgingCartons()
    {
        Result r = Build(Layout2());

        Assert.Equal(8, r.Paths.Count);
        Assert.Contains([24, 15, 14, 13, 12], r.Paths);
        Assert.Contains([24, 23, 22, 21, 20, 19, 18], r.Paths);
        Assert.Contains([40, 39, 38, 37, 27, 35, 34], r.Paths);
        Assert.Contains([33, 32, 31, 30, 29, 28, 27, 26, 25], r.Paths);
    }

    /// <summary>Nomor kardus yang dibuang filter lapisan depan (nilai appsettings: 0.6 / 0.35 / 0.55).</summary>
    private static int[] Excluded(Dictionary<int, BoundingBox> boxes)
    {
        int[] numbers = [.. boxes.Keys];
        CartonBox[] cartons = [.. numbers.Select(n => new CartonBox(boxes[n], 0.9))];
        FrontLayerResult result = FrontLayerFilter.Apply(cartons, 0.6, 0.35, 0.55);
        return [.. numbers.Where((_, i) => !result.Kept.Contains(cartons[i])).Order()];
    }

    [Fact]
    public void Layout1_SideBySideCartons_AreNotBackLayer()
    {
        Assert.Empty(Excluded(Layout1()));
    }

    [Fact]
    public void Layout2_CartonsOnBridgingCartons_AreNotBackLayer()
    {
        int[] excluded = Excluded(Layout2());

        Assert.DoesNotContain(excluded, n => n is 2 or 3 or 4 or 5);
        Assert.DoesNotContain(excluded, n => n is >= 12 and <= 15);
        Assert.DoesNotContain(excluded, n => n is 25 or 26 or 34 or 35);
    }

    [Fact]
    public void Paths_AreTruncatedAtLimit()
    {
        Result r = Build(Layout2());

        StackPaths paths = SupportGraph.Paths(
            [.. r.Layout.Cartons.Select(c => c.RestsOn)], i => i, maxPaths: 3);

        Assert.Equal(3, paths.BottomToTop.Count);
        Assert.True(paths.Truncated);
    }
}

public class SupportGraphScoringTests
{
    private const string Light = "11111111"; // kelas A
    private const string Heavy = "33333333"; // kelas C

    private static readonly Dictionary<string, MasterSku> Master = new()
    {
        [Light] = new(Light, "Ringan", 2, 1, SkuClass.A),
        [Heavy] = new(Heavy, "Berat", 10, 9, SkuClass.C),
    };

    private static DetectedCell Cell(int id, int level, string sku, params int[] restsOn) =>
        new(0, id, level, sku, sku, 0.9, 0.9, ReadStatus.Matched, new BoundingBox(id * 10, 0, (id * 10) + 9, 9), id, restsOn);

    [Fact]
    public void HeavyCartonOnBridgingLightCarton_IsClassViolation()
    {
        // id 0 = dus lebar ringan di lantai, id 1 & 2 menumpu di atasnya; id 2 berat
        DetectedCell[] cells = [Cell(0, 0, Light), Cell(1, 1, Light, 0), Cell(2, 1, Heavy, 0)];

        StackScore score = StackScoringService.CreateDefault().Score(cells, Master);

        Violation violation = Assert.Single(score.Violations);
        Assert.Equal((ViolationType.ClassPositionInvalid, 2), (violation.Type, violation.Cell.Id));
    }

    [Fact]
    public void CartonOnTwoSupports_SeesViolationOnEitherSide()
    {
        // id 2 berat menumpu di id 0 (berat) dan id 1 (ringan) → tetap melanggar karena id 1
        DetectedCell[] cells = [Cell(0, 0, Heavy), Cell(1, 0, Light), Cell(2, 1, Heavy, 0, 1)];

        StackScore score = StackScoringService.CreateDefault().Score(cells, Master);

        Assert.Equal(2, Assert.Single(score.Violations).Cell.Id);
    }

    [Fact]
    public void MaxStack_IsCountedPerPath()
    {
        // dua jalur: 0 → 1 → 3 dan 0 → 2 → 3; Light maxStack 2, tiap jalur berisi 3 Light → id 3 melanggar sekali
        DetectedCell[] cells = [Cell(0, 0, Light), Cell(1, 1, Light, 0), Cell(2, 1, Heavy, 0), Cell(3, 2, Light, 1, 2)];

        StackScore score = StackScoringService.CreateDefault().Score(cells, Master);

        Violation[] maxStack = [.. score.Violations.Where(v => v.Type == ViolationType.MaxStackExceeded)];
        Assert.Equal(3, Assert.Single(maxStack).Cell.Id);
        Assert.Equal(4, score.MaxPossibleScore);
    }
}

public class SupportGraphFallbackTests
{
    [Fact]
    public void OneMissingCartonBetween_StillRestsOnCartonBelowIt()
    {
        // celah 80 px = satu kardus (tinggi 80) tidak terdeteksi
        BoundingBox[] boxes = [new(0, 160, 100, 240), new(0, 0, 100, 80)];

        Assert.Equal([0], SupportGraph.RestsOn(boxes)[1]);
    }

    [Fact]
    public void BoxFarBelow_IsNotASupport()
    {
        // kotak di tanah jauh di bawah bak truk (celah 200 px = 2,5 × tinggi)
        BoundingBox[] boxes = [new(0, 300, 100, 380), new(0, 20, 100, 100)];

        Assert.Empty(SupportGraph.RestsOn(boxes)[1]);
    }
}
