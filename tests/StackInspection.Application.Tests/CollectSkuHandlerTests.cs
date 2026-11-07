using StackInspection.Application.Abstractions;
using StackInspection.Application.CollectSku;
using StackInspection.Application.Configuration;
using StackInspection.Application.Contracts;
using StackInspection.Application.Exceptions;
using StackInspection.Domain;

namespace StackInspection.Application.Tests;

public class CollectSkuHandlerTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static (List<CartonBox> Cartons, Dictionary<BoundingBox, (string?, string?)> Texts) Grid(int rows, int columns, string sku)
    {
        List<CartonBox> cartons = [];
        Dictionary<BoundingBox, (string?, string?)> texts = [];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                CartonBox carton = Build.Carton(300 + (c * 410), 600 + (r * 310));
                cartons.Add(carton);
                texts[carton.Box] = (sku, null);
            }
        }

        return (cartons, texts);
    }

    [Fact]
    public async Task ValidPhoto_ReturnsGridAndDistinctSkus()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(3, 2, "68140913");
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts));

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Equal((6, 3, 2), (result.Cells.Count, result.RowCount, result.ColumnCount));
        Assert.All(result.Cells, c => Assert.Equal(("68140913", ReadStatus.Matched), (c.Sku, c.ReadStatus)));
        DistinctSkuDto distinct = Assert.Single(result.DistinctSkus);
        Assert.Equal(6, distinct.Count);
        Assert.Equal([0, 1, 2], result.Stacks[0].Cartons.Select(c => c.Level));
        // objek kardus di stacks = entri cells yang sama
        Assert.Equal(
            result.Cells.Where(c => c.Column == result.Stacks[0].Column).OrderByDescending(c => c.Row),
            result.Stacks[0].Cartons);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ValidPhoto_ReturnsTimingsThatSumToProcessingTime()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(2, 2, "68140913");
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts));

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        CollectSkuTimingsDto t = result.Timings;
        long[] stages = [t.DecodeMs, t.InspectMs, t.DetectMs, t.OcrMs, t.SkuResolveMs, t.BuildMs];
        Assert.All(stages, ms => Assert.True(ms >= 0));
        Assert.Equal(result.ProcessingTimeMs, stages.Sum());
    }

    [Fact]
    public async Task WideCartonOverTwoStacks_AppearsInTwoCellsWithSameId()
    {
        // dua kardus di lantai, satu kardus lebar di atas keduanya
        CartonBox left = Build.Carton(300, 1000);
        CartonBox right = Build.Carton(710, 1000);
        CartonBox wide = Build.Carton(300, 690, w: 810);
        Dictionary<BoundingBox, (string?, string?)> texts = new()
        {
            [left.Box] = ("68140913", null), [right.Box] = ("68140913", null), [wide.Box] = ("68410975", null),
        };
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector([left, right, wide]), new StubReader(texts));

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Equal((4, 2, 2), (result.Cells.Count, result.RowCount, result.ColumnCount));
        CellDto[] top = [.. result.Cells.Where(c => c.Row == 0)];
        Assert.Equal(2, top.Length);
        Assert.Single(top.Select(c => c.Id).Distinct());
        Assert.Equal([0, 1], top.Select(c => c.Column));
        Assert.All(top, c => Assert.Equal((1, "68410975"), (c.Level, c.Sku)));
        int[] bottomIds = [.. result.Cells.Where(c => c.Row == 1).Select(c => c.Id!.Value)];
        Assert.All(top, c => Assert.Equal(bottomIds.Order(), c.RestsOn!));
        Assert.Equal([("68140913", 2), ("68410975", 1)], result.DistinctSkus.Select(d => (d.Sku, d.Count)));
        Assert.Equal(2, result.Stacks.Count);
        Assert.All(result.Stacks, s => Assert.Same(top[0], s.Cartons[^1]));
        Assert.Contains(CollectWarnings.IrregularGrid, result.Warnings);
    }

    [Theory]
    [InlineData(1600, 1200)] // sisi terpanjang < 2048
    [InlineData(2048, 900)] // sisi terpanjang cukup, tapi 1.84 MP < 2 MP
    public async Task LowResolution_IsRejectedBeforeDetection(int width, int height)
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(1, 1, "68140913");
        StubDetector detector = new(cartons);
        CollectSkuHandler handler = Build.Handler(new StubDecoder(width, height), detector, new StubReader(texts), new StubSpotter("GPS Map Camera", "Lat"));

        PhotoNotInspectableException error = await Assert.ThrowsAsync<PhotoNotInspectableException>(
            () => handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None));

        Assert.Equal(PhotoRejectReasons.ResolutionTooLow, error.Reason); // resolusi dicek pertama
        Assert.Equal(0, detector.Calls);
    }

    [Theory]
    [InlineData(2048, 1152)] // 2.36 MP
    [InlineData(1152, 2048)]
    [InlineData(1919, 3413)] // foto truk lapangan
    public async Task ResolutionAtLimit_IsAccepted(int width, int height)
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(1, 1, "68140913");
        CollectSkuHandler handler = Build.Handler(new StubDecoder(width, height), new StubDetector(cartons), new StubReader(texts));

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Single(result.Cells);
    }

    [Fact]
    public async Task CameraOverlay_IsRejected()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(1, 1, "68140913");
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts), new StubSpotter("Note : Captured by GPS Map Camera", "Lat -5.468766, Long 122.598604"), cameraOverlay: true);

        PhotoNotInspectableException error = await Assert.ThrowsAsync<PhotoNotInspectableException>(
            () => handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None));

        Assert.Equal(PhotoRejectReasons.CameraOverlayDetected, error.Reason);
    }

    [Fact]
    public async Task CameraOverlayDisabled_SkipsOcr()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(1, 1, "68140913");
        StubSpotter spotter = new("Note : Captured by GPS Map Camera", "Lat -5.468766, Long 122.598604");
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts), spotter);

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Single(result.Cells);
    }

    [Fact]
    public async Task SingleKeyword_IsNotEnoughForOverlay()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(1, 1, "68140913");
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts), new StubSpotter("LONG LASTING"), cameraOverlay: true);

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Single(result.Cells);
    }

    [Fact]
    public async Task NoCarton_IsRejected()
    {
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector([]), new StubReader([]));

        PhotoNotInspectableException error = await Assert.ThrowsAsync<PhotoNotInspectableException>(
            () => handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None));

        Assert.Equal(PhotoRejectReasons.NoCartonDetected, error.Reason);
    }

    [Fact]
    public async Task OcrDefault_ReadsOnlyLabelAreas()
    {
        CartonBox labelled = Build.Carton(300, 600);
        CartonBox textElsewhere = Build.Carton(710, 600);
        Dictionary<BoundingBox, (string?, string?)> texts = new()
        {
            [labelled.Box] = ("68410975", null),
            [textElsewhere.Box] = (null, "68582213"), // nomor hanya di bagian bawah kardus
        };
        StubReader reader = new(texts);
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector([labelled, textElsewhere]), reader);

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, ["68410975", "68582213"]), None);

        Assert.Equal(1, reader.Calls);
        // area label = 60 % lebar × 30 % tinggi kiri-atas kardus (400 × 300)
        Assert.All(reader.LastRegions, r => Assert.Equal((240.0, 90.0), (r.Width, r.Height)));
        Dictionary<int, CellDto> byColumn = result.Cells.ToDictionary(c => c.Column);
        Assert.Equal(ReadStatus.Matched, byColumn[0].ReadStatus);
        Assert.Equal(ReadStatus.LabelNotVisible, byColumn[1].ReadStatus);
    }

    [Fact]
    public async Task OcrFullCartonFallback_RereadsOnlyUnreadCartons()
    {
        CartonBox labelled = Build.Carton(300, 600);
        CartonBox textElsewhere = Build.Carton(710, 600);
        Dictionary<BoundingBox, (string?, string?)> texts = new()
        {
            [labelled.Box] = ("68410975", null),
            [textElsewhere.Box] = (null, "68582213"),
        };
        StubReader reader = new(texts);
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector([labelled, textElsewhere]), reader, ocrFullCartonFallback: true);

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, ["68410975", "68582213"]), None);

        Assert.Equal(2, reader.Calls);
        Assert.Equal(textElsewhere.Box, Assert.Single(reader.LastRegions)); // tahap 2 hanya kardus yang belum terbaca
        Assert.All(result.Cells, c => Assert.Equal(ReadStatus.Matched, c.ReadStatus));
    }

    private static readonly OcrScanOptions[] TwoScans =
    [
        new() { WidthRatio = 0.6, HeightRatio = 0.3, MinAreaWidth = 320 },
        new() { WidthRatio = 1.0, HeightRatio = 0.4, MinAreaWidth = 480 },
    ];

    [Fact]
    public async Task OcrRetryScans_EachScanOnlyForCartonsStillUnread()
    {
        CartonBox labelled = Build.Carton(300, 600);
        CartonBox small = Build.Carton(710, 600);
        CartonBox smaller = Build.Carton(1120, 600);
        StubReader reader = new(new Dictionary<BoundingBox, (string?, string?)> { [labelled.Box] = ("68410975", null) });
        reader.EnlargedOnly[small.Box] = ("68582213", 320); // terbaca di scan pertama
        reader.EnlargedOnly[smaller.Box] = ("68140913", 480); // baru terbaca di scan kedua
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector([labelled, small, smaller]), reader, ocrRetryScans: TwoScans);

        CollectSkuResponse result = await handler.HandleAsync(
            new CollectSkuCommand(Build.Jpeg, ["68410975", "68582213", "68140913"]), None);

        Assert.Equal([0, 320, 480], reader.Scans);
        // scan kedua hanya kardus yang masih belum terbaca: selebar kardus × 40 % tinggi
        Assert.Equal(new BoundingBox(1120, 600, 1520, 720), Assert.Single(reader.LastRegions));
        Assert.All(result.Cells, c => Assert.Equal(ReadStatus.Matched, c.ReadStatus));
    }

    [Fact]
    public async Task LabelLocator_ReadsLabelOutsideFixedArea_AndSkipsFixedAreaForThatCarton()
    {
        // Carton(x, y) = 400 × 300; area label tetap = kiri-atas 60 % × 30 %. Label kardus ini di kanan-bawah.
        CartonBox rightLabel = Build.Carton(300, 600);
        CartonBox fixedLabel = Build.Carton(710, 600);
        StubReader reader = new(new Dictionary<BoundingBox, (string?, string?)> { [fixedLabel.Box] = ("68582213", null) });
        reader.Anywhere.Add(new OcrText("68410975", 0.95, new BoundingBox(560, 820, 680, 860)));
        StubLocator locator = new(new Dictionary<BoundingBox, BoundingBox> { [rightLabel.Box] = new(550, 810, 690, 870) });
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector([rightLabel, fixedLabel]), reader, locator: locator);

        CollectSkuResponse result = await handler.HandleAsync(
            new CollectSkuCommand(Build.Jpeg, ["68410975", "68582213"]), None);

        Assert.Equal(1, locator.Calls);
        // tahap 0: kotak label (+ margin, dipotong ke kardus) diperbesar; tahap 1: hanya kardus kedua
        Assert.Equal([320, 0], reader.Scans);
        Assert.Equal(LabelRegionOf(fixedLabel), Assert.Single(reader.LastRegions));
        Dictionary<int, CellDto> byColumn = result.Cells.ToDictionary(c => c.Column);
        Assert.Equal(("68410975", ReadStatus.Matched), (byColumn[0].Sku, byColumn[0].ReadStatus));
        Assert.Equal(("68582213", ReadStatus.Matched), (byColumn[1].Sku, byColumn[1].ReadStatus));
    }

    [Fact]
    public async Task LabelLocator_VerticalLabel_ReadAfterRotation_OnlyConfidentEightDigits()
    {
        // Carton(x, y) = 400 × 300. Kotak label tegak (40 × 120) di kanan; putaran salah = angka acak/huruf.
        CartonBox standing = Build.Carton(300, 600);
        CartonBox lowConfidence = Build.Carton(710, 600);
        BoundingBox standingLabel = new(640, 650, 680, 770);
        BoundingBox lowLabel = new(1050, 650, 1090, 770);
        StubReader reader = new([]);
        // seperti tuning case 01: putaran benar "[65067525]" (bingkai label), putaran salah huruf/angka acak
        reader.Rotated[standingLabel] = [("[65067525]", 0.924), ("SZS2L0S9", 0.97), ("(869099)", 0.516)];
        reader.Rotated[lowLabel] = [("65067525", 0.62)];
        StubLocator locator = new(new Dictionary<BoundingBox, BoundingBox>
        {
            [standing.Box] = standingLabel,
            [lowConfidence.Box] = lowLabel,
        });

        CollectSkuResponse result = await Build.Handler(
                new StubDecoder(3000, 4000), new StubDetector([standing, lowConfidence]), reader, locator: locator)
            .HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Equal(2, reader.RotatedRegions.Count);
        // tidak ada label mendatar (tahap 0 @320 dilewati); area tetap hanya untuk kardus yang belum terbaca
        Assert.Equal([0], reader.Scans);
        Dictionary<int, CellDto> byColumn = result.Cells.ToDictionary(c => c.Column);
        Assert.Equal(("65067525", ReadStatus.Matched), (byColumn[0].Sku, byColumn[0].ReadStatus));
        Assert.Equal(ReadStatus.LabelNotVisible, byColumn[1].ReadStatus);
    }

    [Fact]
    public async Task LabelRecognizer_ConfidentRead_SkipsPaddle_UnsureReadFallsBackToPaddle()
    {
        CartonBox sure = Build.Carton(300, 600);
        CartonBox unsure = Build.Carton(710, 600);
        BoundingBox sureLabel = new(560, 820, 680, 860);
        BoundingBox unsureLabel = new(970, 820, 1090, 860);
        StubReader reader = new([]);
        reader.Anywhere.Add(new OcrText("68582213", 0.95, new BoundingBox(975, 825, 1085, 855))); // dibaca Paddle
        StubRecognizer recognizer = new(new Dictionary<BoundingBox, (string, double)>
        {
            [sureLabel] = ("68410975", 0.99),
            [unsureLabel] = ("68582218", 0.91), // salah 1 digit, di bawah ambang 0,95 → diabaikan
        });
        StubLocator locator = new(new Dictionary<BoundingBox, BoundingBox> { [sure.Box] = sureLabel, [unsure.Box] = unsureLabel });

        CollectSkuResponse result = await Build.Handler(
                new StubDecoder(3000, 4000), new StubDetector([sure, unsure]), reader, locator: locator, recognizer: recognizer)
            .HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        // recognizer membaca kotak mentah (tanpa margin) kedua kardus
        Assert.Equal([sureLabel, unsureLabel], recognizer.Regions.Select(r => r.Box));
        Assert.All(recognizer.Regions, r => Assert.Equal(TextLineRotation.None, r.Rotation));
        // Paddle tahap 0b hanya untuk kardus yang belum terbaca yakin
        Assert.Equal(320, reader.Scans[0]);
        Assert.DoesNotContain(reader.LastRegions, r => r.CenterX < 700);
        Dictionary<int, CellDto> byColumn = result.Cells.ToDictionary(c => c.Column);
        Assert.Equal(("68410975", ReadStatus.Matched), (byColumn[0].Sku, byColumn[0].ReadStatus));
        Assert.Equal(("68582213", ReadStatus.Matched), (byColumn[1].Sku, byColumn[1].ReadStatus));
    }

    [Fact]
    public async Task WithoutPaddle_UsesAllRecognizerReads_AndNeverCallsPaddle()
    {
        CartonBox sure = Build.Carton(300, 600);
        CartonBox unsure = Build.Carton(710, 600);
        CartonBox noLabel = Build.Carton(1120, 600);
        BoundingBox sureLabel = new(560, 820, 680, 860);
        BoundingBox unsureLabel = new(970, 820, 1090, 860);
        StubReader reader = new(new Dictionary<BoundingBox, (string?, string?)> { [noLabel.Box] = ("68140913", null) });
        StubRecognizer recognizer = new(new Dictionary<BoundingBox, (string, double)>
        {
            [sureLabel] = ("68410975", 0.99),
            [unsureLabel] = ("68582213", 0.80), // di bawah ambang 0,95, tetap dipakai tanpa Paddle
        });
        StubLocator locator = new(new Dictionary<BoundingBox, BoundingBox> { [sure.Box] = sureLabel, [unsure.Box] = unsureLabel });

        CollectSkuResponse result = await Build.Handler(
                new StubDecoder(3000, 4000), new StubDetector([sure, unsure, noLabel]), reader,
                locator: locator, recognizer: recognizer, paddleOcrFallback: false)
            .HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Equal(0, reader.Calls);
        Dictionary<int, CellDto> byColumn = result.Cells.ToDictionary(c => c.Column);
        Assert.Equal(("68410975", ReadStatus.Matched), (byColumn[0].Sku, byColumn[0].ReadStatus));
        Assert.Equal(("68582213", ReadStatus.Matched), (byColumn[1].Sku, byColumn[1].ReadStatus));
        Assert.Equal(ReadStatus.LabelNotVisible, byColumn[2].ReadStatus); // tanpa kotak label dan tanpa Paddle
    }

    [Fact]
    public async Task SiblingSkus_SunsilkGroup_MergedThenPartialAndAppearanceResolve()
    {
        // tuning case 01: #16 65632159 (97%), #31 65632152 (88%), #30 "6563215", #39 tanpa bacaan; semua Sunsilk
        CartonBox[] sunsilk = [Build.Carton(300, 600), Build.Carton(710, 600), Build.Carton(1120, 600), Build.Carton(1530, 600)];
        BoundingBox Label(CartonBox c) => new(c.Box.X1 + 10, c.Box.Y1 + 10, c.Box.X1 + 130, c.Box.Y1 + 50);
        StubRecognizer recognizer = new(new Dictionary<BoundingBox, (string, double)>
        {
            [Label(sunsilk[0])] = ("65632159", 0.97),
            [Label(sunsilk[1])] = ("65632152", 0.88),
            [Label(sunsilk[2])] = ("6563215", 0.90),
        });
        StubLocator locator = new(sunsilk.ToDictionary(c => c.Box, Label));

        CollectSkuResponse result = await Build.Handler(
                new StubDecoder(3000, 4000), new StubDetector(sunsilk), new StubReader([]),
                embedder: new StubEmbedder(_ => [1f, 0f]), locator: locator, recognizer: recognizer, paddleOcrFallback: false)
            .HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Dictionary<int, CellDto> byColumn = result.Cells.ToDictionary(c => c.Column);
        Assert.All(byColumn.Values, c => Assert.Equal("65632159", c.Sku));
        Assert.Equal(
            [ReadStatus.Matched, ReadStatus.Corrected, ReadStatus.Corrected, ReadStatus.InferredByAppearance],
            byColumn.OrderBy(c => c.Key).Select(c => c.Value.ReadStatus));
        Assert.Contains("SKU_CORRECTED_BY_MAJORITY", result.Warnings);
    }

    [Fact]
    public async Task Reconstruction_PepsodentBlock_RecoversFromWrongFullReadAndDroppedDigits()
    {
        // tuning case 01: #33 64955472 (salah, 97%), #42 6495562, #51 6495546 (sama-sama kurang angka 4), #34 potongan 64955
        CartonBox[] pepsodent = [Build.Carton(300, 600), Build.Carton(710, 600), Build.Carton(1120, 600), Build.Carton(1530, 600)];
        BoundingBox Label(CartonBox c) => new(c.Box.X1 + 10, c.Box.Y1 + 10, c.Box.X1 + 130, c.Box.Y1 + 50);
        StubRecognizer recognizer = new(new Dictionary<BoundingBox, (string, double)>
        {
            [Label(pepsodent[0])] = ("64955472", 0.97),
            [Label(pepsodent[1])] = ("6495562", 0.99),
            [Label(pepsodent[2])] = ("6495546", 0.98),
            [Label(pepsodent[3])] = ("64955", 0.90),
        });

        CollectSkuResponse result = await Build.Handler(
                new StubDecoder(3000, 4000), new StubDetector(pepsodent), new StubReader([]),
                embedder: new StubEmbedder(_ => [1f, 0f]),
                locator: new StubLocator(pepsodent.ToDictionary(c => c.Box, Label)),
                recognizer: recognizer,
                paddleOcrFallback: false)
            .HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.All(result.Cells, c => Assert.Equal("64955462", c.Sku));
        Assert.All(result.Cells, c => Assert.Equal(ReadStatus.Corrected, c.ReadStatus));
    }

    [Fact]
    public async Task LabelBoxes_ReportLocatorBoxAndBestRecognizerRead()
    {
        CartonBox upright = Build.Carton(300, 600);
        CartonBox standing = Build.Carton(710, 600);
        BoundingBox uprightLabel = new(560, 820, 680, 860);
        BoundingBox standingLabel = new(1050, 650, 1090, 770);
        StubRecognizer recognizer = new(new Dictionary<BoundingBox, (string, double)>
        {
            [uprightLabel] = ("6487156", 0.99),
            [standingLabel] = ("65067525", 0.95),
        });

        CollectSkuResponse result = await Build.Handler(
                new StubDecoder(3000, 4000), new StubDetector([upright, standing]), new StubReader([]),
                locator: new StubLocator(new Dictionary<BoundingBox, BoundingBox> { [upright.Box] = uprightLabel, [standing.Box] = standingLabel }),
                recognizer: recognizer,
                paddleOcrFallback: false)
            .HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Equal(2, result.LabelBoxes.Count);
        Dictionary<int, int> idByColumn = result.Cells.ToDictionary(c => c.Column, c => c.Id!.Value);
        LabelBoxDto first = Assert.Single(result.LabelBoxes, b => b.CartonId == idByColumn[0]);
        Assert.Equal(
            (560, 820, 680, 860, 0.9, "6487156", 0.99),
            (first.BoundingBox.X1, first.BoundingBox.Y1, first.BoundingBox.X2, first.BoundingBox.Y2, first.Confidence, first.Text, first.TextConfidence));
        Assert.Equal("65067525", Assert.Single(result.LabelBoxes, b => b.CartonId == idByColumn[1]).Text);
    }

    [Fact]
    public async Task LabelRecognizer_VerticalLabel_TriesBothRotations()
    {
        CartonBox standing = Build.Carton(300, 600);
        BoundingBox label = new(640, 650, 680, 770);
        StubRecognizer recognizer = new(new Dictionary<BoundingBox, (string, double)> { [label] = ("65067525", 0.97) });

        CollectSkuResponse result = await Build.Handler(
                new StubDecoder(3000, 4000), new StubDetector([standing]), new StubReader([]),
                locator: new StubLocator(new Dictionary<BoundingBox, BoundingBox> { [standing.Box] = label }), recognizer: recognizer)
            .HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Equal([TextLineRotation.Clockwise, TextLineRotation.CounterClockwise], recognizer.Regions.Select(r => r.Rotation));
        Assert.Equal("65067525", Assert.Single(result.Cells).Sku);
    }

    [Fact]
    public async Task LabelLocator_Disabled_ReadsFixedAreaOfAllCartonsLikeBefore()
    {
        CartonBox rightLabel = Build.Carton(300, 600);
        StubReader reader = new([]);
        reader.Anywhere.Add(new OcrText("68410975", 0.95, new BoundingBox(560, 820, 680, 860)));

        CollectSkuResponse result = await Build.Handler(new StubDecoder(3000, 4000), new StubDetector([rightLabel]), reader)
            .HandleAsync(new CollectSkuCommand(Build.Jpeg, ["68410975"]), None);

        Assert.Equal([0], reader.Scans);
        Assert.Equal(ReadStatus.LabelNotVisible, Assert.Single(result.Cells).ReadStatus);
    }

    private static BoundingBox LabelRegionOf(CartonBox carton) =>
        new(carton.Box.X1, carton.Box.Y1, carton.Box.X1 + (carton.Box.Width * 0.6), carton.Box.Y1 + (carton.Box.Height * 0.3));

    [Fact]
    public async Task OcrRetryScans_StopWhenAllCartonsRead()
    {
        CartonBox labelled = Build.Carton(300, 600);
        CartonBox small = Build.Carton(710, 600);
        StubReader reader = new(new Dictionary<BoundingBox, (string?, string?)> { [labelled.Box] = ("68410975", null) });
        reader.EnlargedOnly[small.Box] = ("68582213", 320);
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector([labelled, small]), reader, ocrRetryScans: TwoScans);

        await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, ["68410975", "68582213"]), None);

        Assert.Equal([0, 320], reader.Scans);
    }

    [Fact]
    public async Task ReadStatus_LabelFallbackUnknownAndNotVisible()
    {
        CartonBox corrected = Build.Carton(300, 600);
        CartonBox fromBox = Build.Carton(710, 600);
        CartonBox unknown = Build.Carton(1120, 600);
        CartonBox notVisible = Build.Carton(1530, 600);
        Dictionary<BoundingBox, (string?, string?)> texts = new()
        {
            [corrected.Box] = ("68410B75", null), // 1 karakter salah → Corrected
            [fromBox.Box] = (null, "SKU 68582213"), // label kosong, terbaca di seluruh kardus
            [unknown.Box] = ("12345678", null), // digit ada tapi jauh dari candidate
            [notVisible.Box] = (null, "10"), // tidak ada teks 7–9 digit sama sekali
        };
        StubReader reader = new(texts);
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector([corrected, fromBox, unknown, notVisible]), reader, ocrFullCartonFallback: true);

        CollectSkuResponse result = await handler.HandleAsync(
            new CollectSkuCommand(Build.Jpeg, ["68410975", "68582213,68140913"]), None);

        Dictionary<int, CellDto> byColumn = result.Cells.ToDictionary(c => c.Column);
        Assert.Equal(("68410975", ReadStatus.Corrected), (byColumn[0].Sku, byColumn[0].ReadStatus));
        Assert.Equal(("68582213", ReadStatus.Matched), (byColumn[1].Sku, byColumn[1].ReadStatus));
        Assert.Equal((SkuCodes.Unknown, ReadStatus.Unknown, "12345678"), (byColumn[2].Sku, byColumn[2].ReadStatus, byColumn[2].OcrText));
        Assert.Equal((SkuCodes.LabelNotVisible, ReadStatus.LabelNotVisible), (byColumn[3].Sku, byColumn[3].ReadStatus));
        Assert.Null(byColumn[3].OcrText);
        Assert.Contains(CollectWarnings.UnknownSkuPresent, result.Warnings);
        Assert.Contains(CollectWarnings.LabelNotVisiblePresent, result.Warnings);
        // tahap 1 sekali untuk semua area label, tahap 2 sekali untuk 3 kardus yang belum terbaca
        Assert.Equal(2, reader.Calls);
        Assert.Equal(3, reader.LastRegions.Count);
    }

    [Fact]
    public async Task BackLayer_IsExcludedAndWarned()
    {
        CartonBox bottom = Build.Carton(300, 900);
        CartonBox top = Build.Carton(300, 600);
        CartonBox back = Build.Carton(380, 380, 200, 150); // kecil di atas kolom = lapisan belakang
        Dictionary<BoundingBox, (string?, string?)> texts = new()
        {
            [bottom.Box] = ("68140913", null),
            [top.Box] = ("68140913", null),
            [back.Box] = ("68140913", null),
        };
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector([bottom, top, back]), new StubReader(texts));

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Equal((2, 1), (result.Cells.Count, result.ExcludedBackLayerCount));
        Assert.Contains(CollectWarnings.BackLayerExcluded, result.Warnings);
    }

    [Theory]
    [InlineData("657305541")] // garis kotak label terbaca "1" di belakang
    [InlineData("165730554")] // ... atau di depan
    public async Task LabelFrameReadAsOne_IsStripped(string ocr)
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(1, 1, ocr);
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts));

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        CellDto cell = Assert.Single(result.Cells);
        Assert.Equal(("65730554", ReadStatus.Matched, ocr), (cell.Sku, cell.ReadStatus, cell.OcrText));
    }

    [Fact]
    public async Task WithoutCandidates_MisreadIsCorrectedByMajority()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(2, 2, "65730554");
        texts[cartons[3].Box] = ("85730554", null);
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts));

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        CellDto misread = Assert.Single(result.Cells, c => c.OcrText == "85730554");
        Assert.Equal(("65730554", ReadStatus.Corrected), (misread.Sku, misread.ReadStatus));
        Assert.Contains(CollectWarnings.SkuCorrectedByMajority, result.Warnings);
    }

    [Fact]
    public async Task WithCandidates_MajorityCorrectionIsNotUsed()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(2, 2, "65730554");
        texts[cartons[3].Box] = ("85730554", null);
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts));

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, ["65730554", "85730554"]), None);

        Assert.Single(result.Cells, c => c.Sku == "85730554" && c.ReadStatus == ReadStatus.Matched);
        Assert.DoesNotContain(CollectWarnings.SkuCorrectedByMajority, result.Warnings);
    }

    [Theory]
    [InlineData(5, 0.0)]     // 4 kardus < minimal 5
    [InlineData(0, 0.15)]    // 4 kardus 400x300 di foto 3000x4000 = 4% luas foto
    public async Task WeakDetection_IsFastRejectedBeforeOcr(int minCount, double minCoverage)
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(2, 2, "68140913");
        StubReader reader = new(texts);
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector(cartons), reader, minCartonCount: minCount, minCartonCoverage: minCoverage);

        PhotoNotInspectableException error = await Assert.ThrowsAsync<PhotoNotInspectableException>(
            () => handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None));

        Assert.Equal((PhotoRejectReasons.FastRejected, 0), (error.Reason, reader.Calls));
    }

    [Fact]
    public async Task DenseLoad_PassesDetectionGate()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(8, 6, "68140913");
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts), minCartonCount: 5, minCartonCoverage: 0.15);

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Equal(48, result.Cells.Count);
    }

    [Fact]
    public async Task NullCandidate_IsIgnored()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(2, 2, "65730554");
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts));

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, [null!, "65730554"]), None);

        Assert.All(result.Cells, c => Assert.Equal(("65730554", ReadStatus.Matched), (c.Sku, c.ReadStatus)));
    }

    [Fact]
    public async Task NestedDuplicateBox_IsRemoved()
    {
        CartonBox carton = Build.Carton(300, 600);
        CartonBox nested = new(new BoundingBox(320, 620, 520, 800), 0.7); // kotak ganda di dalam kardus
        Dictionary<BoundingBox, (string?, string?)> texts = new() { [carton.Box] = ("68140913", null) };
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector([carton, nested]), new StubReader(texts));

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        CellDto cell = Assert.Single(result.Cells);
        Assert.Equal(("68140913", 300), (cell.Sku, cell.BoundingBox.X1));
    }

    [Fact]
    public async Task Upload_EmptyTooLargeOrUnsupported_IsValidationError()
    {
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), new StubDetector([]), new StubReader([]), maxFileMb: 1);

        await Assert.ThrowsAsync<RequestValidationException>(() => handler.HandleAsync(new CollectSkuCommand(Array.Empty<byte>(), []), None));
        await Assert.ThrowsAsync<RequestValidationException>(() => handler.HandleAsync(new CollectSkuCommand("GIF89a"u8.ToArray(), []), None));
        byte[] big = new byte[(1024 * 1024) + 1];
        Build.Jpeg.CopyTo(big, 0);
        await Assert.ThrowsAsync<RequestValidationException>(() => handler.HandleAsync(new CollectSkuCommand(big, []), None));
    }

    [Fact]
    public async Task Model_DefaultAndSelectedAreUsed()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(1, 1, "68140913");
        StubDetector detector = new(cartons);
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), detector, new StubReader(texts));

        CollectSkuResponse byDefault = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);
        Assert.Equal(("carton-v1", "carton-v1"), (byDefault.Model, detector.LastModel?.Name));

        CollectSkuResponse chosen = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, [], "carton-v2"), None);
        Assert.Equal(("carton-v2", 0.4), (chosen.Model, detector.LastModel?.ConfidenceThreshold));
    }

    [Fact]
    public async Task Model_UnknownName_IsValidationErrorBeforeDecoding()
    {
        StubDetector detector = new([]);
        CollectSkuHandler handler = Build.Handler(new StubDecoder(3000, 4000), detector, new StubReader([]));

        RequestValidationException error = await Assert.ThrowsAsync<RequestValidationException>(
            () => handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, [], "carton-v9"), None));

        Assert.Contains("model", error.Errors.Keys);
        Assert.Equal(0, detector.Calls);
    }
}
