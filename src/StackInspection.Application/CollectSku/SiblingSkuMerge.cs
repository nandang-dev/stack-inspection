using StackInspection.Domain;

namespace StackInspection.Application.CollectSku;

/// <summary>
/// Menyatukan "SKU kembar" hasil salah baca: dua SKU yang terbaca di foto yang sama, berselisih sedikit digit, dan
/// kardusnya terlihat sangat mirip dianggap produk yang sama. Kardus ber-SKU yang lebih lemah dipindah ke SKU yang
/// lebih kuat (lebih banyak kardus; jika sama, keyakinan OCR tertinggi lebih besar) dengan status
/// <see cref="ReadStatus.Corrected"/>. Melengkapi koreksi mayoritas, yang butuh minimal beberapa kardus ber-SKU sama:
/// tuning case 01 Sunsilk hanya terbaca 65632159 (#16, 97%) dan 65632152 (#31, 88%), sehingga potongan "6563215" dan
/// inferensi tampilan kardus Sunsilk lain ambigu.
/// </summary>
/// <remarks>
/// SKU berbeda yang memang mirip angkanya tetap terpisah karena tampilannya beda (Rinso 65081657 vs 65081663,
/// kemiripan 0,66–0,75). Varian satu keluarga produk bernomor berurutan dan berdesain hampir sama bisa ada di satu
/// muatan (data pre-label: 65730554 ×8 dan 65730557 ×11, 64873190 ×4 dan 64873191 ×4 di foto yang sama), jadi SKU
/// yang lemah hanya dipindah jika terbaca di satu kardus saja atau SKU yang kuat ≥ <c>dominance</c>× lebih banyak.
/// SKU yang ada di <c>candidateSkus</c> tidak pernah dipindah.
/// </remarks>
public static class SiblingSkuMerge
{
    /// <summary>Satu penyatuan, untuk log.</summary>
    public sealed record Merge(string From, string To, int FromCount, int ToCount, double Similarity);

    /// <param name="readings">Hasil baca per kardus.</param>
    /// <param name="embeddings">Vektor tampilan per kardus (panjang 1); kosong = tidak ada penyatuan.</param>
    /// <param name="candidates">SKU dari request; SKU di daftar ini tidak dipindah.</param>
    /// <param name="maxDistance">Selisih digit (Levenshtein) maksimal antara dua SKU kembar.</param>
    /// <param name="minSimilarity">Kemiripan tampilan (cosine) minimal antara kardus kedua SKU.</param>
    /// <param name="dominance">SKU lemah yang terbaca di lebih dari satu kardus hanya dipindah jika SKU kuat ≥ sekian kali lebih banyak.</param>
    /// <param name="merges">Penyatuan yang dilakukan.</param>
    public static IReadOnlyList<SkuReading> Apply(
        IReadOnlyList<SkuReading> readings,
        IReadOnlyList<float[]> embeddings,
        IReadOnlyCollection<string> candidates,
        int maxDistance,
        double minSimilarity,
        int dominance,
        out IReadOnlyList<Merge> merges)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(candidates);
        List<Merge> done = [];
        merges = done;
        if (embeddings.Count != readings.Count || maxDistance <= 0)
        {
            return readings;
        }

        SkuReading[] result = [.. readings];
        bool changed = true;
        while (changed)
        {
            changed = false;
            Dictionary<string, int[]> groups = Enumerable.Range(0, result.Length)
                .Where(i => result[i].Status is ReadStatus.Matched or ReadStatus.Corrected)
                .GroupBy(i => result[i].Sku, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

            // SKU terlemah lebih dulu: ia yang paling mungkin salah baca
            foreach ((string weak, int[] weakIndexes) in groups.OrderBy(g => Strength(result, g.Value)))
            {
                if (candidates.Contains(weak))
                {
                    continue;
                }

                (string Sku, double Similarity)? target = groups
                    .Where(g => g.Key != weak
                        && SkuMatcher.Levenshtein(g.Key, weak) <= maxDistance
                        && Strength(result, g.Value).CompareTo(Strength(result, weakIndexes)) > 0
                        && (weakIndexes.Length == 1 || g.Value.Length >= dominance * weakIndexes.Length))
                    .Select(g => (Sku: g.Key, Similarity: MaxSimilarity(embeddings, weakIndexes, g.Value)))
                    .Where(t => t.Similarity >= minSimilarity)
                    // SKU dengan angka terdekat lebih dulu (85730554 → 65730554, bukan 65730557), lalu yang terkuat
                    .OrderBy(t => SkuMatcher.Levenshtein(t.Sku, weak))
                    .ThenByDescending(t => Strength(result, groups[t.Sku]))
                    .Cast<(string, double)?>()
                    .FirstOrDefault();
                if (target is not (string to, double similarity))
                {
                    continue;
                }

                done.Add(new Merge(weak, to, weakIndexes.Length, groups[to].Length, similarity));
                foreach (int i in weakIndexes)
                {
                    result[i] = result[i] with { Sku = to, Status = ReadStatus.Corrected };
                }

                changed = true;
                break; // kelompok berubah: hitung ulang
            }
        }

        return result;
    }

    /// <summary>Kekuatan SKU: jumlah kardus, lalu keyakinan OCR tertinggi.</summary>
    private static (int Count, double Confidence) Strength(SkuReading[] readings, int[] indexes) =>
        (indexes.Length, indexes.Max(i => readings[i].Confidence));

    private static double MaxSimilarity(IReadOnlyList<float[]> embeddings, int[] a, int[] b) =>
        a.SelectMany(i => b.Select(j => AppearanceInference.Dot(embeddings[i], embeddings[j]))).DefaultIfEmpty(0).Max();
}
