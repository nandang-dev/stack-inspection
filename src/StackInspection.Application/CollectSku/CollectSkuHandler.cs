using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Application.Contracts;
using StackInspection.Application.Exceptions;
using StackInspection.Application.Imaging;
using StackInspection.Application.Inspectability;
using StackInspection.Domain;

namespace StackInspection.Application.CollectSku;

/// <summary>Input endpoint Collect SKU.</summary>
public sealed record CollectSkuCommand(ReadOnlyMemory<byte> Image, IReadOnlyList<string> CandidateSkus, string? Model = null);

/// <summary>Hasil baca SKU satu kardus.</summary>
public sealed record SkuReading(string Sku, string? OcrText, double Confidence, ReadStatus Status);

/// <summary>
/// Pipeline Collect SKU: validasi upload → decode → photo gate → deteksi → buang kotak bersarang →
/// filter lapisan depan → OCR area label semua kardus (tahap 2 opsional: seluruh kardus yang belum terbaca) →
/// teks dicocokkan ke kardus berdasarkan posisi (area label dulu, fallback seluruh kardus) → fuzzy match → koreksi mayoritas → lengkapi potongan nomor →
/// SKU dari kardus yang tampilannya mirip → grid tampilan + relasi tumpuan (satu entri cell per sel grid).
/// </summary>
public sealed partial class CollectSkuHandler
{
    private const int MinSkuDigits = 7;
    private const int MaxSkuDigits = 9;
    private const int MaxPartialDigits = MinSkuDigits;
    private static readonly char[] WordSeparators = [' ', '\t', ':', ';', ',', '/'];
    private static readonly char[] LabelFramePunctuation = ['[', ']', '(', ')', '{', '}', '.', '-', '_', '\'', '"'];

    private readonly IImageDecoder _decoder;
    private readonly ICartonDetector _detector;
    private readonly IModelCatalog _models;
    private readonly ISkuLabelReader _reader;
    private readonly ICartonEmbedder _embedder;
    private readonly ILabelLocator _locator;
    private readonly ITextLineRecognizer _recognizer;
    private readonly IProcessingLog _processingLog;
    private readonly PhotoInspectabilityService _inspectability;
    private readonly VisionOptions _vision;
    private readonly FrontLayerOptions _frontLayer;
    private readonly UploadOptions _upload;
    private readonly PhotoOptions _photo;
    private readonly ILogger<CollectSkuHandler> _logger;

    public CollectSkuHandler(
        IImageDecoder decoder,
        ICartonDetector detector,
        IModelCatalog models,
        ISkuLabelReader reader,
        ICartonEmbedder embedder,
        ILabelLocator locator,
        ITextLineRecognizer recognizer,
        IProcessingLog processingLog,
        PhotoInspectabilityService inspectability,
        IOptions<VisionOptions> vision,
        IOptions<FrontLayerOptions> frontLayer,
        IOptions<UploadOptions> upload,
        IOptions<PhotoOptions> photo,
        ILogger<CollectSkuHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(vision);
        ArgumentNullException.ThrowIfNull(frontLayer);
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(photo);
        _decoder = decoder;
        _detector = detector;
        _models = models;
        _reader = reader;
        _embedder = embedder;
        _locator = locator;
        _recognizer = recognizer;
        _processingLog = processingLog;
        _inspectability = inspectability;
        _vision = vision.Value;
        _frontLayer = frontLayer.Value;
        _upload = upload.Value;
        _photo = photo.Value;
        _logger = logger;
    }

