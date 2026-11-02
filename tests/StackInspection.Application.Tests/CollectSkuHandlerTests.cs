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
        Assert.Equal([0, 1, 2], result.Stacks[0].SkusBottomToTop.Select((_, i) => i));
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
        Assert.All(result.Stacks, s => Assert.Equal(top[0].Id, s.CartonIdsBottomToTop[^1]));
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
