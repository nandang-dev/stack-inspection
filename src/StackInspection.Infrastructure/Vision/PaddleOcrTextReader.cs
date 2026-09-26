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
/// Label SKU dibaca sekali per foto: area semua kardus dipotong menjadi tile berukuran tetap di
/// resolusi asli → deteksi teks per tile → teks yang berada di dalam kardus dikenali sekaligus dalam
/// batch. Jauh lebih cepat daripada menjalankan OCR penuh untuk setiap kardus (ratusan panggilan dengan
/// ukuran input berbeda-beda).
/// </remarks>
public sealed class PaddleOcrTextReader : ISkuLabelReader, ITextSpotter, IDisposable
{
    /// <summary>Strip stempel kamera cukup dibaca di resolusi rendah supaya cepat.</summary>
    private const int SpotMaxWidth = 1280;

    /// <summary>Ukuran tile deteksi teks (di bawah MaxSize detector, jadi tidak diperkecil).</summary>
    public const int TileSize = 960;

    /// <summary>Tumpang tindih antar tile; harus lebih lebar dari satu baris teks SKU agar ada tile yang memuatnya utuh.</summary>
    public const int TileOverlap = 320;

    private const int AreaMargin = 16;
    private const int RecognizeBatchSize = 16;
    private const double DuplicateContainment = 0.6;

    private readonly PaddleOcrAll _ocr;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PaddleOcrTextReader(IOptions<VisionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ocr = new PaddleOcrAll(LocalFullModels.ChineseV5, PaddleDevice.OneDnn(cpuMathThreadCount: options.Value.Threads))
        {
            AllowRotateDetection = false,
            Enable180Classification = false,
        };
    }

    public async Task<IReadOnlyList<OcrText>> ReadAllAsync(
        VisionImage image, IReadOnlyList<BoundingBox> regions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
        {
            return [];
        }

        OpenCvImage source = OpenCvImage.From(image);
        BoundingBox area = OcrTiling.Union(regions, AreaMargin, source.Width, source.Height);
        int offsetX = (int)Math.Floor(area.X1);
        int offsetY = (int)Math.Floor(area.Y1);
        using Mat? crop = source.Crop(area);
        if (crop is null)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<RotatedRect> found = [];
            foreach (OcrTile tile in OcrTiling.Tiles(crop.Width, crop.Height, TileSize, TileOverlap))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Mat input = PadTile(crop, tile);
                foreach (RotatedRect rect in _ocr.Detector.Run(input))
                {
                    found.Add(new RotatedRect(
                        new Point2f(rect.Center.X + tile.X + offsetX, rect.Center.Y + tile.Y + offsetY), rect.Size, rect.Angle));
                }
            }

            // hanya teks yang titik tengahnya di dalam kardus, lalu buang duplikat dari tile yang tumpang tindih
            List<(RotatedRect Rect, BoundingBox Box)> inside = [.. found
                .Select(r => (Rect: r, Box: ToBox(r.BoundingRect())))
                .Where(t => regions.Any(region => Contains(region, t.Box.CenterX, t.Box.CenterY)))];
            IReadOnlyList<int> kept = OcrTiling.Deduplicate([.. inside.Select(t => t.Box)], DuplicateContainment);
            if (kept.Count == 0)
            {
                return [];
            }

            Mat[] crops = [.. kept.Select(i => PaddleOcrAll.GetRotateCropImage(source.Mat, inside[i].Rect))];
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

    /// <summary>Tile tepi diberi padding sampai ukuran penuh supaya bentuk input detector selalu sama.</summary>
    private static Mat PadTile(Mat crop, OcrTile tile)
    {
        using Mat roi = new(crop, new Rect(tile.X, tile.Y, tile.Width, tile.Height));
        Mat padded = new();
        Cv2.CopyMakeBorder(
            roi, padded, 0, TileSize - tile.Height, 0, TileSize - tile.Width, BorderTypes.Constant, Scalar.All(255));
        return padded;
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

    public void Dispose()
    {
        _ocr.Dispose();
        _gate.Dispose();
    }
}
