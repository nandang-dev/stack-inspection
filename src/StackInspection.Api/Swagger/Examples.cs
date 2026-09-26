using Microsoft.AspNetCore.Mvc;
using StackInspection.Application.Contracts;
using StackInspection.Application.Exceptions;
using StackInspection.Domain;
using Swashbuckle.AspNetCore.Filters;

namespace StackInspection.Api.Swagger;

internal static class ExampleData
{
    public static readonly Guid InspectionId = Guid.Parse("3f6c1c1e-8a0b-4d1f-9b7e-2a4b5c6d7e8f");

    public static CollectSkuResponse Collect() => new()
    {
        InspectionId = InspectionId,
        Model = "carton-v1",
        ImageWidth = 1920,
        ImageHeight = 3413,
        RowCount = 8,
        ColumnCount = 6,
        ExcludedBackLayerCount = 12,
        Cells =
        [
            new CellDto
            {
                Row = 0, Column = 0, Level = 7, Sku = "68140913", OcrText = "68140913", OcrConfidence = 0.98,
                DetectionConfidence = 0.95, ReadStatus = ReadStatus.Matched,
                BoundingBox = new BoundingBoxDto { X1 = 148, Y1 = 170, X2 = 418, Y2 = 356 },
            },
            new CellDto
            {
                Row = 0, Column = 1, Level = 3, Sku = "68582213", OcrText = "6858Z213", OcrConfidence = 0.91,
                DetectionConfidence = 0.93, ReadStatus = ReadStatus.Corrected,
                BoundingBox = new BoundingBoxDto { X1 = 422, Y1 = 168, X2 = 696, Y2 = 352 },
            },
            new CellDto
            {
                Row = 7, Column = 5, Level = 0, Sku = SkuCodes.LabelNotVisible, OcrText = null, OcrConfidence = 0,
                DetectionConfidence = 0.9, ReadStatus = ReadStatus.LabelNotVisible,
                BoundingBox = new BoundingBoxDto { X1 = 1545, Y1 = 1590, X2 = 1815, Y2 = 1776 },
            },
        ],
        Stacks =
        [
            new StackDto
            {
                Column = 0,
                Height = 8,
                SkusBottomToTop = ["68140913", "68140913", "68140913", "68140913", "68140913", "68140913", "68140913", "68140913"],
            },
        ],
        DistinctSkus =
        [
            new DistinctSkuDto { Sku = "68140913", Count = 21 },
            new DistinctSkuDto { Sku = "68410975", Count = 17 },
            new DistinctSkuDto { Sku = "68582213", Count = 9 },
            new DistinctSkuDto { Sku = SkuCodes.LabelNotVisible, Count = 1 },
        ],
        Warnings = [CollectWarnings.LabelNotVisiblePresent, CollectWarnings.BackLayerExcluded],
        ProcessingTimeMs = 3120,
    };
}

/// <summary>Contoh response 200 Collect SKU.</summary>
internal sealed class CollectSkuResponseExample : IExamplesProvider<CollectSkuResponse>
{
    public CollectSkuResponse GetExamples() => ExampleData.Collect();
}

/// <summary>Contoh response 422 foto tidak layak.</summary>
internal sealed class PhotoNotInspectableExample : IExamplesProvider<ProblemDetails>
{
    public ProblemDetails GetExamples()
    {
        ProblemDetails problem = new()
        {
            Type = "https://planogram.local/problems/photo-not-inspectable",
            Title = "Foto tidak layak dinilai",
            Status = StatusCodes.Status422UnprocessableEntity,
            Detail = "Resolusi foto 1920x2560 di bawah minimum (sisi terpanjang 3000 px).",
        };
        problem.Extensions["code"] = PhotoNotInspectableException.Code;
        problem.Extensions["reason"] = PhotoRejectReasons.ResolutionTooLow;
        return problem;
    }
}

/// <summary>Contoh request Analyze.</summary>
internal sealed class AnalyzeStackRequestExample : IExamplesProvider<AnalyzeStackRequest>
{
    public AnalyzeStackRequest GetExamples() => new()
    {
        CollectResult = ExampleData.Collect(),
        MasterSkus =
        [
            new MasterSkuDto { Sku = "68410975", Name = "Royco Ayam 576 x 8g", MaxStack = 8, Weight = 5.2, Class = "B" },
            new MasterSkuDto { Sku = "68582213", Name = "Bango Kecap Manis 24 x 210ml", MaxStack = 12, Weight = 7.8, Class = "C" },
            new MasterSkuDto { Sku = "68140913", Name = "SariWangi Teh Asli 48 x TB30 x 1.85g", MaxStack = 7, Weight = 3.1, Class = "A" },
        ],
    };
}

/// <summary>Contoh response 200 Analyze (fixture referensi brief).</summary>
internal sealed class AnalyzeStackResponseExample : IExamplesProvider<AnalyzeStackResponse>
{
    public AnalyzeStackResponse GetExamples() => new()
    {
        InspectionId = ExampleData.InspectionId,
        TotalScore = 20,
        MaxPossibleScore = 48,
        PercentageScore = 41.67,
        Violations =
        [
            new ViolationDto
            {
                Type = ViolationCodes.MaxStackExceeded, Row = 0, Column = 0, Level = 7, Sku = "68140913",
                Message = "SKU 68140913 melebihi max_stack (7) pada kolom 0",
            },
            new ViolationDto
            {
                Type = ViolationCodes.ClassPositionInvalid, Row = 0, Column = 1, Level = 7, Sku = "68582213",
                Message = "Produk 68582213 (C) berada di atas produk 68140913 (A) - kelas tidak sesuai",
            },
        ],
        SkuSummary =
        [
            new SkuSummaryDto { Sku = "68140913", Name = "SariWangi Teh Asli 48 x TB30 x 1.85g", Class = "A", Count = 21, ViolationCount = 1 },
        ],
        Stacks = [new StackSummaryDto { Column = 0, Height = 8, ViolationCount = 1 }],
    };
}

/// <summary>Contoh response 200 daftar model.</summary>
internal sealed class ModelListExample : IExamplesProvider<IReadOnlyList<ModelInfoDto>>
{
    public IReadOnlyList<ModelInfoDto> GetExamples() =>
    [
        new ModelInfoDto
        {
            Name = "carton-v1",
            IsDefault = true,
            InputSize = 960,
            ConfidenceThreshold = 0.5,
            IouThreshold = 0.5,
            Status = "candidate",
            BaseModel = "yolox-tiny",
            DatasetVersion = "ds-v1",
            TrainedAt = new DateTimeOffset(2026, 9, 26, 19, 26, 17, TimeSpan.Zero),
            Metrics = new ModelMetricsDto { Precision = 0.899, Recall = 0.787, Map50 = 0.814, Map50To95 = 0.603 },
        },
    ];
}
