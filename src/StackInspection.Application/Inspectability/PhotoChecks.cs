using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Application.Exceptions;
using StackInspection.Domain;

namespace StackInspection.Application.Inspectability;

/// <summary>Satu pemeriksaan kelayakan foto. <see cref="Order"/> menentukan urutan (kecil lebih dulu).</summary>
public interface IPhotoInspectabilityCheck
{
    int Order { get; }

    /// <exception cref="PhotoNotInspectableException">Foto tidak layak.</exception>
    Task CheckAsync(VisionImage image, CancellationToken cancellationToken);
}

/// <summary>Gate 1: sisi terpanjang ≥ Photo:MinLongSide <b>dan</b> jumlah piksel ≥ Photo:MinMegapixels.</summary>
public sealed class ResolutionCheck : IPhotoInspectabilityCheck
{
    private readonly PhotoOptions _options;

    public ResolutionCheck(IOptions<PhotoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public int Order => 1;

    public Task CheckAsync(VisionImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        int longSide = Math.Max(image.Width, image.Height);
        double megapixels = (double)image.Width * image.Height / 1_000_000;
        if (longSide < _options.MinLongSide || megapixels < _options.MinMegapixels)
        {
            throw new PhotoNotInspectableException(
                PhotoRejectReasons.ResolutionTooLow,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Resolusi foto {image.Width}x{image.Height} ({megapixels:0.##} MP) di bawah minimum (sisi terpanjang {_options.MinLongSide} px dan {_options.MinMegapixels:0.##} MP)."));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Gate 2: classifier kelayakan foto (fast reject). Tolak jika p(tolak) ≥ Photo:ClassifierRejectThreshold,
/// tanpa alasan spesifik (<see cref="PhotoRejectReasons.FastRejected"/>); di bawah ambang (ragu) foto lolos.
/// Hanya berjalan jika <see cref="PhotoOptions.ClassifierModel"/> diisi. Skor setiap foto dicatat di log.
/// </summary>
public sealed partial class InspectabilityClassifierCheck : IPhotoInspectabilityCheck
{
    private readonly IPhotoClassifier _classifier;
    private readonly PhotoOptions _options;
    private readonly ILogger<InspectabilityClassifierCheck> _logger;

    public InspectabilityClassifierCheck(
        IPhotoClassifier classifier, IOptions<PhotoOptions> options, ILogger<InspectabilityClassifierCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _classifier = classifier;
        _options = options.Value;
        _logger = logger;
    }

    public int Order => 2;

    public async Task CheckAsync(VisionImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (string.IsNullOrWhiteSpace(_options.ClassifierModel))
        {
            return;
        }

        double pReject = await _classifier.RejectProbabilityAsync(image, cancellationToken).ConfigureAwait(false);
        bool rejected = pReject >= _options.ClassifierRejectThreshold;
        LogScore(_logger, _options.ClassifierModel, pReject, _options.ClassifierRejectThreshold, rejected);
        if (rejected)
        {
            throw new PhotoNotInspectableException(
                PhotoRejectReasons.FastRejected,
                "Foto tidak layak dinilai. Ambil ulang foto muatan dari depan, cukup dekat, dan jelas.");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Fast reject classifier {Model}: p(tolak) {PReject:0.0000}, ambang {Threshold}, ditolak {Rejected}")]
    private static partial void LogScore(ILogger logger, string model, double pReject, double threshold, bool rejected);
}

/// <summary>
/// Gate 3: tolak foto dengan stempel aplikasi kamera di strip atas/bawah.
/// Hanya berjalan jika <see cref="PhotoOptions.CameraOverlayEnabled"/>.
/// </summary>
public sealed class CameraOverlayCheck : IPhotoInspectabilityCheck
{
    private readonly ITextSpotter _spotter;
    private readonly PhotoOptions _options;

    public CameraOverlayCheck(ITextSpotter spotter, IOptions<PhotoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _spotter = spotter;
        _options = options.Value;
    }

    public int Order => 3;

    public async Task CheckAsync(VisionImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!_options.CameraOverlayEnabled)
        {
            return;
        }

        double strip = image.Height * _options.OverlayScanRatio;
        BoundingBox top = new(0, 0, image.Width, strip);
        BoundingBox bottom = new(0, image.Height - strip, image.Width, image.Height);

        List<OcrText> texts = [];
        texts.AddRange(await _spotter.SpotAsync(image, top, cancellationToken).ConfigureAwait(false));
        texts.AddRange(await _spotter.SpotAsync(image, bottom, cancellationToken).ConfigureAwait(false));

        string[] hits = FindKeywords(texts, _options.EffectiveOverlayKeywords);
        if (hits.Length >= _options.OverlayMinKeywordHits)
        {
            throw new PhotoNotInspectableException(
                PhotoRejectReasons.CameraOverlayDetected,
                $"Foto memiliki stempel aplikasi kamera ({string.Join(", ", hits)}). Ambil foto tanpa stempel.");
        }
    }

    /// <summary>Kata kunci yang ditemukan (tanpa membedakan huruf besar/kecil).</summary>
    public static string[] FindKeywords(IEnumerable<OcrText> texts, IEnumerable<string> keywords)
    {
        string joined = string.Join(' ', texts.Select(t => t.Text));
        return [.. keywords.Where(k => joined.Contains(k, StringComparison.OrdinalIgnoreCase))];
    }
}

/// <summary>Menjalankan semua pemeriksaan kelayakan berurutan.</summary>
public sealed class PhotoInspectabilityService
{
    private readonly IReadOnlyList<IPhotoInspectabilityCheck> _checks;

    public PhotoInspectabilityService(IEnumerable<IPhotoInspectabilityCheck> checks)
    {
        _checks = [.. checks.OrderBy(c => c.Order)];
    }

    public async Task EnsureInspectableAsync(VisionImage image, CancellationToken cancellationToken)
    {
        foreach (IPhotoInspectabilityCheck check in _checks)
        {
            await check.CheckAsync(image, cancellationToken).ConfigureAwait(false);
        }
    }
}
