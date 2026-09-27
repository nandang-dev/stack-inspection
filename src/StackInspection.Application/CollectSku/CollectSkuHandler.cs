using System.Diagnostics;
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
/// filter lapisan depan → OCR sekali untuk semua kardus → teks dicocokkan ke kardus berdasarkan posisi
/// (area label dulu, fallback seluruh kardus) → fuzzy match → grid.
/// </summary>
public sealed class CollectSkuHandler
{
    private const int MinSkuDigits = 7;
    private const int MaxSkuDigits = 9;
    private static readonly char[] WordSeparators = [' ', '\t', ':', ';', ',', '/'];

    private readonly IImageDecoder _decoder;
    private readonly ICartonDetector _detector;
    private readonly IModelCatalog _models;
    private readonly ISkuLabelReader _reader;
    private readonly PhotoInspectabilityService _inspectability;
    private readonly VisionOptions _vision;
    private readonly FrontLayerOptions _frontLayer;
    private readonly UploadOptions _upload;

    public CollectSkuHandler(
        IImageDecoder decoder,
        ICartonDetector detector,
        IModelCatalog models,
        ISkuLabelReader reader,
        PhotoInspectabilityService inspectability,
        IOptions<VisionOptions> vision,
        IOptions<FrontLayerOptions> frontLayer,
        IOptions<UploadOptions> upload)
    {
        ArgumentNullException.ThrowIfNull(vision);
        ArgumentNullException.ThrowIfNull(frontLayer);
        ArgumentNullException.ThrowIfNull(upload);
        _decoder = decoder;
        _detector = detector;
        _models = models;
        _reader = reader;
        _inspectability = inspectability;
        _vision = vision.Value;
        _frontLayer = frontLayer.Value;
        _upload = upload.Value;
    }

