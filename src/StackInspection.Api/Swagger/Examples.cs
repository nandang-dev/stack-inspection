using Microsoft.AspNetCore.Mvc;
using StackInspection.Api.Contracts;
using StackInspection.Application.Contracts;
using StackInspection.Application.Exceptions;
using StackInspection.Domain;
using Swashbuckle.AspNetCore.Filters;

namespace StackInspection.Api.Swagger;

internal static class ExampleData
{
    public static readonly Guid InspectionId = Guid.Parse("3f6c1c1e-8a0b-4d1f-9b7e-2a4b5c6d7e8f");

    /// <summary>id kardus jalur contoh Collect SKU, bawah → atas.</summary>
    public static readonly int[] CollectPathIds = [42, 35, 28, 21, 14, 7, 0];

    /// <summary>id kardus jalur contoh Analyze, bawah → atas.</summary>
    public static readonly int[] AnalyzePathIds = [42, 36, 30, 24, 18, 12, 6, 0];

    /// <summary>
    /// Response Collect SKU kecil yang lengkap dan konsisten (lolos validasi Preview dari hasil): grid 3 x 2,
    /// dua tumpukan, kardus id 0 lebar menjembatani keduanya.
    /// </summary>
    public static CollectSkuResponse CompleteCollect()
    {
        // (id, row, kolom pertama, jumlah kolom, level, restsOn, sku, status)
        (int Id, int Row, int Column, int Span, int Level, int[] RestsOn, string Sku, ReadStatus Status)[] cartons =
        [
            (0, 0, 0, 2, 2, [1, 2], "68582213", ReadStatus.Matched),
            (1, 1, 0, 1, 1, [3], "68410975", ReadStatus.Matched),
            (2, 1, 1, 1, 1, [4], "68410975", ReadStatus.Corrected),
            (3, 2, 0, 1, 0, [], "68140913", ReadStatus.Matched),
            (4, 2, 1, 1, 0, [], SkuCodes.LabelNotVisible, ReadStatus.LabelNotVisible),
        ];
        Dictionary<int, CellDto> topLeft = [];
        List<CellDto> cells = [];
        foreach ((int id, int row, int column, int span, int level, int[] restsOn, string sku, ReadStatus status) in cartons)
        {
            for (int c = column; c < column + span; c++)
            {
                CellDto cell = new()
                {
                    Id = id, Row = row, Column = c, Level = level, RestsOn = restsOn, Sku = sku,
                    OcrText = status == ReadStatus.LabelNotVisible ? null : status == ReadStatus.Corrected ? "6841097" : sku,
                    OcrConfidence = status == ReadStatus.LabelNotVisible ? 0 : 0.95,
                    DetectionConfidence = 0.93,
                    ReadStatus = status,
                    BoundingBox = new BoundingBoxDto
                    {
                        X1 = 160 + (column * 270), Y1 = 1218 + (row * 186), X2 = 160 + ((column + span) * 270), Y2 = 1404 + (row * 186),
                    },
                };
                cells.Add(cell);
                topLeft.TryAdd(id, cell);
            }
        }

        return new CollectSkuResponse
        {
            InspectionId = InspectionId,
            Model = "carton-v1",
            ImageWidth = 1920,
            ImageHeight = 3413,
            RowCount = 3,
            ColumnCount = 2,
            ExcludedBackLayerCount = 0,
            Cells = cells,
            Stacks =
            [
                new StackDto { Path = 0, Column = 0, Height = 3, Cartons = [topLeft[3], topLeft[1], topLeft[0]] },
                new StackDto { Path = 1, Column = 1, Height = 3, Cartons = [topLeft[4], topLeft[2], topLeft[0]] },
            ],
            DistinctSkus =
            [
                new DistinctSkuDto { Sku = "68410975", Count = 2 },
                new DistinctSkuDto { Sku = "68140913", Count = 1 },
                new DistinctSkuDto { Sku = "68582213", Count = 1 },
                new DistinctSkuDto { Sku = SkuCodes.LabelNotVisible, Count = 1 },
            ],
            Warnings = [CollectWarnings.LabelNotVisiblePresent, CollectWarnings.IrregularGrid],
            ProcessingTimeMs = 0,
            Timings = new CollectSkuTimingsDto(),
        };
    }

