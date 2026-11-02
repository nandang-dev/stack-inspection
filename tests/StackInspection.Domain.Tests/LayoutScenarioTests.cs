using Xunit.Abstractions;

namespace StackInspection.Domain.Tests;

/// <summary>
/// Ketahanan <see cref="GridBuilder"/> terhadap layout sintetis yang dikalibrasi dari golden-v3
/// (<see cref="LayoutScenarios"/>). Setiap skenario 200 layout dengan seed tetap.
/// </summary>
public class LayoutScenarioTests(ITestOutputHelper output)
{
    private const int Runs = 200;

    private static ScenarioScore Measure(ScenarioOptions options)
    {
        ScenarioScore[] scores = [.. Enumerable.Range(0, Runs).Select(seed => LayoutScenarios.Evaluate(LayoutScenarios.Generate(options, seed)))];
        int cartons = scores.Sum(s => s.Cartons);
        double Weighted(Func<ScenarioScore, double> metric) => scores.Sum(s => metric(s) * s.Cartons) / cartons;
        return new ScenarioScore(Weighted(s => s.SupportAccuracy), Weighted(s => s.LevelAccuracy), Weighted(s => s.GridOrderAccuracy), cartons);
    }

    public static TheoryData<string> GoldenRangeScenarios() => [.. LayoutScenarios.All.Select(o => o.Name)];

    /// <summary>
    /// Semua skenario dalam rentang golden-v3 saat ini 100 %; batas 99 % memberi ruang perubahan kecil tanpa
    /// membiarkan regresi nyata lolos.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenRangeScenarios))]
    public void GoldenRangeScenario_IsReadAlmostPerfectly(string name)
    {
        ScenarioScore score = Measure(LayoutScenarios.All.Single(o => o.Name == name));

        Assert.True(score.SupportAccuracy >= 0.99, $"tumpuan {score.SupportAccuracy:P1}");
        Assert.True(score.LevelAccuracy >= 0.99, $"level {score.LevelAccuracy:P1}");
        Assert.True(score.GridOrderAccuracy >= 0.99, $"urutan grid {score.GridOrderAccuracy:P1}");
    }

    /// <summary>
    /// Laporan termasuk skenario stres di luar rentang golden-v3. Batas yang diketahui: kardus yang menutup &lt; 50 %
    /// lebar tumpuannya (<see cref="StackBuilder.MinOverlapRatio"/>) dianggap tidak menumpu; ini baru terjadi pada
    /// gabungan geser 30 % + kardus setengah lebar + perspektif + kardus hilang ("campuran-berat").
    /// </summary>
    [Fact]
    public void Report()
    {
        output.WriteLine("skenario            | tumpuan | level  | urutan grid | dus");
        foreach (ScenarioOptions options in LayoutScenarios.All.Concat(LayoutScenarios.Stress))
        {
            ScenarioScore s = Measure(options);
            output.WriteLine($"{options.Name,-19} | {s.SupportAccuracy,6:P1} | {s.LevelAccuracy,6:P1} | {s.GridOrderAccuracy,10:P1} | {s.Cartons}");
        }
    }
}
