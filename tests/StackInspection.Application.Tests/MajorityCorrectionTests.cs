using StackInspection.Application.CollectSku;
using StackInspection.Domain;

namespace StackInspection.Application.Tests;

public class MajorityCorrectionTests
{
    private const string Royco = "65730554";

    private static SkuReading Read(string sku, ReadStatus status = ReadStatus.Matched) => new(sku, sku, 0.9, status);

    private static (List<SkuReading> Readings, List<IReadOnlyList<string>> Variants) Photo(params SkuReading[] extra)
    {
        List<SkuReading> readings = [.. Enumerable.Repeat(Read(Royco), 10), .. extra];
        List<IReadOnlyList<string>> variants = [.. readings.Select(r => (IReadOnlyList<string>)(r.OcrText is null ? [] : [r.OcrText]))];
        return (readings, variants);
    }

    [Theory]
    [InlineData("85730554")] // 6 terbaca 8
    [InlineData("65730584")] // 5 terbaca 8
    public void OneDigitMisread_IsCorrectedToMajority(string misread)
    {
        (List<SkuReading> readings, List<IReadOnlyList<string>> variants) = Photo(Read(misread));

        SkuReading result = MajorityCorrection.Apply(readings, variants, 3, 3, 1)[^1];

        Assert.Equal((Royco, ReadStatus.Corrected, misread), (result.Sku, result.Status, result.OcrText));
    }

    [Fact]
    public void UnknownWithCloseDigits_IsCorrected()
    {
        (List<SkuReading> readings, List<IReadOnlyList<string>> variants) = Photo(new SkuReading(SkuCodes.Unknown, "6573O554", 0.7, ReadStatus.Unknown));
        variants[^1] = ["6573554"]; // 7 digit, satu digit hilang

        SkuReading result = MajorityCorrection.Apply(readings, variants, 3, 3, 1)[^1];

        Assert.Equal((Royco, ReadStatus.Corrected), (result.Sku, result.Status));
    }

    [Fact]
    public void DifferentSkuOrWeakMajority_IsKept()
    {
        // 65232502 jauh berbeda; 65730664 selisih 2 digit (> maxDistance 1)
        (List<SkuReading> readings, List<IReadOnlyList<string>> variants) = Photo(Read("65232502"), Read("65730664"));

        IReadOnlyList<SkuReading> result = MajorityCorrection.Apply(readings, variants, 3, 3, 1);

        Assert.Equal(["65232502", "65730664"], result.Skip(10).Select(r => r.Sku));
    }

    [Fact]
    public void SimilarSkuThatAppearsOftenEnough_IsNotOverwritten()
    {
        // 65730559 muncul 4× vs 10× → tidak dominan 3× lipat → dianggap SKU asli
        (List<SkuReading> readings, List<IReadOnlyList<string>> variants) = Photo([.. Enumerable.Repeat(Read("65730559"), 4)]);

        Assert.All(MajorityCorrection.Apply(readings, variants, 3, 3, 1).Skip(10), r => Assert.Equal("65730559", r.Sku));
    }

    [Fact]
    public void Disabled_ReturnsUnchanged()
    {
        (List<SkuReading> readings, List<IReadOnlyList<string>> variants) = Photo(Read("85730554"));

        Assert.Same(readings, MajorityCorrection.Apply(readings, variants, 0, 3, 1));
    }

    [Fact]
    public void TuningCase01_TwoDigitMisreadsAreCorrected_ThreeDigitsKept()
    {
        // container biru: 30× Sunlight + salah baca, 4× Pepsodent + 1 salah baca, 2 Molto salah baca berbeda
        List<SkuReading> readings =
        [
            .. Enumerable.Repeat(Read("65590059"), 30),
            Read("65590068"), // beda 2 digit
            Read("55590068"), // beda 3 digit
            Read("15690069"), // beda 3 digit
            .. Enumerable.Repeat(Read("64845107"), 4),
            Read("64845163"), // beda 2 digit
            Read("64871158"),
            Read("64371155"),
        ];
        List<IReadOnlyList<string>> variants = [.. readings.Select(r => (IReadOnlyList<string>)[r.OcrText!])];

        IReadOnlyList<SkuReading> result = MajorityCorrection.Apply(readings, variants, 3, 3, 2);

        Assert.Equal(("65590059", ReadStatus.Corrected), (result[30].Sku, result[30].Status));
        Assert.Equal(["55590068", "15690069"], result.Skip(31).Take(2).Select(r => r.Sku));
        Assert.Equal(("64845107", ReadStatus.Corrected), (result[^3].Sku, result[^3].Status));
        Assert.Equal(["64871158", "64371155"], result.TakeLast(2).Select(r => r.Sku)); // tidak ada mayoritas Molto
    }

    [Fact]
    public void TwoDigits_GenuineMinorityVariantIsAlsoCorrected_AcceptedRisk()
    {
        // risiko yang diterima (keputusan 2026-11-03): varian asli 65081663 di antara 3× 65081657 ikut dikoreksi
        List<SkuReading> readings = [.. Enumerable.Repeat(Read("65081657"), 3), Read("65081663")];
        List<IReadOnlyList<string>> variants = [.. readings.Select(r => (IReadOnlyList<string>)[r.OcrText!])];

        Assert.Equal("65081657", MajorityCorrection.Apply(readings, variants, 3, 3, 2)[^1].Sku);
    }

    [Fact]
    public void ClearlyReadMinority_IsKept_WeakReadIsCorrected()
    {
        // 65081663 terbaca jelas (99%) = varian asli → dipertahankan; 65081658 terbaca lemah (80%) → salah baca
        List<SkuReading> readings =
        [
            .. Enumerable.Repeat(Read("65081657"), 3),
            new("65081663", "65081663", 0.99, ReadStatus.Matched),
            new("65081658", "65081658", 0.80, ReadStatus.Matched),
        ];
        List<IReadOnlyList<string>> variants = [.. readings.Select(r => (IReadOnlyList<string>)[r.OcrText!])];

        IReadOnlyList<SkuReading> result = MajorityCorrection.Apply(readings, variants, 3, 3, 2, maxConfidence: 0.97);

        Assert.Equal(("65081663", ReadStatus.Matched), (result[3].Sku, result[3].Status));
        Assert.Equal(("65081657", ReadStatus.Corrected), (result[4].Sku, result[4].Status));
    }

    [Fact]
    public void AllowFilter_LimitsTargets()
    {
        List<SkuReading> readings = [.. Enumerable.Repeat(Read("65590059"), 5), Read("15690069")];
        List<IReadOnlyList<string>> variants = [.. readings.Select(r => (IReadOnlyList<string>)[r.OcrText!])];

        Assert.Equal("15690069", MajorityCorrection.Apply(readings, variants, 3, 3, 3, allow: (_, _) => false)[^1].Sku);
        Assert.Equal("65590059", MajorityCorrection.Apply(readings, variants, 3, 3, 3, allow: (_, _) => true)[^1].Sku);
    }
}
