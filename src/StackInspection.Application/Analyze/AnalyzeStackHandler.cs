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
        List<ViolationDto> violations = [.. score.Violations.Select(ToDto)];
        HashSet<int> violating = [.. violations.Select(v => v.CartonId)];
        ILookup<int, string> violationTypes = violations.ToLookup(v => v.CartonId, v => v.Type);
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
            Violations = violations,
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
                Cartons = [.. path.Select(i => ToStackCarton(cells[i], violationTypes[cells[i].Id]))],
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
        void Add(string field, string message) => AddError(errors, field, message);

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

        List<DetectedCell> cells = ValidateCells(request.CollectResult, errors);
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }

        return (cells, master);
    }

    /// <summary>
    /// Validasi hasil Collect SKU dari luar (mis. LLM) untuk Preview tanpa foto (400). Aturan <c>cells</c> sama
    /// dengan Analyze, ditambah hal yang selalu dijamin Collect SKU: ukuran foto dan grid, sel di dalam grid, satu
    /// kardus per sel, kotak di dalam foto, dan <c>level</c> = 1 + level tertinggi di <c>restsOn</c>.
    /// </summary>
    public static void ValidateCollectResult(CollectSkuResponse? collect)
    {
        if (collect is null)
        {
            throw new RequestValidationException("collectResult", "collectResult wajib diisi.");
        }

        Dictionary<string, List<string>> errors = [];
        if (collect.ImageWidth <= 0 || collect.ImageHeight <= 0)
        {
            AddError(errors, "collectResult.imageWidth", "imageWidth dan imageHeight harus lebih dari 0.");
        }

        if (collect.RowCount <= 0 || collect.ColumnCount <= 0)
        {
            AddError(errors, "collectResult.rowCount", "rowCount dan columnCount harus lebih dari 0.");
        }

        List<DetectedCell> cartons = ValidateCells(collect, errors);
        Dictionary<(int Row, int Column), int> owners = [];
        for (int i = 0; i < collect.Cells.Count; i++)
        {
            CellDto c = collect.Cells[i];
            int id = c.Id!.Value;
            string field = $"collectResult.cells[{i}]";
            if (c.Row < 0 || c.Row >= collect.RowCount || c.Column < 0 || c.Column >= collect.ColumnCount)
            {
                AddError(errors, field, $"Sel id {id} row {c.Row} column {c.Column} di luar grid {collect.RowCount} x {collect.ColumnCount}.");
            }

            if (owners.TryGetValue((c.Row, c.Column), out int other) && other != id)
            {
                AddError(errors, field, $"Sel row {c.Row} column {c.Column} berisi dua kardus (id {other} dan {id}).");
            }
            else
            {
                owners[(c.Row, c.Column)] = id;
            }
        }

        Dictionary<int, DetectedCell> byId = cartons.ToDictionary(c => c.Id);
        foreach (DetectedCell carton in cartons)
        {
            BoundingBox box = carton.Box;
            if (box.X1 >= box.X2 || box.Y1 >= box.Y2 || box.X1 < 0 || box.Y1 < 0
                || box.X2 > collect.ImageWidth || box.Y2 > collect.ImageHeight)
            {
                AddError(
                    errors,
                    "collectResult.cells",
                    $"boundingBox kardus id {carton.Id} tidak valid atau di luar foto {collect.ImageWidth} x {collect.ImageHeight}.");
            }

            List<int> supports = [.. carton.RestsOn.Where(byId.ContainsKey)];
            if (supports.Count != carton.RestsOn.Count)
            {
                continue; // restsOn ke id yang tidak ada sudah dilaporkan ValidateCells
            }

            int expected = supports.Count == 0 ? 0 : 1 + supports.Max(id => byId[id].Level);
            if (carton.Level != expected)
            {
                AddError(
                    errors,
                    "collectResult.cells",
                    $"level kardus id {carton.Id} = {carton.Level}, seharusnya {expected} (0 jika restsOn kosong, selain itu 1 + level tertinggi di restsOn).");
            }
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }
    }

    /// <summary>
    /// Validasi <c>cells</c>: satu <see cref="DetectedCell"/> per id kardus, row/column = sel kiri-atasnya. Cells kosong
    /// atau versi lama langsung dilempar (bersama error yang sudah terkumpul); error lain ditambahkan ke <paramref name="errors"/>.
    /// </summary>
    private static List<DetectedCell> ValidateCells(CollectSkuResponse? collect, Dictionary<string, List<string>> errors)
    {
        IReadOnlyList<CellDto> cellDtos = collect?.Cells ?? [];
        if (cellDtos.Count == 0)
        {
            AddError(errors, "collectResult.cells", "cells tidak boleh kosong.");
        }
        else if (cellDtos.Any(c => c.Id is null || c.RestsOn is null))
        {
            AddError(
                errors,
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
                AddError(errors, $"collectResult.cells[{i}]", $"Sel id {id} row {c.Row} column {c.Column} duplikat.");
                continue;
            }

            if (!cartons.TryGetValue(id, out (CellDto First, int Row, int Column) carton))
            {
                cartons[id] = (c, c.Row, c.Column);
                continue;
            }

            if (!SameCarton(carton.First, c))
            {
                AddError(errors, $"collectResult.cells[{i}]", $"Data kardus id {id} tidak sama di semua selnya.");
            }

            cartons[id] = (carton.First, Math.Min(carton.Row, c.Row), Math.Min(carton.Column, c.Column));
        }

        foreach ((int id, (CellDto c, int row, int column)) in cartons.OrderBy(x => x.Key))
        {
            foreach (int below in c.RestsOn!)
            {
                if (!cartons.TryGetValue(below, out (CellDto First, int Row, int Column) support) || support.First.Level >= c.Level)
                {
                    AddError(errors, "collectResult.cells", $"restsOn kardus id {id} merujuk id {below} yang tidak ada atau tidak berada di bawahnya.");
                }
            }

            BoundingBox box = new(c.BoundingBox.X1, c.BoundingBox.Y1, c.BoundingBox.X2, c.BoundingBox.Y2);
            cells.Add(new DetectedCell(
                row, column, c.Level, c.Sku, c.OcrText, c.OcrConfidence, c.DetectionConfidence, c.ReadStatus, box, id, [.. c.RestsOn!]));
        }

        return cells;
    }

    private static void AddError(Dictionary<string, List<string>> errors, string field, string message)
    {
        if (!errors.TryGetValue(field, out List<string>? list))
        {
            list = [];
            errors[field] = list;
        }

        list.Add(message);
    }

    private static StackSummaryCartonDto ToStackCarton(DetectedCell cell, IEnumerable<string> violations) => new()
    {
        Id = cell.Id,
        Row = cell.Row,
        Column = cell.Column,
        Level = cell.Level,
        RestsOn = cell.RestsOn,
        Sku = cell.Sku,
        OcrText = cell.OcrText,
        OcrConfidence = cell.OcrConfidence,
        DetectionConfidence = cell.DetectionConfidence,
        ReadStatus = cell.ReadStatus,
        BoundingBox = new BoundingBoxDto
        {
            X1 = (int)Math.Round(cell.Box.X1),
            Y1 = (int)Math.Round(cell.Box.Y1),
            X2 = (int)Math.Round(cell.Box.X2),
            Y2 = (int)Math.Round(cell.Box.Y2),
        },
        Violations = [.. violations.Distinct().Order(StringComparer.Ordinal)],
    };

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
