using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Application.Exceptions;

namespace StackInspection.Infrastructure.Vision;

/// <summary>
/// Membaca model dari <c>Vision:ModelsDirectory</c>: setiap subfolder berisi satu file <c>.onnx</c> dan
/// (opsional) <c>model-card.json</c> hasil export Carton Trainer. Tanpa model card, input size dan
/// threshold memakai default <see cref="VisionOptions"/>. Folder dipindai sekali saat start.
/// </summary>
public sealed class FileSystemModelCatalog : IModelCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Lazy<IReadOnlyList<DetectionModel>> _models;

    public FileSystemModelCatalog(IOptions<VisionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        VisionOptions vision = options.Value;
        DefaultModelName = vision.DefaultModel;
        string directory = OnnxCartonDetector.ResolvePath(vision.ModelsDirectory);
        _models = new Lazy<IReadOnlyList<DetectionModel>>(() => Scan(directory, vision));
    }

    public string DefaultModelName { get; }

    public IReadOnlyList<DetectionModel> List() => _models.Value;

    public DetectionModel Resolve(string? name)
    {
        string wanted = string.IsNullOrWhiteSpace(name) ? DefaultModelName : name.Trim();
        DetectionModel? model = List().FirstOrDefault(m => string.Equals(m.Name, wanted, StringComparison.Ordinal));
        if (model is not null)
        {
            return model;
        }

        string available = List().Count == 0 ? "(tidak ada)" : string.Join(", ", List().Select(m => m.Name));
        throw new RequestValidationException("model", $"Model '{wanted}' tidak ditemukan. Model tersedia: {available}.");
    }

    public static IReadOnlyList<DetectionModel> Scan(string directory, VisionOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<DetectionModel> models = [];
        foreach (string folder in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(folder);
            ModelCardFile? card = ReadCard(Path.Combine(folder, "model-card.json"));
            string? onnxFile = card?.Onnx?.File is { Length: > 0 } file && File.Exists(Path.Combine(folder, file))
                ? Path.Combine(folder, file)
                : Directory.EnumerateFiles(folder, "*.onnx").Order(StringComparer.Ordinal).FirstOrDefault();
            if (onnxFile is null)
            {
                continue;
            }

            models.Add(new DetectionModel(
                Name: name,
                OnnxPath: onnxFile,
                InputSize: card?.InputSize is > 0 ? card.InputSize.Value : defaults.InputSize,
                ConfidenceThreshold: card?.RecommendedThresholds?.Confidence ?? defaults.ConfidenceThreshold,
                IouThreshold: card?.RecommendedThresholds?.Iou ?? defaults.IouThreshold,
                Status: card?.Status,
                BaseModel: card?.BaseModel,
                DatasetVersion: card?.DatasetVersion,
                TrainedAt: card?.TrainedAt,
                Metrics: card?.Metrics is { } m ? new ModelMetrics(m.Precision, m.Recall, m.Map50, m.Map50To95) : null));
        }

        return models;
    }

    private static ModelCardFile? ReadCard(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ModelCardFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null; // model card rusak: tetap pakai default
        }
    }

    /// <summary>Subset <c>model-card.json</c> Carton Trainer yang dipakai API.</summary>
    private sealed class ModelCardFile
    {
        public int? InputSize { get; init; }

        public ThresholdsFile? RecommendedThresholds { get; init; }

        public MetricsFile? Metrics { get; init; }

        public string? Status { get; init; }

        public string? BaseModel { get; init; }

        public string? DatasetVersion { get; init; }

        public DateTimeOffset? TrainedAt { get; init; }

        public OnnxFile? Onnx { get; init; }
    }

    private sealed class ThresholdsFile
    {
        public double? Confidence { get; init; }

        public double? Iou { get; init; }
    }

    private sealed class MetricsFile
    {
        public double Precision { get; init; }

        public double Recall { get; init; }

        public double Map50 { get; init; }

        [JsonPropertyName("map50_95")]
        public double Map50To95 { get; init; }
    }

    private sealed class OnnxFile
    {
        public string? File { get; init; }
    }
}
