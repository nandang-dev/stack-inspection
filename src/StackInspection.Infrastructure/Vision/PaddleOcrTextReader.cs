using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Domain;

namespace StackInspection.Infrastructure.Vision;

/// <summary>
/// OCR PaddleOCR PP-OCRv5 (model lokal, CPU/oneDNN). <see cref="PaddleOcrAll"/> tidak thread-safe,
/// jadi semua akses diserialisasi dengan <see cref="SemaphoreSlim"/>(1, 1).
/// </summary>
/// <remarks>
/// Label SKU dibaca sekali per foto: area yang diminta (biasanya area label setiap kardus) dikemas rapat ke
/// beberapa halaman <see cref="TileSize"/> × <see cref="TileSize"/> di resolusi asli (<see cref="OcrPacker"/>) →
/// deteksi teks per halaman → teks yang berada di dalam area dikenali sekaligus dalam batch (potongan lurus dari
/// foto asli). Yang diproses hanya luas area itu sendiri, bukan seluruh muatan, dan jauh lebih cepat daripada OCR
/// penuh per kardus (ratusan panggilan dengan ukuran input berbeda-beda).
/// </remarks>
public sealed partial class PaddleOcrTextReader : ISkuLabelReader, ITextSpotter, IDisposable
{
    /// <summary>Strip stempel kamera cukup dibaca di resolusi rendah supaya cepat.</summary>
    private const int SpotMaxWidth = 1280;

    /// <summary>Ukuran halaman deteksi teks (di bawah MaxSize detector, jadi tidak diperkecil).</summary>
    public const int TileSize = 960;

    /// <summary>Celah putih antar potongan di satu halaman agar teks dari potongan berbeda tidak menyatu.</summary>
    private const int PieceGap = 16;

    private const int AreaMargin = 16;
    private const int RecognizeBatchSize = 16;
    private const double DuplicateContainment = 0.6;

    private readonly PaddleOcrAll _ocr;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _tileOverlap;
    private readonly double _maxUpscale;
    private readonly float? _nativeBoxScore;
    private readonly float _enlargedBoxScore;
    private readonly ILogger<PaddleOcrTextReader> _logger;

    public PaddleOcrTextReader(IOptions<VisionOptions> options, ILogger<PaddleOcrTextReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _tileOverlap = options.Value.OcrTileOverlap;
        _maxUpscale = options.Value.OcrMaxUpscale;
        _logger = logger;
        _ocr = new PaddleOcrAll(LocalFullModels.ChineseV5, PaddleDevice.OneDnn(cpuMathThreadCount: options.Value.Threads))
        {
            AllowRotateDetection = false,
            Enable180Classification = false,
        };
        _nativeBoxScore = _ocr.Detector.BoxScoreThreahold;
        _enlargedBoxScore = (float)options.Value.OcrBoxScoreThreshold;
    }

    public async Task<IReadOnlyList<OcrText>> ReadAllAsync(
        VisionImage image, IReadOnlyList<BoundingBox> regions, int minAreaWidth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
        {
            return [];
        }

        OpenCvImage source = OpenCvImage.From(image);
        OcrRect[] areas = [.. regions.Select(r => Clamp(r, AreaMargin, source.Width, source.Height)).Where(r => r.Width > 0 && r.Height > 0)];
        bool enlarged = minAreaWidth > 0;
        int minWidth = Math.Max(0, minAreaWidth);
        OcrPacking packing = OcrPacker.Pack(areas, TileSize, PieceGap, _tileOverlap, minWidth, _maxUpscale);
        if (packing.PageCount == 0)
        {
            return [];
        }

        int upscaled = areas.Count(a => OcrPacker.UpscaleOf(a.Width, minWidth, _maxUpscale) > 1);

        long waitStart = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan waited = Stopwatch.GetElapsedTime(waitStart);
            // aman diubah per panggilan: akses detektor diserialisasi oleh _gate
            _ocr.Detector.BoxScoreThreahold = enlarged ? _enlargedBoxScore : _nativeBoxScore;
            long detectStart = Stopwatch.GetTimestamp();
            List<RotatedRect> found = [];
            foreach (IGrouping<int, OcrPiece> page in packing.Pieces.GroupBy(p => p.Page))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Mat input = BuildPage(source.Mat, page);
                foreach (RotatedRect rect in _ocr.Detector.Run(input))
                {
                    // kembalikan ke koordinat foto lewat potongan yang memuat titik tengahnya (dibagi skala perbesaran)
                    OcrPiece? piece = page.FirstOrDefault(p =>
                        rect.Center.X >= p.X && rect.Center.X < p.X + p.Width
                        && rect.Center.Y >= p.Y && rect.Center.Y < p.Y + p.Height);
                    if (piece is not null)
                    {
                        float scale = (float)piece.Scale;
                        found.Add(new RotatedRect(
                            new Point2f(((rect.Center.X - piece.X) / scale) + piece.Source.X, ((rect.Center.Y - piece.Y) / scale) + piece.Source.Y),
                            new Size2f(rect.Size.Width / scale, rect.Size.Height / scale),
                            rect.Angle));
                    }
                }
            }