    public async Task<CollectSkuResponse> HandleAsync(CollectSkuCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using IProcessingLogCapture capture = _processingLog.Begin();
        Stopwatch stopwatch = Stopwatch.StartNew();
        ValidateUpload(command.Image);
        string[] candidates = NormalizeCandidates(command.CandidateSkus);
        DetectionModel model = _models.Resolve(command.Model);

        using VisionImage image = _decoder.Decode(command.Image);
        long decodeMs = stopwatch.ElapsedMilliseconds;
        await _inspectability.EnsureInspectableAsync(image, cancellationToken).ConfigureAwait(false);
        long inspectMs = stopwatch.ElapsedMilliseconds;

        IReadOnlyList<CartonBox> detected = await _detector.DetectAsync(image, model, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CartonBox> distinct = ContainmentFilter.Apply(detected, _vision.ContainmentThreshold);
        string? failedGate = DetectionGate.Check(
            distinct, image.Width, image.Height, _photo.MinCartonCount, _photo.MinCartonCoverage);
        if (failedGate is not null)
        {
            LogFastReject(_logger, failedGate, stopwatch.ElapsedMilliseconds, distinct.Count);
            throw new PhotoNotInspectableException(
                PhotoRejectReasons.FastRejected,
                "Foto tidak layak dinilai. Ambil ulang foto muatan dari depan, cukup dekat, dan jelas.");
        }

        FrontLayerResult front = FrontLayerFilter.Apply(
            distinct, _frontLayer.MinWidthRatio, _frontLayer.MaxGapRatio, _frontLayer.MinTopHeightRatio);
        if (front.Kept.Count == 0)
        {
            throw new PhotoNotInspectableException(
                PhotoRejectReasons.NoCartonDetected,
                "Tidak ada kardus yang terdeteksi di lapisan depan foto.");
        }

        long filterMs = stopwatch.ElapsedMilliseconds;
        // Kotak label mentah (dipotong ke kardus) untuk recognizer; + margin untuk Paddle yang punya deteksi teks sendiri.
        IReadOnlyList<IReadOnlyList<BoundingBox>> rawLabels = await LocateLabelsAsync(image, front.Kept, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<IReadOnlyList<BoundingBox>> labels = [.. rawLabels.Select((l, i) =>
            (IReadOnlyList<BoundingBox>)[.. l.Select(b => Expand(b, front.Kept[i].Box, _vision.LabelLocatorMargin))])];
        long locateMs = stopwatch.ElapsedMilliseconds;
        IReadOnlyList<OcrText> texts = [];
        IReadOnlyList<SkuReading> ReadAll() => [.. front.Kept.Select((c, i) => ReadSku(c.Box, labels[i], texts, candidates))];
        IReadOnlyList<SkuReading> readings = ReadAll();
        bool Unread(int i) => readings[i].Status is not (ReadStatus.Matched or ReadStatus.Corrected);

        // OCR tahap 0a: recognizer langsung pada kotak label (tanpa deteksi teks, cepat). Dengan cadangan Paddle hanya
        // hasil sangat yakin yang dipakai (sisanya dibaca Paddle); tanpa Paddle semua hasil dipakai dan bacaan yang ragu
        // disaring oleh koreksi mayoritas / pelengkapan potongan seperti bacaan OCR biasa.
        bool paddle = _vision.PaddleOcrFallback;
        if (!string.IsNullOrWhiteSpace(_vision.LabelRecognizerModel) && rawLabels.Any(l => l.Count > 0))
        {
            TextLineRegion[] lines = [.. rawLabels.SelectMany(l => l).SelectMany(b => IsVertical(b)
                ? (TextLineRegion[])[new(b, TextLineRotation.Clockwise), new(b, TextLineRotation.CounterClockwise)]
                : [new(b, TextLineRotation.None)])];
            long recognizeStart = stopwatch.ElapsedMilliseconds;
            IReadOnlyList<OcrText> recognized = await _recognizer.RecognizeAsync(image, lines, cancellationToken).ConfigureAwait(false);
            double minConfidence = paddle ? _vision.LabelRecognizerMinConfidence : 0;
            texts = [.. recognized.Where((t, i) => t.Text.Length > 0 && t.Confidence >= minConfidence
                && (lines[i].Rotation == TextLineRotation.None || IsConfidentRotatedRead(t)))];
            readings = ReadAll();
            LogRecognizer(
                _logger,
                _vision.LabelRecognizerModel,
                lines.Length,
                recognized.Count(t => t.Text.Length > 0),
                texts.Count,
                minConfidence,
                readings.Count(r => r.Status is ReadStatus.Matched or ReadStatus.Corrected),
                readings.Count,
                stopwatch.ElapsedMilliseconds - recognizeStart);
        }

        if (!paddle)
        {
            LogNoPaddle(_logger, readings.Count(r => r.Status is not (ReadStatus.Matched or ReadStatus.Corrected)));
        }

        BoundingBox[] located = [.. labels.SelectMany(l => l)];
        if (paddle)
        {
            await ReadWithPaddleAsync().ConfigureAwait(false);
        }

        // Cadangan Paddle (Vision:PaddleOcrFallback): kardus yang belum terbaca dibaca ulang dalam beberapa putaran.
        async Task ReadWithPaddleAsync()
        {
            // OCR tahap 0b (Paddle): kotak label + margin, hanya kardus yang belum terbaca
            BoundingBox[] upright = [.. labels.Where((_, i) => Unread(i)).SelectMany(l => l).Where(l => !IsVertical(l))];
            if (upright.Length > 0)
            {
                IReadOnlyList<OcrText> more = await _reader
                    .ReadAllAsync(image, upright, _vision.LabelLocatorMinAreaWidth, cancellationToken)
                    .ConfigureAwait(false);
                texts = [.. texts, .. more];
            }

            // OCR tahap 0c: label tegak (diputar 90°), dibaca setelah diputar ke dua arah; hanya angka yang meyakinkan
            BoundingBox[] vertical = [.. labels.Where((_, i) => Unread(i)).SelectMany(l => l).Where(IsVertical)];
            if (vertical.Length > 0 && _vision.LabelLocatorRotatedMinConfidence > 0)
            {
                IReadOnlyList<OcrText> rotated = await _reader
                    .ReadRotatedAsync(image, vertical, _vision.LabelLocatorMinAreaWidth, cancellationToken)
                    .ConfigureAwait(false);
                texts = [.. texts, .. rotated.Where(IsConfidentRotatedRead)];
            }

            readings = ReadAll();

            // OCR tahap 1: area label tetap (kiri-atas), hanya kardus yang belum terbaca dari kotak label
            BoundingBox[] fixedAreas = [.. front.Kept
                .Where((_, i) => readings[i].Status is not (ReadStatus.Matched or ReadStatus.Corrected))
                .Select(c => LabelRegion(c.Box, _vision.LabelCropWidthRatio, _vision.LabelCropHeightRatio))];
            if (fixedAreas.Length > 0)
            {
                IReadOnlyList<OcrText> more = await _reader.ReadAllAsync(image, fixedAreas, 0, cancellationToken).ConfigureAwait(false);
                texts = [.. texts, .. more];
                readings = ReadAll();
            }

            foreach (OcrScanOptions scan in _vision.OcrRetryScans)
            {
                // OCR tahap 1b: kardus yang masih belum terbaca dibaca ulang dengan ukuran area/perbesaran scan ini
                // (angka kecil, atau terpotong batas area). Kardus yang sudah terbaca tidak disentuh.
                BoundingBox[] unread = [.. front.Kept
                    .Where((_, i) => readings[i].Status is not (ReadStatus.Matched or ReadStatus.Corrected))
                    .Select(c => LabelRegion(c.Box, scan.WidthRatio, scan.HeightRatio))];
                if (unread.Length == 0)
                {
                    break;
                }

                IReadOnlyList<OcrText> more = await _reader
                    .ReadAllAsync(image, unread, scan.MinAreaWidth, cancellationToken)
                    .ConfigureAwait(false);
                texts = [.. texts, .. more];
                readings = ReadAll();
            }

            if (_vision.OcrFullCartonFallback)
            {
                // OCR tahap 2 (opsional): seluruh kardus, hanya untuk kardus yang belum terbaca dari area label
                BoundingBox[] unread = [.. front.Kept
                    .Where((_, i) => readings[i].Status is not (ReadStatus.Matched or ReadStatus.Corrected))
                    .Select(c => c.Box)];
                if (unread.Length > 0)
                {
                    IReadOnlyList<OcrText> more = await _reader.ReadAllAsync(image, unread, 0, cancellationToken).ConfigureAwait(false);
                    texts = [.. texts, .. more];
                    readings = ReadAll();
                }
            }
        }

        long ocrMs = stopwatch.ElapsedMilliseconds;
        bool majorityCorrected = false;
        if (candidates.Length == 0)
        {
            // Tanpa candidateSkus (master SKU): koreksi salah baca ke SKU mayoritas di foto yang sama.
            IReadOnlyList<SkuReading> corrected = MajorityCorrection.Apply(
                readings,
                [.. front.Kept.Select(c => DigitVariants(c.Box, texts))],
                _vision.MajorityMinCount,
                _vision.MajorityDominance,
                _vision.MajorityMaxDistance,
                _vision.MajorityMaxConfidence);
            majorityCorrected = corrected.Where((r, i) => r != readings[i]).Any();
            readings = corrected;
        }

        IReadOnlyList<SkuReading> completed = PartialReadMatch.Apply(
            readings,
            [.. front.Kept.Select(c => PartialDigits(c.Box, texts))],
            candidates,
            _vision.PartialReadMinDigits,
            _vision.MajorityMinCount,
            _vision.MajorityDominance);
        bool partialCorrected = completed.Where((r, i) => r != readings[i]).Any();
        readings = completed;
        IReadOnlyList<float[]> embeddings = await EmbedAsync(image, front.Kept, readings, cancellationToken).ConfigureAwait(false);
        if (candidates.Length == 0 && embeddings.Count == readings.Count && _vision.MajorityAppearanceMaxDistance > _vision.MajorityMaxDistance)
        {
            // Salah baca lebih dari MajorityMaxDistance digit tetap dikoreksi jika tampilannya mirip kardus mayoritas.
            IReadOnlyList<SkuReading> current = readings;
            IReadOnlyList<SkuReading> corrected = MajorityCorrection.Apply(
                current,
                [.. front.Kept.Select(c => DigitVariants(c.Box, texts))],
                _vision.MajorityMinCount,
                _vision.MajorityDominance,
                _vision.MajorityAppearanceMaxDistance,
                _vision.MajorityMaxConfidence,
                (i, sku) => AppearanceInference.BestSimilarity(current, embeddings, i, sku) >= _vision.AppearanceMinSimilarity);
            majorityCorrected |= corrected.Where((r, i) => r != current[i]).Any();
            readings = corrected;
        }

        readings = InferByAppearance(readings, embeddings);
        long resolveMs = stopwatch.ElapsedMilliseconds;
        LogTimings(
            _logger,
            decodeMs,
            inspectMs - decodeMs,
            filterMs - inspectMs,
            locateMs - filterMs,
            ocrMs - locateMs,
            resolveMs - ocrMs,
            front.Kept.Count,
            located.Length,
            texts.Count);

        GridLayout layout = GridBuilder.Build([.. front.Kept.Select(c => c.Box)]);
        // id kardus diurutkan dari kiri-atas agar mudah dibaca; restsOn ikut dipetakan ke id
        int[] ids = new int[front.Kept.Count];
        int nextId = 0;
        foreach (int index in Enumerable.Range(0, front.Kept.Count)
            .OrderBy(i => layout.Cartons[i].Cells[0].Row)
            .ThenBy(i => layout.Cartons[i].Cells[0].Column))
        {
            ids[index] = nextId++;
        }

        List<CellDto> cells = [];
        for (int i = 0; i < front.Kept.Count; i++)
        {
            CartonBox carton = front.Kept[i];
            CartonPlacement placement = layout.Cartons[i];
            SkuReading reading = readings[i];
            int[] restsOn = [.. placement.RestsOn.Select(r => ids[r]).Order()];
            foreach (GridCell grid in placement.Cells)
            {
                cells.Add(new CellDto
                {
                    Id = ids[i],
                    Row = grid.Row,
                    Column = grid.Column,
                    Level = placement.Level,
                    RestsOn = restsOn,
                    Sku = reading.Sku,
                    OcrText = reading.OcrText,
                    OcrConfidence = Math.Round(reading.Confidence, 4),
                    DetectionConfidence = Math.Round(carton.Confidence, 4),
                    ReadStatus = reading.Status,
                    BoundingBox = ToDto(carton.Box),
                });
            }
        }

        cells = [.. cells.OrderBy(c => c.Row).ThenBy(c => c.Column).ThenBy(c => c.Id)];
        // sel kiri-atas mewakili kardus di stacks (cells sudah urut row lalu column)
        Dictionary<int, CellDto> cartonCells = cells.GroupBy(c => c.Id!.Value).ToDictionary(g => g.Key, g => g.First());
        List<StackDto> stacks = [.. layout.Paths.BottomToTop.Select((path, p) => new StackDto
        {
            Path = p,
            Column = layout.Cartons[path[0]].Cells.Min(c => c.Column),
            Height = path.Count,
            Cartons = [.. path.Select(i => cartonCells[ids[i]])],
        })];
        List<DistinctSkuDto> distinctSkus = BuildDistinct(cells);
        List<string> warnings = BuildWarnings(front, layout, cells, majorityCorrected, partialCorrected);
        long totalMs = stopwatch.ElapsedMilliseconds;
        LogResult(
            _logger,
            cells.Where(c => c.ReadStatus == ReadStatus.Matched).Select(c => c.Id).Distinct().Count(),
            cells.Where(c => c.ReadStatus == ReadStatus.Corrected).Select(c => c.Id).Distinct().Count(),
            cells.Where(c => c.ReadStatus == ReadStatus.InferredByAppearance).Select(c => c.Id).Distinct().Count(),
            cells.Where(c => c.ReadStatus == ReadStatus.Unknown).Select(c => c.Id).Distinct().Count(),
            cells.Where(c => c.ReadStatus == ReadStatus.LabelNotVisible).Select(c => c.Id).Distinct().Count());
        return new CollectSkuResponse
        {
            InspectionId = Guid.NewGuid(),
            Model = model.Name,
            ImageWidth = image.Width,
            ImageHeight = image.Height,
            RowCount = layout.RowCount,
            ColumnCount = layout.ColumnCount,
            ExcludedBackLayerCount = front.ExcludedCount,
            Cells = cells,
            Stacks = stacks,
            DistinctSkus = distinctSkus,
            Warnings = warnings,
            ProcessingTimeMs = totalMs,
            Logs = [.. capture.Entries.Select(e => new ProcessingLogDto { AtMs = e.AtMs, Level = e.Level, Source = e.Source, Message = e.Message })],
            Timings = new CollectSkuTimingsDto
            {
                DecodeMs = decodeMs,
                InspectMs = inspectMs - decodeMs,
                DetectMs = filterMs - inspectMs,
                LabelLocateMs = locateMs - filterMs,
                OcrMs = ocrMs - locateMs,
                SkuResolveMs = resolveMs - ocrMs,
                BuildMs = totalMs - resolveMs,
            },
        };
    }

    private void ValidateUpload(ReadOnlyMemory<byte> image)
    {
        if (image.IsEmpty)
        {
            throw new RequestValidationException("image", "File gambar kosong.");
        }

        if (image.Length > _upload.MaxFileSizeBytes)
        {
            throw new RequestValidationException("image", $"Ukuran file melebihi batas {_upload.MaxFileSizeMb} MB.");
        }

        if (ImageFormatDetector.Detect(image.Span) == ImageFormat.Unsupported)
        {
            throw new RequestValidationException("image", "Format gambar tidak didukung. Gunakan JPG atau PNG.");
        }
    }

    private static string[] NormalizeCandidates(IReadOnlyList<string?> candidates) =>
        [.. candidates
            // field form candidateSkus yang dikirim kosong di-bind menjadi elemen null
            .OfType<string>()
            .SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(SkuMatcher.NormalizeDigits)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>Area label: 60% lebar kiri × 30% tinggi atas dari bbox (rasio dari konfigurasi).</summary>
    public static BoundingBox LabelRegion(BoundingBox box, double widthRatio, double heightRatio) =>
        new(box.X1, box.Y1, box.X1 + (box.Width * widthRatio), box.Y1 + (box.Height * heightRatio));

    /// <summary>
    /// Urutan sumber teks: kotak label hasil label locator, lalu area label tetap, lalu teks lain di dalam kardus.
    /// </summary>
    private SkuReading ReadSku(BoundingBox box, IReadOnlyList<BoundingBox> located, IReadOnlyList<OcrText> texts, string[] candidates)
    {
        BoundingBox label = LabelRegion(box, _vision.LabelCropWidthRatio, _vision.LabelCropHeightRatio);
        List<OcrText> locatedTexts = [];
        List<OcrText> labelTexts = [];
        List<OcrText> boxTexts = [];
        foreach (OcrText text in texts)
        {
            if (text.Box is not BoundingBox position || !Contains(box, position.CenterX, position.CenterY))
            {
                continue;
            }

            if (located.Any(l => Contains(l, position.CenterX, position.CenterY)))
            {
                locatedTexts.Add(text);
            }
            else
            {
                (Contains(label, position.CenterX, position.CenterY) ? labelTexts : boxTexts).Add(text);
            }
        }

        SkuReading? fromLocated = Resolve(locatedTexts, candidates);
        if (fromLocated is { Status: ReadStatus.Matched or ReadStatus.Corrected })
        {
            return fromLocated;
        }

        SkuReading? fromLabel = Resolve(labelTexts, candidates);
        if (fromLabel is { Status: ReadStatus.Matched or ReadStatus.Corrected })
        {
            return fromLabel;
        }

        SkuReading? fromBox = Resolve(boxTexts, candidates);
        if (fromBox is { Status: ReadStatus.Matched or ReadStatus.Corrected })
        {
            return fromBox;
        }

        // Ada teks digit tapi tidak bisa dipastikan → UNKNOWN; tidak ada sama sekali → LABEL_NOT_VISIBLE.
        return fromLocated ?? fromLabel ?? fromBox ?? new SkuReading(SkuCodes.LabelNotVisible, null, 0, ReadStatus.LabelNotVisible);
    }

    /// <summary>
    /// Kotak label per kardus dari label locator, dipotong ke batas kardus (tanpa margin). Kosong untuk semua kardus
    /// jika <see cref="VisionOptions.LabelLocatorModel"/> kosong.
    /// </summary>
    private async Task<IReadOnlyList<IReadOnlyList<BoundingBox>>> LocateLabelsAsync(
        VisionImage image, IReadOnlyList<CartonBox> cartons, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_vision.LabelLocatorModel))
        {
            return [.. cartons.Select(_ => (IReadOnlyList<BoundingBox>)[])];
        }

        IReadOnlyList<IReadOnlyList<LabelBox>> found = await _locator
            .LocateAsync(image, [.. cartons.Select(c => c.Box)], cancellationToken)
            .ConfigureAwait(false);
        if (found.Count != cartons.Count)
        {
            return [.. cartons.Select(_ => (IReadOnlyList<BoundingBox>)[])];
        }

        return [.. cartons.Select((carton, i) => (IReadOnlyList<BoundingBox>)[.. found[i].Select(l => Expand(l.Box, carton.Box, 0))])];
    }

    /// <summary>Kotak label lebih tinggi daripada lebarnya = teks label diputar 90° (label mendatar selalu lebar).</summary>
    private static bool IsVertical(BoundingBox label) => label.Height > label.Width;

    /// <summary>
    /// Teks dari label yang diputar: tepat 8 angka asli dalam satu kata (tanda baca di tepi kata dibuang, mis.
    /// "[65067525]" dari bingkai label) dan keyakinan cukup.
    /// </summary>
    private bool IsConfidentRotatedRead(OcrText text) =>
        text.Confidence >= _vision.LabelLocatorRotatedMinConfidence
        && text.Text
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim(LabelFramePunctuation))
            .Any(word => word.Length == SkuMatcher.SkuLength && word.All(char.IsAsciiDigit));

