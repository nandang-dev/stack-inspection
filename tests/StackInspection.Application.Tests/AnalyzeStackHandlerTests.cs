using StackInspection.Application.Analyze;
using StackInspection.Application.Contracts;
using StackInspection.Application.Exceptions;
using StackInspection.Domain;

namespace StackInspection.Application.Tests;

public class AnalyzeStackHandlerTests
{
    private static readonly AnalyzeStackHandler Handler = new(StackScoringService.CreateDefault());

    private static readonly MasterSkuDto[] Master =
    [
        new() { Sku = "68410975", Name = "Royco", MaxStack = 8, Weight = 5.2, Class = "B" },
        new() { Sku = "68582213", Name = "Bango", MaxStack = 12, Weight = 7.8, Class = "C" },
        new() { Sku = "68140913", Name = "SariWangi", MaxStack = 7, Weight = 3.1, Class = "A" },
    ];

    private static CollectSkuResponse ReferenceCollect()
    {
        string[] rows =
        [
            "A C C B B B", "A C C B B B", "A C C B B B", "A C C B B B",
            "A A C B B B", "A A C B A A", "A A A B A A", "A A A A A A",
        ];
        Dictionary<string, string> sku = new() { ["A"] = "68140913", ["B"] = "68410975", ["C"] = "68582213" };
        List<CellDto> cells = [];
        for (int r = 0; r < rows.Length; r++)
        {
            string[] codes = rows[r].Split(' ');
            for (int c = 0; c < codes.Length; c++)
            {
                cells.Add(new CellDto
                {
                    Id = (r * 6) + c, RestsOn = r == rows.Length - 1 ? [] : [((r + 1) * 6) + c],
                    Row = r, Column = c, Level = 7 - r, Sku = sku[codes[c]], ReadStatus = ReadStatus.Matched,
                    BoundingBox = new BoundingBoxDto { X1 = c * 100, Y1 = r * 80, X2 = (c * 100) + 95, Y2 = (r * 80) + 75 },
                });
            }
        }

        return new CollectSkuResponse { InspectionId = Guid.NewGuid(), Cells = cells };
    }

    [Fact]
    public void ReferenceGrid_ScoreAndSummaries()
    {
        CollectSkuResponse collect = ReferenceCollect();

        AnalyzeStackResponse result = Handler.Handle(new AnalyzeStackRequest { CollectResult = collect, MasterSkus = Master });

        Assert.Equal((20, 48, 41.67), (result.TotalScore, result.MaxPossibleScore, result.PercentageScore));
        Assert.Equal(collect.InspectionId, result.InspectionId);
        Assert.Equal(1, result.Violations.Count(v => v.Type == ViolationCodes.MaxStackExceeded));
        Assert.Equal(27, result.Violations.Count(v => v.Type == ViolationCodes.ClassPositionInvalid));
        SkuSummaryDto sariwangi = result.SkuSummary.Single(s => s.Sku == "68140913");
        Assert.Equal(("SariWangi", "A", 21, 1), (sariwangi.Name, sariwangi.Class, sariwangi.Count, sariwangi.ViolationCount));
        Assert.Equal([1, 4, 6, 7, 5, 5], result.Stacks.Select(s => s.ViolationCount));
    }

    [Fact]
    public void ClientStacks_AreIgnored()
    {
        CollectSkuResponse collect = ReferenceCollect();
        CollectSkuResponse tampered = new()
        {
            InspectionId = collect.InspectionId,
            Cells = collect.Cells,
            Stacks = [new StackDto { Column = 0, Height = 1, Cartons = [new CellDto { Id = 0, Sku = "68582213" }] }],
        };

        AnalyzeStackResponse result = Handler.Handle(new AnalyzeStackRequest { CollectResult = tampered, MasterSkus = Master });

        Assert.Equal(20, result.TotalScore);
    }

