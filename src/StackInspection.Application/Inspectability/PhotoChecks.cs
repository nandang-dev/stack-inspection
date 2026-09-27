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

/// <summary>Gate 2: tolak foto dengan stempel aplikasi kamera di strip atas/bawah.</summary>
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

    public int Order => 2;

    public async Task CheckAsync(VisionImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
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