            TimeSpan detectTime = Stopwatch.GetElapsedTime(detectStart);
            // hanya teks yang titik tengahnya di dalam area, lalu buang duplikat dari tile yang tumpang tindih
            List<(RotatedRect Rect, BoundingBox Box)> inside = [.. found
                .Select(r => (Rect: r, Box: ToBox(r.BoundingRect())))
                .Where(t => regions.Any(region => Contains(region, t.Box.CenterX, t.Box.CenterY)))];
            IReadOnlyList<int> kept = OcrTiling.Deduplicate([.. inside.Select(t => t.Box)], DuplicateContainment);
            if (kept.Count == 0)
            {
                LogOcr(_logger, minWidth, regions.Count, upscaled, packing.PageCount, (long)waited.TotalMilliseconds, (long)detectTime.TotalMilliseconds, 0, 0);
                return [];
            }

            long recognizeStart = Stopwatch.GetTimestamp();
            // Potongan lurus (bounding rect), sama seperti PaddleOcrAll.Run dengan AllowRotateDetection=false.
            // GetRotateCropImage menghasilkan teks terbalik atas-bawah untuk sebagian kotak (5→2, 6→9).
            Mat[] crops = [.. kept.Select(i => source.Mat[inside[i].Rect.BoundingRect() & new Rect(0, 0, source.Width, source.Height)].Clone())];
            try
            {
                PaddleOcrRecognizerResult[] results = _ocr.Recognizer.Run(crops, RecognizeBatchSize);
                List<OcrText> texts = [];
                for (int i = 0; i < results.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(results[i].Text))
                    {
                        texts.Add(new OcrText(results[i].Text, results[i].Score, inside[kept[i]].Box));
                    }
                }

                LogOcr(
                    _logger,
                    minWidth,
                    regions.Count,
                    upscaled,
                    packing.PageCount,
                    (long)waited.TotalMilliseconds,
                    (long)detectTime.TotalMilliseconds,
                    (long)Stopwatch.GetElapsedTime(recognizeStart).TotalMilliseconds,
                    texts.Count);
                return texts;
            }
            finally
            {
                foreach (Mat mat in crops)
                {
                    mat.Dispose();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<OcrText>> SpotAsync(VisionImage image, BoundingBox region, CancellationToken cancellationToken)
    {
        using Mat? crop = OpenCvImage.From(image).Crop(region);
        if (crop is null)
        {
            return [];
        }

        using Mat input = Shrink(crop, SpotMaxWidth);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ocr.Detector.BoxScoreThreahold = _nativeBoxScore;
            PaddleOcrResult result = _ocr.Run(input);
            return [.. result.Regions
                .Where(r => !string.IsNullOrWhiteSpace(r.Text))
                .Select(r => new OcrText(r.Text, r.Score))];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Halaman putih <see cref="TileSize"/> × <see cref="TileSize"/> berisi potongan-potongan foto (ukuran input
    /// detector selalu sama); potongan ber-<see cref="OcrPiece.Scale"/> &gt; 1 diperbesar (bicubic).
    /// </summary>
    private static Mat BuildPage(Mat source, IEnumerable<OcrPiece> pieces)
    {
        Mat page = new(new Size(TileSize, TileSize), source.Type(), Scalar.All(255));
        foreach (OcrPiece piece in pieces)
        {
            using Mat from = new(source, new Rect(piece.Source.X, piece.Source.Y, piece.Source.Width, piece.Source.Height));
            using Mat to = new(page, new Rect(piece.X, piece.Y, piece.Width, piece.Height));
            if (piece.Width == piece.Source.Width && piece.Height == piece.Source.Height)
            {
                from.CopyTo(to);
            }
            else
            {
                using Mat resized = new();
                Cv2.Resize(from, resized, new Size(piece.Width, piece.Height), 0, 0, InterpolationFlags.Cubic);
                resized.CopyTo(to);
            }
        }

        return page;
    }

    /// <summary>Area + margin, dibatasi ke dalam foto.</summary>
    private static OcrRect Clamp(BoundingBox region, int margin, int width, int height)
    {
        int x1 = Math.Clamp((int)Math.Floor(region.X1) - margin, 0, width);
        int y1 = Math.Clamp((int)Math.Floor(region.Y1) - margin, 0, height);
        int x2 = Math.Clamp((int)Math.Ceiling(region.X2) + margin, 0, width);
        int y2 = Math.Clamp((int)Math.Ceiling(region.Y2) + margin, 0, height);
        return new OcrRect(x1, y1, x2 - x1, y2 - y1);
    }

    private static Mat Shrink(Mat crop, int maxWidth)
    {
        if (crop.Width > maxWidth)
        {
            Mat resized = new();
            double scale = (double)maxWidth / crop.Width;
            Cv2.Resize(crop, resized, new Size(maxWidth, (int)(crop.Height * scale)), 0, 0, InterpolationFlags.Area);
            return resized;
        }

        return crop.Clone(); // salinan kontinu dari ROI
    }

    private static BoundingBox ToBox(Rect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static bool Contains(BoundingBox box, double x, double y) =>
        x >= box.X1 && x <= box.X2 && y >= box.Y1 && y <= box.Y2;

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "OCR label (min {MinWidth} px): {Regions} area ({Upscaled} diperbesar) → {Pages} halaman deteksi, antre {WaitMs} ms, deteksi {DetectMs} ms, pengenalan {RecognizeMs} ms, {Texts} teks")]
    private static partial void LogOcr(
        ILogger logger, int minWidth, int regions, int upscaled, int pages, long waitMs, long detectMs, long recognizeMs, int texts);

    public void Dispose()
    {
        _ocr.Dispose();
        _gate.Dispose();
    }
}