    public async Task<CollectSkuResponse> HandleAsync(CollectSkuCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Stopwatch stopwatch = Stopwatch.StartNew();
        ValidateUpload(command.Image);
        string[] candidates = NormalizeCandidates(command.CandidateSkus);
        DetectionModel model = _models.Resolve(command.Model);

        using VisionImage image = _decoder.Decode(command.Image);
        await _inspectability.EnsureInspectableAsync(image, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CartonBox> detected = await _detector.DetectAsync(image, model, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CartonBox> distinct = ContainmentFilter.Apply(detected, _vision.ContainmentThreshold);
        FrontLayerResult front = FrontLayerFilter.Apply(distinct, _frontLayer.MinWidthRatio, _frontLayer.MaxGapRatio);
        if (front.Kept.Count == 0)
        {
            throw new PhotoNotInspectableException(
                PhotoRejectReasons.NoCartonDetected,
                "Tidak ada kardus yang terdeteksi di lapisan depan foto.");
        }

        IReadOnlyList<OcrText> texts = await _reader
            .ReadAllAsync(image, [.. front.Kept.Select(c => c.Box)], cancellationToken)
            .ConfigureAwait(false);
        List<SkuReading> readings = [.. front.Kept.Select(c => ReadSku(c.Box, texts, candidates))];

        GridLayout layout = GridBuilder.Build([.. front.Kept.Select(c => c.Box)]);
        List<CellDto> cells = [];
        for (int i = 0; i < front.Kept.Count; i++)
        {
            CartonBox carton = front.Kept[i];
            GridPosition position = layout.Positions[i];
            SkuReading reading = readings[i];
            cells.Add(new CellDto
            {
                Row = position.Row,
                Column = position.Column,
                Level = position.Level,
                Sku = reading.Sku,
                OcrText = reading.OcrText,
                OcrConfidence = Math.Round(reading.Confidence, 4),
                DetectionConfidence = Math.Round(carton.Confidence, 4),
                ReadStatus = reading.Status,
                BoundingBox = ToDto(carton.Box),
            });
        }

        cells = [.. cells.OrderBy(c => c.Row).ThenBy(c => c.Column)];
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
            Stacks = BuildStacks(cells),
            DistinctSkus = BuildDistinct(cells),
            Warnings = BuildWarnings(front, layout, cells),
            ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
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

    private static string[] NormalizeCandidates(IReadOnlyList<string> candidates) =>
        [.. candidates
            .SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(SkuMatcher.NormalizeDigits)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>Area label: 60% lebar kiri × 30% tinggi atas dari bbox (rasio dari konfigurasi).</summary>
    public static BoundingBox LabelRegion(BoundingBox box, double widthRatio, double heightRatio) =>
        new(box.X1, box.Y1, box.X1 + (box.Width * widthRatio), box.Y1 + (box.Height * heightRatio));

    /// <summary>
    /// Teks yang titik tengahnya di area label dipakai lebih dulu; teks lain di dalam kardus sebagai fallback.
    /// </summary>
    private SkuReading ReadSku(BoundingBox box, IReadOnlyList<OcrText> texts, string[] candidates)
    {
        BoundingBox label = LabelRegion(box, _vision.LabelCropWidthRatio, _vision.LabelCropHeightRatio);
        List<OcrText> labelTexts = [];
        List<OcrText> boxTexts = [];
        foreach (OcrText text in texts)
        {
            if (text.Box is not BoundingBox position || !Contains(box, position.CenterX, position.CenterY))
            {
                continue;
            }

            (Contains(label, position.CenterX, position.CenterY) ? labelTexts : boxTexts).Add(text);
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
        return fromLabel ?? fromBox ?? new SkuReading(SkuCodes.LabelNotVisible, null, 0, ReadStatus.LabelNotVisible);
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
        foreach (OcrText text in texts.SelectMany(Variants))
        {
            int digits = SkuMatcher.NormalizeDigits(text.Text).Length;
            if (digits is < MinSkuDigits or > MaxSkuDigits)
            {
                continue;
            }

            SkuMatch match = SkuMatcher.Match(text.Text, candidates, _vision.FuzzyMaxDistance);
            if (match.Status == ReadStatus.Unknown)
            {
                if (bestUnknown is null || text.Confidence > bestUnknown.Confidence)
                {
                    bestUnknown = new SkuReading(SkuCodes.Unknown, text.Text, text.Confidence, ReadStatus.Unknown);
                }

                continue;
            }

            bool better = match.Distance < bestDistance
                || (match.Distance == bestDistance && bestMatch is not null && text.Confidence > bestMatch.Confidence);
            if (better)
            {
                bestDistance = match.Distance;
                bestMatch = new SkuReading(match.Sku, text.Text, text.Confidence, match.Status);
            }
        }

        return bestMatch ?? bestUnknown;
    }

    /// <summary>
    /// Setiap kata dicoba sendiri (supaya "SKU 68582213" tidak ikut mengubah huruf S menjadi 5), lalu teks
    /// utuh (untuk OCR yang memecah angka, misalnya "6841 0975").
    /// </summary>
    private static IEnumerable<OcrText> Variants(OcrText text)
    {
        string[] words = text.Text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (string word in words)
        {
            yield return text with { Text = word };
        }

        if (words.Length > 1)
        {
            yield return text;
        }
    }

    private static BoundingBoxDto ToDto(BoundingBox box) => new()
    {
        X1 = (int)Math.Round(box.X1),
        Y1 = (int)Math.Round(box.Y1),
        X2 = (int)Math.Round(box.X2),
        Y2 = (int)Math.Round(box.Y2),
    };

    private static List<StackDto> BuildStacks(List<CellDto> cells) =>
        [.. cells
            .GroupBy(c => c.Column)
            .OrderBy(g => g.Key)
            .Select(g => new StackDto
            {
                Column = g.Key,
                Height = g.Count(),
                SkusBottomToTop = [.. g.OrderBy(c => c.Level).Select(c => c.Sku)],
            })];

    private static List<DistinctSkuDto> BuildDistinct(List<CellDto> cells) =>
        [.. cells
            .GroupBy(c => c.Sku, StringComparer.Ordinal)
            .Select(g => new DistinctSkuDto { Sku = g.Key, Count = g.Count() })
            .OrderByDescending(d => d.Count)
            .ThenBy(d => d.Sku, StringComparer.Ordinal)];

    private List<string> BuildWarnings(FrontLayerResult front, GridLayout layout, List<CellDto> cells)
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

        if (front.ExcludedCount > 0)
        {
            warnings.Add(CollectWarnings.BackLayerExcluded);
        }

        return warnings;
    }
}
