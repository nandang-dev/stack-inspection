using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackInspection.Application.Analyze;
using StackInspection.Application.CollectSku;
using StackInspection.Application.Configuration;
using StackInspection.Application.Inspectability;
using StackInspection.Application.Models;
using StackInspection.Domain;

namespace StackInspection.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<VisionOptions>().Bind(configuration.GetSection(VisionOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PhotoOptions>().Bind(configuration.GetSection(PhotoOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<FrontLayerOptions>().Bind(configuration.GetSection(FrontLayerOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<UploadOptions>().Bind(configuration.GetSection(UploadOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();

        services.AddSingleton(StackScoringService.CreateDefault());
        services.AddSingleton<IPhotoInspectabilityCheck, ResolutionCheck>();
        services.AddSingleton<IPhotoInspectabilityCheck, CameraOverlayCheck>();
        services.AddSingleton<PhotoInspectabilityService>();
        services.AddScoped<CollectSkuHandler>();
        services.AddScoped<AnalyzeStackHandler>();
        services.AddScoped<ListModelsHandler>();
        return services;
    }
}
