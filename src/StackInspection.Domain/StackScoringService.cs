namespace StackInspection.Domain;

/// <summary>Hasil penilaian tumpukan.</summary>
public sealed record StackScore(
    int TotalScore,
    int MaxPossibleScore,
    double PercentageScore,
    IReadOnlyList<Violation> Violations);

/// <summary>Menjalankan semua aturan dan menghitung skor.</summary>
public sealed class StackScoringService
{
    private readonly IReadOnlyList<IStackRule> _rules;

    public StackScoringService(IEnumerable<IStackRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules];
    }

    /// <summary>Urutan aturan standar.</summary>
    public static StackScoringService CreateDefault() =>
        new([new UnknownSkuRule(), new LabelNotVisibleRule(), new MaxStackRule(), new ClassPositionRule()]);

    public StackScore Score(IReadOnlyList<DetectedCell> cells, IReadOnlyDictionary<string, MasterSku> master)
    {
        ArgumentNullException.ThrowIfNull(cells);
        StackEvaluationContext context = new(cells, master);
        List<Violation> violations = [.. _rules
            .SelectMany(rule => rule.Evaluate(context))
            .OrderBy(v => v.Cell.Row)
            .ThenBy(v => v.Cell.Column)
            .ThenBy(v => v.Type)];

        // Satu sel dengan beberapa pelanggaran tetap dikurangi satu kali.
        int violatingCells = violations.Select(v => (v.Cell.Column, v.Cell.Level)).Distinct().Count();
        int max = cells.Count;
        int total = max - violatingCells;
        double percentage = max == 0 ? 0 : Math.Round(total * 100.0 / max, 2, MidpointRounding.AwayFromZero);
        return new StackScore(total, max, percentage, violations);
    }
}
