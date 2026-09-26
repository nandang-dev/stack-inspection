using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Infrastructure.Fakes;
using StackInspection.Infrastructure.Vision;

namespace StackInspection.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Vision asli (ONNX Runtime + OpenCV + PaddleOCR) atau fake dari fixture JSON, sesuai <c>Vision:UseFake</c>.
    /// Pilihan dibuat saat service pertama kali dipakai (konfigurasi final), sehingga model dan library
    /// native hanya dimuat jika memang dipakai.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        _ = configuration;

        services.AddSingleton<FakeVisionFixtureProvider>();
        services.AddSingleton<FakeImageDecoder>();
        services.AddSingleton<FakeCartonDetector>();
        services.AddSingleton<FakeSkuLabelReader>();
        services.AddSingleton<FakeTextSpotter>();
        services.AddSingleton<FakeModelCatalog>();

        services.AddSingleton<OpenCvImageDecoder>();
        services.AddSingleton<OnnxCartonDetector>();
        services.AddSingleton<PaddleOcrTextReader>();
        services.AddSingleton<FileSystemModelCatalog>();

        services.AddSingleton<IModelCatalog>(sp => UseFake(sp)
            ? sp.GetRequiredService<FakeModelCatalog>()
            : sp.GetRequiredService<FileSystemModelCatalog>());

        services.AddSingleton<IImageDecoder>(sp => UseFake(sp)
            ? sp.GetRequiredService<FakeImageDecoder>()
            : sp.GetRequiredService<OpenCvImageDecoder>());
        services.AddSingleton<ICartonDetector>(sp => UseFake(sp)
            ? sp.GetRequiredService<FakeCartonDetector>()
            : sp.GetRequiredService<OnnxCartonDetector>());
        services.AddSingleton<ISkuLabelReader>(sp => UseFake(sp)
            ? sp.GetRequiredService<FakeSkuLabelReader>()
            : sp.GetRequiredService<PaddleOcrTextReader>());
        services.AddSingleton<ITextSpotter>(sp => UseFake(sp)
            ? sp.GetRequiredService<FakeTextSpotter>()
            : sp.GetRequiredService<PaddleOcrTextReader>());
        return services;
    }

    private static bool UseFake(IServiceProvider services) =>
        services.GetRequiredService<IOptions<VisionOptions>>().Value.UseFake;
}
