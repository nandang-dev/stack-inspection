using StackInspection.Application.Abstractions;
using StackInspection.Application.Contracts;

namespace StackInspection.Application.Models;

/// <summary>Daftar model deteksi yang tersedia (untuk <c>GET /api/v1/models</c> dan dropdown Swagger).</summary>
public sealed class ListModelsHandler(IModelCatalog catalog)
{
    public IReadOnlyList<ModelInfoDto> Handle() =>
        [.. catalog.List().Select(m => new ModelInfoDto
        {
            Name = m.Name,
            IsDefault = string.Equals(m.Name, catalog.DefaultModelName, StringComparison.Ordinal),
            InputSize = m.InputSize,
            ConfidenceThreshold = m.ConfidenceThreshold,
            IouThreshold = m.IouThreshold,
            Status = m.Status,
            BaseModel = m.BaseModel,
            DatasetVersion = m.DatasetVersion,
            TrainedAt = m.TrainedAt,
            Metrics = m.Metrics is null
                ? null
                : new ModelMetricsDto
                {
                    Precision = m.Metrics.Precision,
                    Recall = m.Metrics.Recall,
                    Map50 = m.Metrics.Map50,
                    Map50To95 = m.Metrics.Map50To95,
                },
        })];
}
