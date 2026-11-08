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
    [InlineData("6471156", "64871156", true)]   // satu digit di tengah terlewat (tuning case 01 #22)
    [InlineData("6471165", "64871156", false)]  // terlewat satu digit dan tertukar: bukan
    public void IsPartOf_ToleratesOneDigitOnlyFromSevenDigits(string digits, string sku, bool expected) =>
        Assert.Equal(expected, PartialReadMatch.IsPartOf(digits, sku));

    [Theory]
    [InlineData("649558", "64955462", 6, true)]  // tuning case 01 #46: label tertutup lakban, digit ke-6 salah
    [InlineData("649558", "64955462", 7, false)] // pelengkapan langsung: < 7 digit harus persis
    [InlineData("649528", "64955462", 6, false)] // 2 digit berbeda
    public void IsPartOf_OneMismatchThresholdIsConfigurable(string digits, string sku, int minDigits, bool expected) =>
        Assert.Equal(expected, PartialReadMatch.IsPartOf(digits, sku, minDigits));

    [Theory]
    [InlineData("655959", "65590059", true)]   // tuning case 01 #76: dua angka 0 terlewat
    [InlineData("65900", "65590059", true)]    // #58
    [InlineData("655959", "64955462", false)]
    [InlineData("65590059", "65590059", false)] // bukan potongan
    public void IsSubsequenceOf_AllowsSkippedDigitsInOrder(string digits, string sku, bool expected) =>
        Assert.Equal(expected, PartialReadMatch.IsSubsequenceOf(digits, sku));

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
    public void PartialRead_AmbiguousPiece_UsesSkuThatDominatesPhoto()
    {
        // tuning case 01: 65590059 terbaca di banyak kardus, salah baca 65590053 hanya sekali
        SkuReading[] readings = [Read("65590059"), Read("65590059"), Read("65590059"), Read("65590053"), NotVisible(), NotVisible()];
        IReadOnlyList<string>[] partial = [[], [], [], [], ["65590"], ["6497"]];

        IReadOnlyList<SkuReading> result = PartialReadMatch.Apply(readings, partial, ["64972453", "64970111"], minDigits: 4, dominantMinCount: 3, dominance: 3);

        Assert.Equal(("65590059", ReadStatus.Corrected), (result[4].Sku, result[4].Status));
        Assert.Equal(ReadStatus.LabelNotVisible, result[5].Status); // 6497: dua candidate, tidak ada yang dominan
    }

    [Fact]
    public void PartialRead_AmbiguousPiece_WithoutClearMajority_IsLeftUnread()
    {
        SkuReading[] readings = [Read("64955462"), Read("64955462"), Read("64955456"), NotVisible()];
        IReadOnlyList<string>[] partial = [[], [], [], ["64955"]];

        IReadOnlyList<SkuReading> result = PartialReadMatch.Apply(readings, partial, [], minDigits: 5, dominantMinCount: 3, dominance: 3);

        Assert.Equal(ReadStatus.LabelNotVisible, result[3].Status); // 2 lawan 1: belum cukup dominan
    }

    private static SkuReading Read(string sku, double confidence) => new(sku, sku, confidence, ReadStatus.Matched);

    [Fact]
    public void SiblingMerge_SunsilkReadOncePerSku_MergesToMoreConfidentRead()
    {
        // tuning case 01: #16 65632159 (97%) dan #31 65632152 (88%), kardus Sunsilk sangat mirip (cos 10° ≈ 0,98)
        SkuReading[] readings = [Read("65632159", 0.97), Read("65632152", 0.88), Read("64924088"), Read("64924088")];
        float[][] vectors = [Angle(0), Angle(10), Angle(80), Angle(82)];

        IReadOnlyList<SkuReading> result = SiblingSkuMerge.Apply(readings, vectors, [], 2, 0.85, 3, out var merges);

        Assert.Equal(("65632159", ReadStatus.Corrected, "65632152"), (result[1].Sku, result[1].Status, result[1].OcrText));
        Assert.Equal(new SiblingSkuMerge.Merge("65632152", "65632159", 1, 1, merges[0].Similarity), Assert.Single(merges));
        Assert.Equal("64924088", result[2].Sku);
    }

    [Fact]
    public void SiblingMerge_SimilarDigitsButDifferentLook_StaySeparate()
    {
        // Rinso Classic Fresh 65081657 vs Perfume Essence 65081663: beda 2 digit, kemiripan ±0,7 (cos 45°)
        SkuReading[] readings = [Read("65081657"), Read("65081657"), Read("65081657"), Read("65081663")];
        float[][] vectors = [Angle(0), Angle(2), Angle(4), Angle(45)];

        IReadOnlyList<SkuReading> result = SiblingSkuMerge.Apply(readings, vectors, [], 2, 0.85, 3, out var merges);

        Assert.Equal("65081663", result[3].Sku);
        Assert.Empty(merges);
    }

    [Fact]
    public void SiblingMerge_WeakSkuInCandidates_IsKept_AndMinorityMisreadJoinsMajority()
    {
        SkuReading[] readings = [Read("65590059"), Read("65590059"), Read("65590059"), Read("65590053", 0.98), Read("65590058")];
        float[][] vectors = [Angle(0), Angle(3), Angle(6), Angle(4), Angle(5)];

        IReadOnlyList<SkuReading> result = SiblingSkuMerge.Apply(readings, vectors, ["65590058"], 2, 0.85, 3, out _);

        Assert.Equal(("65590059", ReadStatus.Corrected), (result[3].Sku, result[3].Status));
        Assert.Equal(("65590058", ReadStatus.Matched), (result[4].Sku, result[4].Status)); // ada di candidateSkus
    }

    [Fact]
    public void SiblingMerge_BalancedVariantsOfOneProductFamily_StaySeparate()
    {
        // data pre-label: 65730554 ×8 dan 65730557 ×11 di foto yang sama, desain kardus hampir sama
        SkuReading[] readings = [.. Enumerable.Repeat(Read("65730554"), 8), .. Enumerable.Repeat(Read("65730557"), 11), Read("85730554")];
        float[][] vectors = [.. Enumerable.Range(0, readings.Length).Select(i => Angle(i % 3))];

        IReadOnlyList<SkuReading> result = SiblingSkuMerge.Apply(readings, vectors, [], 2, 0.85, 3, out var merges);

        Assert.Equal(9, result.Count(r => r.Sku == "65730554"));  // 8 asli tidak dipindah ke 65730557 (8 vs 11) + 1 salah baca
        Assert.Equal(11, result.Count(r => r.Sku == "65730557"));
        Assert.Equal(("65730554", ReadStatus.Corrected), (result[^1].Sku, result[^1].Status)); // salah baca 6→8, 1 kardus
        Assert.Single(merges);
    }

    private static SkuReading Short(string text, double confidence) => new(SkuCodes.Unknown, text, confidence, ReadStatus.Unknown);

    [Fact]
    public void Reconstruction_EqualSupportAndConfidence_BrokenByNearFullRead_OrSkipped()
    {
        IReadOnlyList<string>[] variants = [["64955472"], ["6495562"], ["6495546"], ["6495545"]];
        float[][] vectors = [Angle(0), Angle(2), Angle(4), Angle(6)];
        // ...62 dan ...65 sama-sama dari 2 bacaan berkeyakinan 0,95; bacaan utuh 64955472 hanya dekat ke ...62
        SkuReading[] near = [Read("64955472", 0.97), Short("6495562", 0.95), Short("6495546", 0.95), Short("6495545", 0.95)];
        Assert.Equal("64955462", Assert.Single(Found(near, variants, vectors)).Sku);

        // tanpa bacaan utuh: benar-benar seri → tidak dipakai
        SkuReading[] tied = [Read("65590059"), Short("6495562", 0.95), Short("6495546", 0.95), Short("6495545", 0.95)];
        Assert.Empty(Found(tied, [["65590059"], ["6495562"], ["6495546"], ["6495545"]], [Angle(80), Angle(2), Angle(4), Angle(6)]));
    }

    private static IReadOnlyList<SkuReconstruction.Reconstruction> Found(
        SkuReading[] readings, IReadOnlyList<string>[] variants, float[][] vectors)
    {
        SkuReconstruction.Apply(readings, variants, vectors, 8, 0.85, out var found);
        return found;
    }

    [Theory]
    [InlineData("6871156", "64871156", true)]   // angka 4 hilang (tuning case 01 #28)
    [InlineData("6487156", "64871156", true)]   // satu angka 1 dari 11 hilang (#36)
    [InlineData("4871156", "64871156", true)]   // digit pertama hilang
    [InlineData("6487115", "64871156", true)]   // digit terakhir hilang
    [InlineData("5457156", "64871156", false)]  // salah angka, bukan sekadar hilang
    [InlineData("64871156", "64871156", false)] // panjang sama
    public void Reconstruction_IsOneDeletion(string read, string sku, bool expected) =>
        Assert.Equal(expected, SkuReconstruction.IsOneDeletion(read, sku));

    [Fact]
    public void Reconstruction_MoltoTwoReadsMissingDifferentDigits_GiveOneSku()
    {
        SkuReading[] readings = [Unknown("6871156"), Unknown("6487156"), Unknown("5457156"), Read("65590059")];
        IReadOnlyList<string>[] variants = [["6871156"], ["6487156"], ["5457156"], ["65590059"]];
        float[][] vectors = [Angle(0), Angle(5), Angle(8), Angle(60)];

        IReadOnlyList<SkuReading> result = SkuReconstruction.Apply(readings, variants, vectors, 8, 0.85, out var found);

        Assert.Equal(("64871156", ReadStatus.Corrected, "6871156"), (result[0].Sku, result[0].Status, result[0].OcrText));
        Assert.Equal(("64871156", ReadStatus.Corrected), (result[1].Sku, result[1].Status));
        Assert.Equal(ReadStatus.Unknown, result[2].Status); // 5457156 salah angka, bukan pendukung
        Assert.Equal(["6871156", "6487156"], Assert.Single(found).Reads);
    }

    [Fact]
    public void Reconstruction_EqualSupport_PicksCandidateWithMostConfidentReads()
    {
        // Pepsodent: 64955462 (#42 0,99 + #51 0,98), 64955465 (#43 0,97 + #51), 64955460 (#45 0,88 + #51);
        // tanpa bacaan utuh sama sekali, keyakinan OCR yang menentukan
        SkuReading[] readings = [Read("65590059"), Short("6495562", 0.99), Short("6495546", 0.98), Short("6495545", 0.97), Short("6495540", 0.88)];
        IReadOnlyList<string>[] variants = [["65590059"], ["6495562"], ["6495546"], ["6495545"], ["6495540"]];
        float[][] vectors = [Angle(80), Angle(2), Angle(4), Angle(6), Angle(8)];

        IReadOnlyList<SkuReading> result = SkuReconstruction.Apply(readings, variants, vectors, 8, 0.85, out var found);

        Assert.Equal("64955462", Assert.Single(found).Sku);
        Assert.Equal(("64955462", "64955462"), (result[1].Sku, result[2].Sku));
        Assert.Equal(ReadStatus.Unknown, result[3].Status); // sisanya diserahkan ke tahap berikutnya
    }

    [Fact]
    public void Reconstruction_SingleReadOrDissimilarCartons_DoNothing()
    {
        SkuReading[] single = [Unknown("6563215"), Read("65590059")];
        Assert.Same(single, SkuReconstruction.Apply(single, [["6563215"], ["65590059"]], [Angle(0), Angle(1)], 8, 0.85, out _));

        SkuReading[] dissimilar = [Unknown("6871156"), Unknown("6487156")];
        IReadOnlyList<SkuReading> result = SkuReconstruction.Apply(dissimilar, [["6871156"], ["6487156"]], [Angle(0), Angle(60)], 8, 0.85, out var found);
        Assert.Empty(found);
        Assert.All(result, r => Assert.Equal(ReadStatus.Unknown, r.Status));
    }

    [Fact]
    public void Appearance_ConsistencyCheck_BlocksSkuThatContradictsOwnDigits()
    {
        // Molto mirip Sunlight secara tampilan, tetapi angkanya 6871156 jelas bukan 65590059
        SkuReading[] readings = [Read("65590059"), Read("65590059"), Unknown("6871156"), NotVisible()];
        float[][] vectors = [Angle(0), Angle(2), Angle(10), Angle(11)];

        IReadOnlyList<SkuReading> result = AppearanceInference.Apply(
            readings, vectors, 0.80, 1, 0.05, (i, sku) => i != 2 || sku != "65590059");

        Assert.Equal(ReadStatus.Unknown, result[2].Status);
        Assert.Equal(("65590059", ReadStatus.InferredByAppearance), (result[3].Sku, result[3].Status));
    }

    [Fact]
    public void Appearance_ConsistencyCheck_PicksMostSimilarSkuThatFitsOwnDigits()
    {
        // tuning case 01 #22: Molto terbaca 5457156 paling mirip tetangga 64924088, tetapi angkanya hanya cocok Molto
        SkuReading[] readings = [Read("64924088"), Read("64871156"), Unknown("5457156")];
        float[][] vectors = [Angle(0), Angle(20), Angle(2)];

        IReadOnlyList<SkuReading> result = AppearanceInference.Apply(
            readings, vectors, 0.90, 1, 0.05, (i, sku) => i != 2 || sku == "64871156");

        Assert.Equal(("64871156", ReadStatus.InferredByAppearance), (result[2].Sku, result[2].Status));
    }

    [Fact]
    public void SiblingMerge_WithoutEmbeddings_DoesNothing()
    {
        SkuReading[] readings = [Read("65632159", 0.97), Read("65632152", 0.88)];

        Assert.Same(readings, SiblingSkuMerge.Apply(readings, [], [], 2, 0.85, 3, out var merges));
        Assert.Empty(merges);
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
    public async Task Handler_ThreeDigitMisreadLookingLikeMajority_IsCorrected_ThenUnreadNeighbourIsInferred()
    {
        // tuning case 01: #59 terbaca 15690069 (beda 3 digit dari Sunlight 65590059) dan meracuni inferensi
        // tampilan kardus di bawahnya (#71) karena dua SKU sama miripnya
        List<CartonBox> cartons = [.. Enumerable.Range(0, 6).Select(c => Build.Carton(300 + (c * 410), 600))];
        Dictionary<BoundingBox, (string?, string?)> texts = new()
        {
            [cartons[0].Box] = ("65590059", null),
            [cartons[1].Box] = ("65590059", null),
            [cartons[2].Box] = ("65590059", null),
            [cartons[3].Box] = ("15690069", null),
            [cartons[4].Box] = (null, null),
            [cartons[5].Box] = ("64845107", null), // produk lain, tampilan berbeda: tidak tersentuh
        };
        StubEmbedder embedder = new(b => b == cartons[5].Box ? Angle(80) : Angle(0));
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts), embedder: embedder);

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        CellDto[] cells = [.. result.Cells.OrderBy(c => c.BoundingBox.X1)];
        Assert.Equal(("65590059", ReadStatus.Corrected, "15690069"), (cells[3].Sku, cells[3].ReadStatus, cells[3].OcrText));
        Assert.Equal(("65590059", ReadStatus.InferredByAppearance), (cells[4].Sku, cells[4].ReadStatus));
        Assert.Equal(1.0, cells[4].OcrConfidence, 3); // kemiripan tampilan
        Assert.Equal(("64845107", ReadStatus.Matched), (cells[5].Sku, cells[5].ReadStatus));
        Assert.Contains(CollectWarnings.SkuCorrectedByMajority, result.Warnings);
    }

    [Fact]
    public async Task Handler_ThreeDigitMisread_NotLookingLikeMajority_IsKept()
    {
        List<CartonBox> cartons = [.. Enumerable.Range(0, 4).Select(c => Build.Carton(300 + (c * 410), 600))];
        Dictionary<BoundingBox, (string?, string?)> texts = new()
        {
            [cartons[0].Box] = ("65590059", null),
            [cartons[1].Box] = ("65590059", null),
            [cartons[2].Box] = ("65590059", null),
            [cartons[3].Box] = ("15690069", null),
        };
        StubEmbedder embedder = new(b => b == cartons[3].Box ? Angle(60) : Angle(0));
        CollectSkuHandler handler = Build.Handler(
            new StubDecoder(3000, 4000), new StubDetector(cartons), new StubReader(texts), embedder: embedder);

        CollectSkuResponse result = await handler.HandleAsync(new CollectSkuCommand(Build.Jpeg, []), None);

        Assert.Equal("15690069", result.Cells.MaxBy(c => c.BoundingBox.X1)!.Sku);
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
