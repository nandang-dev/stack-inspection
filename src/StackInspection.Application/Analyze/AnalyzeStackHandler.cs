using StackInspection.Application.Contracts;
using StackInspection.Application.Exceptions;
using StackInspection.Domain;

namespace StackInspection.Application.Analyze;

/// <summary>Menghitung pelanggaran aturan tumpukan dan skor dari hasil Collect SKU + master SKU.</summary>
public sealed class AnalyzeStackHandler
{
    private readonly StackScoringService _scoring;

    public AnalyzeStackHandler(StackScoringService scoring)
    {
        _scoring = scoring;
    }

    public AnalyzeStackResponse Handle(AnalyzeStackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        (List<DetectedCell> cells, Dictionary<string, MasterSku> master) = Validate(request);

        StackScore score = _scoring.Score(cells, master);
        HashSet<(int Column, int Level)> violating = [.. score.Violations.Select(v => (v.Cell.Column, v.Cell.Level))];

        return new AnalyzeStackResponse
        {
            InspectionId = request.CollectResult!.InspectionId,
            TotalScore = score.TotalScore,
            MaxPossibleScore = score.MaxPossibleScore,
            PercentageScore = score.PercentageScore,
            Violations = [.. score.Violations.Select(ToDto)],
            SkuSummary = [.. cells
                .GroupBy(c => c.Sku, StringComparer.Ordinal)
                .Select(g =>
                {
                    MasterSku? info = master.GetValueOrDefault(g.Key);
                    return new SkuSummaryDto
                    {
                        Sku = g.Key,
                        Name = info?.Name,
                        Class = info?.Class.ToString(),
                        Count = g.Count(),
                        ViolationCount = g.Count(c => violating.Contains((c.Column, c.Level))),
                    };
                })
                .OrderByDescending(s => s.Count)
                .ThenBy(s => s.Sku, StringComparer.Ordinal)],
            Stacks = [.. cells
                .GroupBy(c => c.Column)
                .OrderBy(g => g.Key)
                .Select(g => new StackSummaryDto
                {
                    Column = g.Key,
                    Height = g.Count(),
                    ViolationCount = g.Count(c => violating.Contains((c.Column, c.Level))),
                })],
        };
    }

    /// <summary>Validasi request (400). Stack dibentuk ulang dari row/column/level, bukan dari <c>stacks</c> client.</summary>
    public static (List<DetectedCell> Cells, Dictionary<string, MasterSku> Master) Validate(AnalyzeStackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Dictionary<string, List<string>> errors = [];
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out List<string>? list))
            {
                list = [];
                errors[field] = list;
            }

            list.Add(message);
        }

        Dictionary<string, MasterSku> master = new(StringComparer.Ordinal);
        if (request.MasterSkus.Count == 0)
        {
            Add("masterSkus", "masterSkus tidak boleh kosong.");
        }

        for (int i = 0; i < request.MasterSkus.Count; i++)
        {
            MasterSkuDto dto = request.MasterSkus[i];
            string field = $"masterSkus[{i}]";
            if (string.IsNullOrWhiteSpace(dto.Sku))
            {
                Add(field, "sku wajib diisi.");
                continue;
            }

            if (dto.MaxStack <= 0)
            {
                Add(field, $"maxStack SKU {dto.Sku} harus lebih dari 0.");
            }

            if (!Enum.TryParse(dto.Class, ignoreCase: false, out SkuClass skuClass) || !Enum.IsDefined(skuClass)
                || !string.Equals(dto.Class, skuClass.ToString(), StringComparison.Ordinal))
            {
                Add(field, $"class SKU {dto.Sku} harus A, B, atau C.");
                continue;
            }

            if (!master.TryAdd(dto.Sku, new MasterSku(dto.Sku, dto.Name, dto.MaxStack, dto.Weight, skuClass)))
            {
                Add(field, $"SKU {dto.Sku} duplikat di masterSkus.");
            }
        }

        IReadOnlyList<CellDto> cellDtos = request.CollectResult?.Cells ?? [];
        if (cellDtos.Count == 0)
        {
            Add("collectResult.cells", "cells tidak boleh kosong.");
        }

        HashSet<(int, int)> positions = [];
        List<DetectedCell> cells = [];
        for (int i = 0; i < cellDtos.Count; i++)
        {
            CellDto c = cellDtos[i];
            if (!positions.Add((c.Column, c.Level)))
            {
                Add($"collectResult.cells[{i}]", $"Posisi column {c.Column} level {c.Level} duplikat.");
            }

            BoundingBox box = new(c.BoundingBox.X1, c.BoundingBox.Y1, c.BoundingBox.X2, c.BoundingBox.Y2);
            cells.Add(new DetectedCell(
                c.Row, c.Column, c.Level, c.Sku, c.OcrText, c.OcrConfidence, c.DetectionConfidence, c.ReadStatus, box));
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }

        return (cells, master);
    }

    private static ViolationDto ToDto(Violation violation) => new()
    {
        Type = violation.Type switch
        {
            ViolationType.UnknownSku => ViolationCodes.UnknownSku,
            ViolationType.LabelNotVisible => ViolationCodes.LabelNotVisible,
            ViolationType.MaxStackExceeded => ViolationCodes.MaxStackExceeded,
            _ => ViolationCodes.ClassPositionInvalid,
        },
        Row = violation.Cell.Row,
        Column = violation.Cell.Column,
        Level = violation.Cell.Level,
        Sku = violation.Cell.Sku,
        Message = violation.Message,
    };
}
