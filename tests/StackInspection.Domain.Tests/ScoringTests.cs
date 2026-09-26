using StackInspection.Domain;

namespace StackInspection.Domain.Tests;

public class ScoringTests
{
    private static readonly StackScoringService Scoring = StackScoringService.CreateDefault();

    [Fact]
    public void ReferenceGrid_ProducesExpectedScore()
    {
        StackScore score = Scoring.Score(ReferenceGrid.Cells(), ReferenceGrid.Master);

        Assert.Equal(20, score.TotalScore);
        Assert.Equal(48, score.MaxPossibleScore);
        Assert.Equal(41.67, score.PercentageScore);
    }

    [Fact]
    public void ReferenceGrid_MaxStackViolationOnlyTopOfColumnZero()
    {
        Violation[] violations = [.. Scoring.Score(ReferenceGrid.Cells(), ReferenceGrid.Master).Violations
            .Where(v => v.Type == ViolationType.MaxStackExceeded)];

        Violation single = Assert.Single(violations);
        Assert.Equal((0, 0, 7), (single.Cell.Row, single.Cell.Column, single.Cell.Level));
        Assert.Equal("SKU 68140913 melebihi max_stack (7) pada kolom 0", single.Message);
    }

    [Fact]
    public void ReferenceGrid_ClassPositionViolationsPerColumn()
    {
        Violation[] violations = [.. Scoring.Score(ReferenceGrid.Cells(), ReferenceGrid.Master).Violations
            .Where(v => v.Type == ViolationType.ClassPositionInvalid)];

        Assert.Equal(27, violations.Length);
        Dictionary<int, int> perColumn = violations.GroupBy(v => v.Cell.Column).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<int, int> { [1] = 4, [2] = 6, [3] = 7, [4] = 5, [5] = 5 }, perColumn);
        Assert.All(violations.Where(v => v.Cell.Column is 1 or 2), v => Assert.Equal(ReferenceGrid.C, v.Cell.Sku));
        Assert.All(violations.Where(v => v.Cell.Column >= 3), v => Assert.Equal(ReferenceGrid.B, v.Cell.Sku));
    }

    [Fact]
    public void ClassViolationMessage_NamesNearestLowerClassBelow()
    {
        Violation violation = Scoring.Score(ReferenceGrid.Cells(), ReferenceGrid.Master).Violations
            .First(v => v.Type == ViolationType.ClassPositionInvalid && v.Cell.Column == 3);

        Assert.Equal("Produk 68410975 (B) berada di atas produk 68140913 (A) - kelas tidak sesuai", violation.Message);
    }

    [Fact]
    public void Violations_AreSortedByRowColumnType()
    {
        Violation[] violations = [.. Scoring.Score(ReferenceGrid.Cells(), ReferenceGrid.Master).Violations];

        Violation[] sorted = [.. violations.OrderBy(v => v.Cell.Row).ThenBy(v => v.Cell.Column).ThenBy(v => v.Type)];
        Assert.Equal(sorted, violations);
    }

    [Fact]
    public void LabelNotVisibleCells_CountAsFailedOnce()
    {
        List<DetectedCell> cells = ReferenceGrid.Cells();
        // kolom 0 level 0–1 (row 7 dan 6) tidak terlihat labelnya
        foreach (int row in new[] { 6, 7 })
        {
            DetectedCell original = ReferenceGrid.At(cells, row, 0);
            cells[cells.IndexOf(original)] = original with
            {
                Sku = SkuCodes.LabelNotVisible,
                OcrText = null,
                ReadStatus = ReadStatus.LabelNotVisible,
            };
        }

        StackScore score = Scoring.Score(cells, ReferenceGrid.Master);

        // kolom 0 kini hanya 6 SariWangi terbaca → maxStack tidak lagi terlampaui; 2 sel gagal karena label
        Assert.Equal(2, score.Violations.Count(v => v.Type == ViolationType.LabelNotVisible));
        Assert.DoesNotContain(score.Violations, v => v.Type == ViolationType.MaxStackExceeded);
        Assert.Equal(48 - 27 - 2, score.TotalScore);
    }

    [Fact]
    public void UnknownSku_IsNotEvaluatedByOtherRules()
    {
        List<DetectedCell> cells = ReferenceGrid.Cells();
        DetectedCell top = ReferenceGrid.At(cells, 0, 3);
        cells[cells.IndexOf(top)] = top with { Sku = SkuCodes.Unknown, ReadStatus = ReadStatus.Unknown };

        Violation[] forCell = [.. Scoring.Score(cells, ReferenceGrid.Master).Violations
            .Where(v => v.Cell.Row == 0 && v.Cell.Column == 3)];

        Violation single = Assert.Single(forCell);
        Assert.Equal(ViolationType.UnknownSku, single.Type);
    }

    [Fact]
    public void SkuNotInMaster_IsUnknown()
    {
        List<DetectedCell> cells = ReferenceGrid.Cells();
        DetectedCell cell = ReferenceGrid.At(cells, 7, 5);
        cells[cells.IndexOf(cell)] = cell with { Sku = "99999999" };

        Assert.Contains(
            Scoring.Score(cells, ReferenceGrid.Master).Violations,
            v => v.Type == ViolationType.UnknownSku && v.Cell.Sku == "99999999");
    }

    [Fact]
    public void ClassRule_SkipsUnreadableCellsWhenLookingBelow()
    {
        // kolom: C (bawah), UNKNOWN, B, A (atas) → tidak ada pelanggaran kelas
        Dictionary<string, MasterSku> master = new(ReferenceGrid.Master);
        DetectedCell Make(int level, string sku, ReadStatus status) =>
            new(3 - level, 0, level, sku, null, 0.9, 0.9, status, new BoundingBox(0, (3 - level) * 10, 10, ((3 - level) * 10) + 9));
        List<DetectedCell> cells =
        [
            Make(0, ReferenceGrid.C, ReadStatus.Matched),
            Make(1, SkuCodes.Unknown, ReadStatus.Unknown),
            Make(2, ReferenceGrid.B, ReadStatus.Matched),
            Make(3, ReferenceGrid.A, ReadStatus.Matched),
        ];

        StackScore score = Scoring.Score(cells, master);

        Assert.DoesNotContain(score.Violations, v => v.Type == ViolationType.ClassPositionInvalid);
        Assert.Equal(3, score.TotalScore);
    }
}