    [Fact]
    public void ReferenceGrid_ViolationsPointToCartonIdAndStacksArePaths()
    {
        CollectSkuResponse collect = ReferenceCollect();
        CellDto collectTop(int? id) => collect.Cells.Where(c => c.Id == id).OrderBy(c => c.Row).ThenBy(c => c.Column).First();
        AnalyzeStackResponse result = Handler.Handle(new AnalyzeStackRequest { CollectResult = collect, MasterSkus = Master });

        ViolationDto maxStack = result.Violations.Single(v => v.Type == ViolationCodes.MaxStackExceeded);
        Assert.Equal((0, 0, 0), (maxStack.CartonId, maxStack.Row, maxStack.Column));
        Assert.Equal(6, result.Stacks.Count);
        Assert.Equal([42, 36, 30, 24, 18, 12, 6, 0], result.Stacks[0].Cartons.Select(c => c.Id));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], result.Stacks[0].Cartons.Select(c => c.Level));
        Assert.Contains(ViolationCodes.MaxStackExceeded, result.Stacks[0].Cartons[^1].Violations);
        CellDto top = collectTop(result.Stacks[0].Cartons[^1].Id);
        StackSummaryCartonDto carton = result.Stacks[0].Cartons[^1];
        Assert.Equal(
            (top.Row, top.Column, top.Sku, top.OcrText, top.OcrConfidence, top.ReadStatus, top.BoundingBox.X1, top.BoundingBox.Y2),
            (carton.Row, carton.Column, carton.Sku, carton.OcrText, carton.OcrConfidence, carton.ReadStatus, carton.BoundingBox.X1, carton.BoundingBox.Y2));
        Assert.All(
            result.Stacks.SelectMany(s => s.Cartons),
            c => Assert.Equal(result.Violations.Where(v => v.CartonId == c.Id).Select(v => v.Type).Distinct().Order(), c.Violations));
        Assert.Equal([0, 1, 2, 3, 4, 5], result.Stacks.Select(s => s.Path));
    }

    [Fact]
    public void CartonInSeveralCells_IsScoredOnce()
    {
        // id 0 = Bango (C) lebar di lantai menempati 2 sel; id 1 = SariWangi (A) di atasnya
        CellDto Cell(int id, int row, int column, int level, string sku, params int[] restsOn) => new()
        {
            Id = id, Row = row, Column = column, Level = level, Sku = sku, RestsOn = restsOn, ReadStatus = ReadStatus.Matched,
            BoundingBox = new BoundingBoxDto { X1 = column * 100, Y1 = row * 100, X2 = (column * 100) + 90, Y2 = (row * 100) + 90 },
        };
        CollectSkuResponse collect = new()
        {
            Cells = [Cell(1, 0, 0, 1, "68140913", 0), Cell(0, 1, 0, 0, "68582213"), Cell(0, 1, 1, 0, "68582213")],
        };

        AnalyzeStackResponse result = Handler.Handle(new AnalyzeStackRequest { CollectResult = collect, MasterSkus = Master });

        Assert.Equal((2, 2), (result.TotalScore, result.MaxPossibleScore));
        Assert.Equal([1, 1], result.SkuSummary.Select(s => s.Count));
        Assert.Equal([0, 1], Assert.Single(result.Stacks).Cartons.Select(c => c.Id));
    }

    [Fact]
    public void HeavyCartonOnLightBridgingCarton_IsViolation()
    {
        CellDto Cell(int id, int row, int column, int level, string sku, params int[] restsOn) => new()
        {
            Id = id, Row = row, Column = column, Level = level, Sku = sku, RestsOn = restsOn, ReadStatus = ReadStatus.Matched,
            BoundingBox = new BoundingBoxDto { X1 = column * 100, Y1 = row * 100, X2 = (column * 100) + 90, Y2 = (row * 100) + 90 },
        };
        CollectSkuResponse collect = new()
        {
            Cells =
            [
                Cell(0, 0, 0, 1, "68140913", 2), Cell(1, 0, 1, 1, "68582213", 2),
                Cell(2, 1, 0, 0, "68140913"), Cell(2, 1, 1, 0, "68140913"),
            ],
        };

        AnalyzeStackResponse result = Handler.Handle(new AnalyzeStackRequest { CollectResult = collect, MasterSkus = Master });

        ViolationDto violation = Assert.Single(result.Violations);
        Assert.Equal((1, ViolationCodes.ClassPositionInvalid), (violation.CartonId, violation.Type));
        Assert.Equal(2, result.Stacks.Count);
    }

    public static TheoryData<string, AnalyzeStackRequest> InvalidRequests()
    {
        CollectSkuResponse collect = ReferenceCollect();
        CellDto first = collect.Cells[0];
        return new TheoryData<string, AnalyzeStackRequest>
        {
            { "masterSkus", new AnalyzeStackRequest { CollectResult = collect, MasterSkus = [] } },
            { "masterSkus[1]", new AnalyzeStackRequest { CollectResult = collect, MasterSkus = [Master[0], Master[0]] } },
            {
                "masterSkus[0]",
                new AnalyzeStackRequest { CollectResult = collect, MasterSkus = [new MasterSkuDto { Sku = "1", Name = "x", MaxStack = 0, Class = "A" }] }
            },
            {
                "masterSkus[0]",
                new AnalyzeStackRequest { CollectResult = collect, MasterSkus = [new MasterSkuDto { Sku = "1", Name = "x", MaxStack = 3, Class = "D" }] }
            },
            { "collectResult.cells", new AnalyzeStackRequest { CollectResult = new CollectSkuResponse(), MasterSkus = Master } },
            {
                // hasil Collect SKU versi lama (tanpa id/restsOn)
                "collectResult.cells",
                new AnalyzeStackRequest
                {
                    CollectResult = new CollectSkuResponse { Cells = [new CellDto { Row = 0, Column = 0, Level = 0, Sku = "68140913" }] },
                    MasterSkus = Master,
                }
            },
            {
                // restsOn merujuk kardus yang tidak ada
                "collectResult.cells",
                new AnalyzeStackRequest
                {
                    CollectResult = new CollectSkuResponse { Cells = [new CellDto { Id = 0, Level = 1, RestsOn = [9], Sku = "68140913" }] },
                    MasterSkus = Master,
                }
            },
            {
                // sel dengan id sama berisi data berbeda
                "collectResult.cells[1]",
                new AnalyzeStackRequest
                {
                    CollectResult = new CollectSkuResponse
                    {
                        Cells =
                        [
                            new CellDto { Id = 0, Column = 0, RestsOn = [], Sku = "68140913" },
                            new CellDto { Id = 0, Column = 1, RestsOn = [], Sku = "68582213" },
                        ],
                    },
                    MasterSkus = Master,
                }
            },
            {
                "collectResult.cells[48]",
                new AnalyzeStackRequest
                {
                    CollectResult = new CollectSkuResponse { Cells = [.. collect.Cells, first] },
                    MasterSkus = Master,
                }
            },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void InvalidRequest_IsRejectedWithFieldError(string field, AnalyzeStackRequest request)
    {
        RequestValidationException error = Assert.Throws<RequestValidationException>(() => Handler.Handle(request));

        Assert.Contains(field, error.Errors.Keys);
    }
}