    public static CollectSkuResponse Collect() => new()
    {
        InspectionId = InspectionId,
        Model = "carton-v1",
        ImageWidth = 1920,
        ImageHeight = 3413,
        RowCount = 8,
        ColumnCount = 7,
        ExcludedBackLayerCount = 12,
        Cells =
        [
            // kardus id 0 lebar: menempati R0C0 dan R0C1 (dua entri, isi dan id sama)
            new CellDto
            {
                Id = 0, Row = 0, Column = 0, Level = 7, RestsOn = [7], Sku = "68140913", OcrText = "68140913",
                OcrConfidence = 0.98, DetectionConfidence = 0.95, ReadStatus = ReadStatus.Matched,
                BoundingBox = new BoundingBoxDto { X1 = 148, Y1 = 170, X2 = 690, Y2 = 356 },
            },
            new CellDto
            {
                Id = 0, Row = 0, Column = 1, Level = 7, RestsOn = [7], Sku = "68140913", OcrText = "68140913",
                OcrConfidence = 0.98, DetectionConfidence = 0.95, ReadStatus = ReadStatus.Matched,
                BoundingBox = new BoundingBoxDto { X1 = 148, Y1 = 170, X2 = 690, Y2 = 356 },
            },
            new CellDto
            {
                Id = 1, Row = 0, Column = 2, Level = 3, RestsOn = [8, 9], Sku = "68582213", OcrText = "6858Z213",
                OcrConfidence = 0.91, DetectionConfidence = 0.93, ReadStatus = ReadStatus.Corrected,
                BoundingBox = new BoundingBoxDto { X1 = 700, Y1 = 168, X2 = 970, Y2 = 352 },
            },
            new CellDto
            {
                Id = 47, Row = 7, Column = 6, Level = 0, RestsOn = [], Sku = SkuCodes.LabelNotVisible, OcrText = null,
                OcrConfidence = 0, DetectionConfidence = 0.9, ReadStatus = ReadStatus.LabelNotVisible,
                BoundingBox = new BoundingBoxDto { X1 = 1545, Y1 = 1590, X2 = 1815, Y2 = 1776 },
            },
        ],
        Stacks =
        [
            new StackDto
            {
                Path = 0,
                Column = 0,
                Height = 7,
                Cartons =
                [
                    .. CollectPathIds.Select((id, level) => new CellDto
                    {
                        Id = id,
                        Row = CollectPathIds.Length - 1 - level,
                        Column = 0,
                        Level = level,
                        RestsOn = level == 0 ? [] : [CollectPathIds[level - 1]],
                        Sku = "68140913",
                        OcrText = "68140913",
                        OcrConfidence = 0.98,
                        DetectionConfidence = 0.94,
                        ReadStatus = ReadStatus.Matched,
                        BoundingBox = new BoundingBoxDto
                        {
                            X1 = 160, Y1 = 1776 - ((level + 1) * 186), X2 = 430, Y2 = 1776 - (level * 186),
                        },
                    }),
                ],
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
        Timings = new CollectSkuTimingsDto
        {
            DecodeMs = 180,
            InspectMs = 45,
            DetectMs = 610,
            LabelLocateMs = 240,
            OcrMs = 1950,
            SkuResolveMs = 92,
            BuildMs = 3,
        },
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
            Detail = "Resolusi foto 1600x1200 (1.92 MP) di bawah minimum (sisi terpanjang 2048 px dan 2 MP).",
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

/// <summary>Contoh body Preview dari hasil: response Collect SKU lengkap dan konsisten (atau output LLM berskema sama).</summary>
internal sealed class PreviewFromResultBodyExample : IExamplesProvider<CollectSkuResponse>
{
    public CollectSkuResponse GetExamples() => ExampleData.CompleteCollect();
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
                Type = ViolationCodes.MaxStackExceeded, CartonId = 0, Row = 0, Column = 0, Level = 7, Sku = "68140913",
                Message = "SKU 68140913 melebihi max_stack (7) pada kolom 0",
            },
            new ViolationDto
            {
                Type = ViolationCodes.ClassPositionInvalid, CartonId = 1, Row = 0, Column = 2, Level = 3, Sku = "68582213",
                Message = "Produk 68582213 (C) berada di atas produk 68140913 (A) - kelas tidak sesuai",
            },
        ],
        SkuSummary =
        [
            new SkuSummaryDto { Sku = "68140913", Name = "SariWangi Teh Asli 48 x TB30 x 1.85g", Class = "A", Count = 21, ViolationCount = 1 },
        ],
        Stacks =
        [
            new StackSummaryDto
            {
                Path = 0,
                Column = 0,
                Height = 8,
                Cartons =
                [
                    .. ExampleData.AnalyzePathIds.Select((id, level) => new StackSummaryCartonDto
                    {
                        Id = id,
                        Row = ExampleData.AnalyzePathIds.Length - 1 - level,
                        Column = 0,
                        Level = level,
                        RestsOn = level == 0 ? [] : [ExampleData.AnalyzePathIds[level - 1]],
                        Sku = "68140913",
                        OcrText = "68140913",
                        OcrConfidence = 0.98,
                        DetectionConfidence = 0.94,
                        ReadStatus = ReadStatus.Matched,
                        BoundingBox = new BoundingBoxDto
                        {
                            X1 = 160, Y1 = 1776 - ((level + 1) * 186), X2 = 430, Y2 = 1776 - (level * 186),
                        },
                        Violations = level == 7 ? [ViolationCodes.MaxStackExceeded] : [],
                    }),
                ],
                ViolationCount = 1,
            },
        ],
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
