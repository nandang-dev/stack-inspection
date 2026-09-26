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
            Stacks = [new StackDto { Column = 0, Height = 1, SkusBottomToTop = ["68582213"] }],
        };

        AnalyzeStackResponse result = Handler.Handle(new AnalyzeStackRequest { CollectResult = tampered, MasterSkus = Master });

        Assert.Equal(20, result.TotalScore);
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
