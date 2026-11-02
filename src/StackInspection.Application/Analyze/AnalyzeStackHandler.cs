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
        HashSet<int> violating = [.. score.Violations.Select(v => v.Cell.Id)];
        // jalur dihitung ulang dari restsOn, bukan dari stacks client
        Dictionary<int, int> indexOf = cells.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        StackPaths paths = SupportGraph.Paths(
            [.. cells.Select(c => (IReadOnlyList<int>)[.. c.RestsOn.Select(id => indexOf[id])])],
            i => cells[i].Box.CenterX,
            GridBuilder.MaxPaths);

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
                        ViolationCount = g.Count(c => violating.Contains(c.Id)),
                    };
                })
                .OrderByDescending(s => s.Count)
                .ThenBy(s => s.Sku, StringComparer.Ordinal)],
            Stacks = [.. paths.BottomToTop.Select((path, p) => new StackSummaryDto
            {
                Path = p,
                Column = cells[path[0]].Column,
                Height = path.Count,
                CartonIdsBottomToTop = [.. path.Select(i => cells[i].Id)],
                ViolationCount = path.Count(i => violating.Contains(cells[i].Id)),
            })],
        };
    }

    /// <summary>
    /// Validasi request (400). Satu <see cref="DetectedCell"/> per id kardus; row/column = sel kiri-atasnya.
    /// Tumpukan dibentuk ulang dari <c>restsOn</c>, bukan dari <c>stacks</c> client.
    /// </summary>
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
        else if (cellDtos.Any(c => c.Id is null || c.RestsOn is null))
        {
            Add(
                "collectResult.cells",
                "collectResult berasal dari Collect SKU versi lama (cell tanpa id/restsOn). Jalankan ulang Collect SKU.");
        }

        if (errors.ContainsKey("collectResult.cells"))
        {
            throw new RequestValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }

        List<DetectedCell> cells = [];
        // satu kardus bisa muncul di beberapa sel grid; semua entri dengan id sama harus berisi data yang sama
        HashSet<(int, int, int)> positions = [];
        Dictionary<int, (CellDto First, int Row, int Column)> cartons = [];
        for (int i = 0; i < cellDtos.Count; i++)
        {
            CellDto c = cellDtos[i];
            int id = c.Id!.Value;
            if (!positions.Add((id, c.Row, c.Column)))
            {
                Add($"collectResult.cells[{i}]", $"Sel id {id} row {c.Row} column {c.Column} duplikat.");
                continue;
            }

            if (!cartons.TryGetValue(id, out (CellDto First, int Row, int Column) carton))
            {
                cartons[id] = (c, c.Row, c.Column);
                continue;
            }

            if (!SameCarton(carton.First, c))
            {
                Add($"collectResult.cells[{i}]", $"Data kardus id {id} tidak sama di semua selnya.");
            }

            cartons[id] = (carton.First, Math.Min(carton.Row, c.Row), Math.Min(carton.Column, c.Column));
        }

        foreach ((int id, (CellDto c, int row, int column)) in cartons.OrderBy(x => x.Key))
        {
            foreach (int below in c.RestsOn!)
            {
                if (!cartons.TryGetValue(below, out (CellDto First, int Row, int Column) support) || support.First.Level >= c.Level)
                {
                    Add("collectResult.cells", $"restsOn kardus id {id} merujuk id {below} yang tidak ada atau tidak berada di bawahnya.");
                }
            }

            BoundingBox box = new(c.BoundingBox.X1, c.BoundingBox.Y1, c.BoundingBox.X2, c.BoundingBox.Y2);
            cells.Add(new DetectedCell(
                row, column, c.Level, c.Sku, c.OcrText, c.OcrConfidence, c.DetectionConfidence, c.ReadStatus, box, id, [.. c.RestsOn!]));
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
        CartonId = violation.Cell.Id,
        Row = violation.Cell.Row,
        Column = violation.Cell.Column,
        Level = violation.Cell.Level,
        Sku = violation.Cell.Sku,
        Message = violation.Message,
    };

    private static bool SameCarton(CellDto a, CellDto b) =>
        a.Level == b.Level
        && string.Equals(a.Sku, b.Sku, StringComparison.Ordinal)
        && a.ReadStatus == b.ReadStatus
        && a.RestsOn!.Order().SequenceEqual(b.RestsOn!.Order());
}
