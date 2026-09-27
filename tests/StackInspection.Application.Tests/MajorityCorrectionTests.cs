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
}
