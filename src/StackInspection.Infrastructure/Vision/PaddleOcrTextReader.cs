using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;
using StackInspection.Application.Abstractions;
using StackInspection.Domain;

namespace StackInspection.Infrastructure.Vision;

/// <summary>
/// OCR PaddleOCR PP-OCRv5 (model lokal, CPU/MKL-DNN). <see cref="PaddleOcrAll"/> tidak thread-safe,
/// jadi semua akses diserialisasi dengan <see cref="SemaphoreSlim"/>(1, 1).
/// </summary>
public sealed class PaddleOcrTextReader : ISkuLabelReader, ITextSpotter, IDisposable
{
    /// <summary>Strip stempel kamera cukup dibaca di resolusi rendah supaya cepat.</summary>
    private const int SpotMaxWidth = 1280;

    private readonly PaddleOcrAll _ocr;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PaddleOcrTextReader()
    {
        _ocr = new PaddleOcrAll(LocalFullModels.ChineseV5, PaddleDevice.Mkldnn())
        {
            AllowRotateDetection = false,
            Enable180Classification = false,
        };
    }

    public Task<IReadOnlyList<OcrText>> ReadAsync(VisionImage image, BoundingBox region, CancellationToken cancellationToken) =>
        RunAsync(image, region, maxWidth: null, cancellationToken);

    public Task<IReadOnlyList<OcrText>> SpotAsync(VisionImage image, BoundingBox region, CancellationToken cancellationToken) =>
        RunAsync(image, region, SpotMaxWidth, cancellationToken);

    private async Task<IReadOnlyList<OcrText>> RunAsync(
        VisionImage image, BoundingBox region, int? maxWidth, CancellationToken cancellationToken)
    {
        using Mat? crop = OpenCvImage.From(image).Crop(region);
        if (crop is null)
        {
            return [];
        }

        using Mat input = Prepare(crop, maxWidth);
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

    private static Mat Prepare(Mat crop, int? maxWidth)
    {
        if (maxWidth is int limit && crop.Width > limit)
        {
            Mat resized = new();
            double scale = (double)limit / crop.Width;
            Cv2.Resize(crop, resized, new Size(limit, (int)(crop.Height * scale)), 0, 0, InterpolationFlags.Area);
            return resized;
        }

        return crop.Clone(); // salinan kontinu dari ROI
    }

    public void Dispose()
    {
        _ocr.Dispose();
        _gate.Dispose();
    }
}
