using Microsoft.Extensions.Options;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Application.Exceptions;
using StackInspection.Infrastructure.Vision;

namespace StackInspection.Infrastructure.Tests;

public sealed class FileSystemModelCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ai-models-{Guid.NewGuid():N}");

    public FileSystemModelCatalogTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void AddModel(string name, string? card)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, $"{name}.onnx"), [1, 2, 3]);
        if (card is not null)
        {
            File.WriteAllText(Path.Combine(folder, "model-card.json"), card);
        }
    }

    private FileSystemModelCatalog Catalog(string defaultModel = "carton-v1") =>
        new(Options.Create(new VisionOptions { ModelsDirectory = _root, DefaultModel = defaultModel, InputSize = 640 }));

    [Fact]
    public void ReadsCarttonTrainerModelCard()
    {
        AddModel("carton-v1", """
            {"name":"carton-v1","baseModel":"yolox-tiny","datasetVersion":"ds-v1","inputSize":960,
             "recommendedThresholds":{"confidence":0.45,"iou":0.55},
             "metrics":{"precision":0.899,"recall":0.787,"map50":0.814,"map50_95":0.603},
             "status":"candidate","trainedAt":"2026-09-26T19:26:17Z","onnx":{"file":"carton-v1.onnx"}}
            """);

        DetectionModel model = Assert.Single(Catalog().List());

        Assert.Equal(("carton-v1", 960, 0.45, 0.55, "candidate"), (model.Name, model.InputSize, model.ConfidenceThreshold, model.IouThreshold, model.Status));
        Assert.Equal(0.603, model.Metrics?.Map50To95);
        Assert.Equal(("yolox-tiny", "ds-v1"), (model.BaseModel, model.DatasetVersion));
        Assert.EndsWith("carton-v1.onnx", model.OnnxPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelWithoutCard_UsesDefaults_AndFolderWithoutOnnxIsIgnored()
    {
        AddModel("carton-v2", card: null);
        Directory.CreateDirectory(Path.Combine(_root, "kosong"));

        DetectionModel model = Assert.Single(Catalog().List());

        Assert.Equal(("carton-v2", 640, 0.5, null), (model.Name, model.InputSize, model.ConfidenceThreshold, model.Status));
    }

    [Fact]
    public void Resolve_DefaultByNameAndUnknown()
    {
        AddModel("carton-v1", card: null);
        AddModel("carton-v2", card: null);
        FileSystemModelCatalog catalog = Catalog("carton-v2");

        Assert.Equal("carton-v2", catalog.Resolve(null).Name);
        Assert.Equal("carton-v1", catalog.Resolve("carton-v1").Name);
        RequestValidationException error = Assert.Throws<RequestValidationException>(() => catalog.Resolve("carton-v9"));
        Assert.Contains("carton-v1, carton-v2", error.Errors["model"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDirectory_IsEmptyList()
    {
        FileSystemModelCatalog catalog = new(Options.Create(new VisionOptions { ModelsDirectory = Path.Combine(_root, "tidak-ada") }));

        Assert.Empty(catalog.List());
    }
}