    private static BoundingBox Expand(BoundingBox label, BoundingBox carton, double marginRatio)
    {
        double margin = Math.Min(label.Width, label.Height) * marginRatio;
        return new BoundingBox(
            Math.Max(carton.X1, label.X1 - margin),
            Math.Max(carton.Y1, label.Y1 - margin),
            Math.Min(carton.X2, label.X2 + margin),
            Math.Min(carton.Y2, label.Y2 + margin));
    }

    /// <summary>Semua string digit 7–9 karakter dari teks di dalam kardus (termasuk varian per kata).</summary>
    private static IReadOnlyList<string> DigitVariants(BoundingBox box, IReadOnlyList<OcrText> texts) =>
        [.. texts
            .Where(t => t.Box is BoundingBox position && Contains(box, position.CenterX, position.CenterY))
            .SelectMany(Variants)
            .Select(v => SkuMatcher.NormalizeDigits(v.Candidate))
            .Where(d => d.Length is >= MinSkuDigits and <= MaxSkuDigits)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Potongan nomor (kata yang seluruh karakternya digit atau huruf mirip digit) yang lebih pendek dari SKU,
    /// misalnya "1972453" dari label yang terpotong. Kata seperti "12x700g" tidak dipakai.
    /// </summary>
    private static IReadOnlyList<string> PartialDigits(BoundingBox box, IReadOnlyList<OcrText> texts) =>
        [.. texts
            .Where(t => t.Box is BoundingBox position && Contains(box, position.CenterX, position.CenterY))
            .SelectMany(t => t.Text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
            .Where(word => SkuMatcher.NormalizeDigits(word).Length == word.Length && word.Length <= MaxPartialDigits)
            .Select(SkuMatcher.NormalizeDigits)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Vektor tampilan semua kardus (Vision:AppearanceModel), dipakai koreksi mayoritas berdukungan tampilan dan
    /// inferensi tampilan. Kosong jika embedder nonaktif, atau tidak ada kardus terbaca / yang perlu dibantu.
    /// </summary>
    private async Task<IReadOnlyList<float[]>> EmbedAsync(
        VisionImage image, IReadOnlyList<CartonBox> cartons, IReadOnlyList<SkuReading> readings, CancellationToken cancellationToken)
    {
        bool hasRead = readings.Any(r => r.Status is ReadStatus.Matched or ReadStatus.Corrected);
        bool needsHelp = readings.Select(r => r.Sku).Distinct(StringComparer.Ordinal).Skip(1).Any()
            || readings.Any(r => r.Status is ReadStatus.Unknown or ReadStatus.LabelNotVisible);
        if (string.IsNullOrWhiteSpace(_vision.AppearanceModel) || !hasRead || !needsHelp)
        {
            return [];
        }

        return await _embedder.EmbedAsync(image, [.. cartons.Select(c => c.Box)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Kardus tanpa SKU terbaca diberi SKU kardus lain yang tampilannya sangat mirip (Vision:Appearance*).</summary>
    private IReadOnlyList<SkuReading> InferByAppearance(IReadOnlyList<SkuReading> readings, IReadOnlyList<float[]> embeddings)
    {
        if (embeddings.Count != readings.Count
            || !readings.Any(r => r.Status is ReadStatus.Unknown or ReadStatus.LabelNotVisible))
        {
            return readings;
        }

        IReadOnlyList<SkuReading> inferred = AppearanceInference.Apply(
            readings, embeddings, _vision.AppearanceMinSimilarity, _vision.AppearanceMinReferences, _vision.AppearanceMinMargin);
        LogAppearance(
            _logger,
            inferred.Count(r => r.Status == ReadStatus.InferredByAppearance),
            readings.Count(r => r.Status is ReadStatus.Unknown or ReadStatus.LabelNotVisible));
        return inferred;
    }

    private static bool Contains(BoundingBox box, double x, double y) =>
        x >= box.X1 && x <= box.X2 && y >= box.Y1 && y <= box.Y2;

    /// <summary>
    /// Memilih teks 7–9 digit terbaik. null = tidak ada teks digit sama sekali.
    /// </summary>
    public SkuReading? Resolve(IReadOnlyList<OcrText> texts, IReadOnlyCollection<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(texts);
        SkuReading? bestUnknown = null;
        SkuReading? bestMatch = null;
        int bestDistance = int.MaxValue;
        foreach ((string candidate, string display, double confidence) in texts.SelectMany(Variants))
        {
            int digits = SkuMatcher.NormalizeDigits(candidate).Length;
            if (digits is < MinSkuDigits or > MaxSkuDigits)
            {
                continue;
            }

            SkuMatch match = SkuMatcher.Match(candidate, candidates, _vision.FuzzyMaxDistance);
            if (match.Status == ReadStatus.Unknown)
            {
                if (bestUnknown is null || confidence > bestUnknown.Confidence)
                {
                    bestUnknown = new SkuReading(SkuCodes.Unknown, display, confidence, ReadStatus.Unknown);
                }

                continue;
            }

            bool better = match.Distance < bestDistance
                || (match.Distance == bestDistance && bestMatch is not null && confidence > bestMatch.Confidence);
            if (better)
            {
                bestDistance = match.Distance;
                bestMatch = new SkuReading(match.Sku, display, confidence, match.Status);
            }
        }

        return bestMatch ?? bestUnknown;
    }

    /// <summary>
    /// Setiap kata dicoba sendiri (supaya "SKU 68582213" tidak ikut mengubah huruf S menjadi 5), lalu teks
    /// utuh (untuk OCR yang memecah angka, misalnya "6841 0975"). <c>Display</c> = teks yang dilaporkan di
    /// <c>ocrText</c> (kata asli hasil OCR, bukan hasil koreksi).
    /// </summary>
    private static IEnumerable<(string Candidate, string Display, double Confidence)> Variants(OcrText text)
    {
        string[] words = text.Text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (string word in words)
        {
            yield return (word, word, text.Confidence);

            // Garis kotak di sekitar nomor SKU sering terbaca sebagai "1" di awal/akhir ("657305541").
            string digits = SkuMatcher.NormalizeDigits(word);
            if (digits.Length == SkuMatcher.SkuLength + 1)
            {
                if (digits[0] == '1')
                {
                    yield return (digits[1..], word, text.Confidence);
                }

                if (digits[^1] == '1')
                {
                    yield return (digits[..^1], word, text.Confidence);
                }
            }
        }

        if (words.Length > 1)
        {
            yield return (text.Text, text.Text, text.Confidence);
        }
    }

    private static BoundingBoxDto ToDto(BoundingBox box) => new()
    {
        X1 = (int)Math.Round(box.X1),
        Y1 = (int)Math.Round(box.Y1),
        X2 = (int)Math.Round(box.X2),
        Y2 = (int)Math.Round(box.Y2),
    };

    /// <summary>Jumlah kardus (id unik, bukan jumlah sel) per SKU.</summary>
    private static List<DistinctSkuDto> BuildDistinct(List<CellDto> cells) =>
        [.. cells
            .DistinctBy(c => c.Id)
            .GroupBy(c => c.Sku, StringComparer.Ordinal)
            .Select(g => new DistinctSkuDto { Sku = g.Key, Count = g.Count() })
            .OrderByDescending(d => d.Count)
            .ThenBy(d => d.Sku, StringComparer.Ordinal)];

    private List<string> BuildWarnings(
        FrontLayerResult front, GridLayout layout, List<CellDto> cells, bool majorityCorrected, bool partialCorrected)
    {
        List<string> warnings = [];
        if (front.Kept.Any(c => c.Confidence < _vision.LowConfidenceThreshold))
        {
            warnings.Add(CollectWarnings.LowDetectionConfidence);
        }

        if (cells.Exists(c => c.ReadStatus == ReadStatus.Unknown))
        {
            warnings.Add(CollectWarnings.UnknownSkuPresent);
        }

        if (cells.Exists(c => c.ReadStatus == ReadStatus.LabelNotVisible))
        {
            warnings.Add(CollectWarnings.LabelNotVisiblePresent);
        }

        if (layout.IsIrregular)
        {
            warnings.Add(CollectWarnings.IrregularGrid);
        }

        if (layout.Paths.Truncated)
        {
            warnings.Add(CollectWarnings.StackPathsTruncated);
        }

        if (front.ExcludedCount > 0)
        {
            warnings.Add(CollectWarnings.BackLayerExcluded);
        }

        if (majorityCorrected)
        {
            warnings.Add(CollectWarnings.SkuCorrectedByMajority);
        }

        if (partialCorrected)
        {
            warnings.Add(CollectWarnings.SkuCorrectedByPartialRead);
        }

        if (cells.Exists(c => c.ReadStatus == ReadStatus.InferredByAppearance))
        {
            warnings.Add(CollectWarnings.SkuInferredByAppearance);
        }

        return warnings;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Collect SKU timing: decode {DecodeMs} ms, inspect {InspectMs} ms, detect {DetectMs} ms, label {LabelLocateMs} ms, ocr {OcrMs} ms, sku {SkuResolveMs} ms ({Cartons} kardus, {Labels} label, {Texts} teks)")]
    private static partial void LogTimings(
        ILogger logger, long decodeMs, long inspectMs, long detectMs, long labelLocateMs, long ocrMs, long skuResolveMs, int cartons, int labels, int texts);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Fast reject: gate {Gate} setelah {ElapsedMs} ms ({Cartons} kardus terdeteksi)")]
    private static partial void LogFastReject(ILogger logger, string gate, long elapsedMs, int cartons);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Appearance inference: {Inferred} dari {Unread} kardus tanpa SKU terbaca diisi dari kardus mirip")]
    private static partial void LogAppearance(ILogger logger, int inferred, int unread);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "OCR recognizer {Model}: {Lines} kotak label, {Read} terbaca, {Used} dipakai (keyakinan ≥ {MinConfidence}), {Cartons}/{Total} kardus terbaca, {ElapsedMs} ms")]
    private static partial void LogRecognizer(
        ILogger logger, string model, int lines, int read, int used, double minConfidence, int cartons, int total, long elapsedMs);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "OCR tanpa Paddle (Vision:PaddleOcrFallback = false): {Unread} kardus belum terbaca diserahkan ke koreksi mayoritas, potongan nomor, dan inferensi tampilan")]
    private static partial void LogNoPaddle(ILogger logger, int unread);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Hasil akhir per kardus: Matched {Matched}, Corrected {Corrected}, InferredByAppearance {Inferred}, Unknown {Unknown}, LabelNotVisible {NotVisible}")]
    private static partial void LogResult(ILogger logger, int matched, int corrected, int inferred, int unknown, int notVisible);
}
