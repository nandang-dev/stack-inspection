using StackInspection.Application.CollectSku;
using StackInspection.Application.Contracts;
using StackInspection.Domain;

namespace StackInspection.Application.Tests;

public sealed class SkuInferenceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static SkuReading Read(string sku) => new(sku, sku, 0.99, ReadStatus.Matched);

    private static SkuReading Unknown(string? text = null) => new(SkuCodes.Unknown, text, 0.8, ReadStatus.Unknown);

    private static SkuReading NotVisible() => new(SkuCodes.LabelNotVisible, null, 0, ReadStatus.LabelNotVisible);

    /// <summary>Vektor satuan 2 dimensi dengan sudut tertentu: cosine dua vektor = cos(selisih sudut).</summary>
    private static float[] Angle(double degrees) =>
        [(float)Math.Cos(degrees * Math.PI / 180), (float)Math.Sin(degrees * Math.PI / 180)];

    [Theory]
    [InlineData("4972453", "64972453", true)]   // potongan kanan
    [InlineData("1972453", "64972453", true)]   // 7 digit, digit pertama salah baca
    [InlineData("1971453", "64972453", false)]  // 2 digit berbeda
    [InlineData("85578", "65085578", true)]     // 5 digit persis
    [InlineData("85579", "65085578", false)]    // < 7 digit harus persis
    [InlineData("65578", "65085578", false)]    // tidak berurutan
    [InlineData("64972453", "64972453", false)] // bukan potongan (sudah utuh)
    public void IsPartOf_ToleratesOneDigitOnlyFromSevenDigits(string digits, string sku, bool expected) =>
        Assert.Equal(expected, PartialReadMatch.IsPartOf(digits, sku));

    [Fact]
    public void PartialRead_CompletesFromSkuReadInSamePhoto()
    {
        SkuReading[] readings = [Read("64972453"), Unknown("1972453"), NotVisible()];
        IReadOnlyList<string>[] partial = [["64972453"], ["1972453"], ["85578"]];

        IReadOnlyList<SkuReading> result = PartialReadMatch.Apply(readings, partial, [], minDigits: 5);

        Assert.Equal(("64972453", ReadStatus.Corrected), (result[1].Sku, result[1].Status));
        Assert.Equal(ReadStatus.LabelNotVisible, result[2].Status); // 85578 bukan bagian SKU acuan mana pun
    }

    [Fact]
    public void PartialRead_UsesCandidates_AndSkipsAmbiguousPiece()
    {
        SkuReading[] readings = [NotVisible(), NotVisible()];
        IReadOnlyList<string>[] partial = [["85578"], ["6497"]];

        IReadOnlyList<SkuReading> result = PartialReadMatch.Apply(readings, partial, ["65085578", "64972453", "64970111"], minDigits: 4);

        Assert.Equal(("65085578", ReadStatus.Corrected, "85578"), (result[0].Sku, result[0].Status, result[0].OcrText));
        Assert.Equal(ReadStatus.LabelNotVisible, result[1].Status); // "6497" ada di 2 SKU → tidak dipilih
    }

    [Fact]
    public void Appearance_NeedsTwoSimilarReferencesWithSameSku()
    {
        SkuReading[] readings = [Read("64972453"), Read("64972453"), Read("13143147"), NotVisible(), NotVisible()];
        float[][] vectors = [Angle(0), Angle(5), Angle(60), Angle(3), Angle(62)];

        IReadOnlyList<SkuReading> result = AppearanceInference.Apply(readings, vectors, 0.9, 2, 0.05);

        Assert.Equal(("64972453", ReadStatus.InferredByAppearance), (result[3].Sku, result[3].Status));
        Assert.Equal(ReadStatus.LabelNotVisible, result[4].Status); // hanya 1 acuan 13143147 yang mirip
    }

    [Fact]
    public void Appearance_SkipsWhenAnotherSkuIsAlmostAsSimilar()
    {
        SkuReading[] readings = [Read("64972453"), Read("64972453"), Read("65734536"), Unknown()];
        float[][] vectors = [Angle(0), Angle(2), Angle(6), Angle(3)];

        IReadOnlyList<SkuReading> result = AppearanceInference.Apply(readings, vectors, 0.9, 2, 0.05);

        Assert.Equal(ReadStatus.Unknown, result[3].Status);
    }

    [Fact]
    public async Task Handler_InfersHiddenLabel_AndReportsWarnings()
    {
        List<CartonBox> cartons = [.. Enumerable.Range(0, 4).Select(c => Build.Carton(300 + (c * 410), 600))];
        Dictionary<BoundingBox, (string?, string?)> texts = new()
        {
            [cartons[0].Box] = ("64972453", null),
            [cartons[1].Box] = ("64972453", null),
            [cartons[2].Box] = ("1972453", null), // label terpotong
            [cartons[3].Box] = (null, null),      // label tertutup plastik
        };
        StubEmbedder embedder = new(_ => Angle(0));
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts), embedder: embedder);

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Equal(
            [ReadStatus.Matched, ReadStatus.Matched, ReadStatus.Corrected, ReadStatus.InferredByAppearance],
            result.Cells.OrderBy(c => c.BoundingBox.X1).Select(c => c.ReadStatus));
        Assert.All(result.Cells, c => Assert.Equal("64972453", c.Sku));
        Assert.Contains(CollectWarnings.SkuCorrectedByPartialRead, result.Warnings);
        Assert.Contains(CollectWarnings.SkuInferredByAppearance, result.Warnings);
        Assert.Equal(1, embedder.Calls);
    }

    [Fact]
    public async Task Handler_DoesNotCallEmbedder_WhenEveryLabelIsRead()
    {
        List<CartonBox> cartons = [Build.Carton(300, 600), Build.Carton(710, 600)];
        Dictionary<BoundingBox, (string?, string?)> texts = cartons.ToDictionary(c => c.Box, _ => ((string?)"64972453", (string?)null));
        StubEmbedder embedder = new(_ => Angle(0));
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts), embedder: embedder);

        await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Equal(0, embedder.Calls);
    }
}
