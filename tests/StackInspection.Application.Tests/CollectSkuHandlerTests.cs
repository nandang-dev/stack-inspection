using StackInspection.Application.CollectSku;
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
    public async Task LowResolution_IsRejectedBeforeDetection()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(1, 1, "68140913");
        StubDetector detector = new(cartons);
        CollectSkuHandler handler = Build.Handler(new StubDecoder(1920, 2560), detector, new StubReader(texts), new StubSpotter("GPS Map Camera", "Lat"));

        PhotoNotInspectableException error = await Assert.ThrowsAsync<PhotoNotInspectableException>(
            () => handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None));

        Assert.Equal(PhotoRejectReasons.ResolutionTooLow, error.Reason); // resolusi dicek pertama
        Assert.Equal(0, detector.Calls);
    }

    [Theory]
    [InlineData(3000, 2000)]
    [InlineData(2000, 3000)]
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
            new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts), new StubSpotter("Note : Captured by GPS Map Camera", "Lat -5.468766, Long 122.598604"));

        PhotoNotInspectableException error = await Assert.ThrowsAsync<PhotoNotInspectableException>(
            () => handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None));

        Assert.Equal(PhotoRejectReasons.CameraOverlayDetected, error.Reason);
    }

    [Fact]
    public async Task SingleKeyword_IsNotEnoughForOverlay()
    {
        (List<CartonBox> cartons, Dictionary<BoundingBox, (string?, string?)> texts) = Grid(1, 1, "68140913");
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts), new StubSpotter("LONG LASTING"));

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
            new StubDecoder(3000, 4000), new StubDetector([corrected, fromBox, unknown, notVisible]), reader);

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
        Assert.Contains(Build.Label(corrected), reader.Requests); // area label dibaca dulu
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
