using StackInspection.Domain;

namespace StackInspection.Application.CollectSku;

/// <summary>
/// Koreksi SKU berdasarkan mayoritas di foto yang sama, dipakai hanya jika request tidak membawa
/// <c>candidateSkus</c>. Hasil baca yang berbeda sedikit (≤ <c>maxDistance</c> digit) dari SKU yang jauh
/// lebih sering muncul dianggap salah baca OCR (misalnya <c>85730554</c> di antara 32× <c>65730554</c>)
/// dan diganti dengan status <see cref="ReadStatus.Corrected"/>.
/// </summary>
public static class MajorityCorrection
{
    /// <param name="readings">Hasil baca per kardus.</param>
    /// <param name="digitVariants">Semua kemungkinan string digit hasil OCR per kardus (urutan sama dengan <paramref name="readings"/>).</param>
    /// <param name="minCount">SKU dianggap mayoritas jika terbaca minimal sekian kali (0 = nonaktif).</param>
    /// <param name="dominance">SKU mayoritas harus muncul ≥ sekian kali lipat dari SKU yang dikoreksi.</param>
    /// <param name="maxDistance">Selisih digit (Levenshtein) maksimal agar dikoreksi.</param>
    public static IReadOnlyList<SkuReading> Apply(
        IReadOnlyList<SkuReading> readings,
        IReadOnlyList<IReadOnlyList<string>> digitVariants,
        int minCount,
        int dominance,
        int maxDistance)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(digitVariants);
        if (minCount <= 0)
        {
            return readings;
        }

        Dictionary<string, int> counts = readings
            .Where(r => r.Status is ReadStatus.Matched or ReadStatus.Corrected)
            .GroupBy(r => r.Sku, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        KeyValuePair<string, int>[] majority = [.. counts.Where(c => c.Value >= minCount)];
        if (majority.Length == 0)
        {
            return readings;
        }

        SkuReading[] result = [.. readings];
        for (int i = 0; i < result.Length; i++)
        {
            SkuReading reading = result[i];
            if (reading.Status == ReadStatus.LabelNotVisible)
            {
                continue;
            }

            int ownCount = reading.Status == ReadStatus.Unknown ? 0 : counts.GetValueOrDefault(reading.Sku);
            List<(string Sku, int Distance)> targets = [];
            foreach ((string sku, int count) in majority)
            {
                if (string.Equals(sku, reading.Sku, StringComparison.Ordinal) || count < dominance * Math.Max(ownCount, 1))
                {
                    continue;
                }

                int distance = digitVariants[i].Count == 0 ? int.MaxValue : digitVariants[i].Min(v => SkuMatcher.Levenshtein(v, sku));
                if (distance <= maxDistance)
                {
                    targets.Add((sku, distance));
                }
            }

            if (targets.Count == 0)
            {
                continue;
            }

            int best = targets.Min(t => t.Distance);
            string[] nearest = [.. targets.Where(t => t.Distance == best).Select(t => t.Sku)];
            if (nearest.Length == 1)
            {
                result[i] = reading with { Sku = nearest[0], Status = ReadStatus.Corrected };
            }
        }

        return result;
    }
}
