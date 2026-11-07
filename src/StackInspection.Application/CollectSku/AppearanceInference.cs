using StackInspection.Domain;

namespace StackInspection.Application.CollectSku;

/// <summary>
/// Mengisi SKU kardus yang labelnya tidak terbaca (<see cref="ReadStatus.Unknown"/> /
/// <see cref="ReadStatus.LabelNotVisible"/>) dari kardus lain di foto yang sama yang tampilannya sangat mirip.
/// Syarat (semua harus terpenuhi):
/// <list type="bullet">
/// <item>minimal <c>minReferences</c> kardus acuan (SKU terbaca OCR) dengan SKU yang sama berkemiripan ≥ <c>minSimilarity</c>;</item>
/// <item>kemiripan tertinggi ke SKU lain lebih rendah minimal <c>minMargin</c>.</item>
/// </list>
/// Satu acuan saja tidak cukup: kardus berbungkus plastik terlihat mirip satu sama lain walau SKU-nya berbeda.
/// </summary>
public static class AppearanceInference
{
    /// <param name="readings">Hasil baca per kardus.</param>
    /// <param name="embeddings">Vektor tampilan per kardus (panjang 1, urutan sama dengan <paramref name="readings"/>).</param>
    /// <param name="minSimilarity">Kemiripan (cosine) minimal ke kardus acuan.</param>
    /// <param name="minReferences">Jumlah kardus acuan ber-SKU sama yang harus lolos <paramref name="minSimilarity"/> (0 = nonaktif).</param>
    /// <param name="minMargin">Selisih kemiripan minimal antara SKU terbaik dan SKU lain.</param>
    /// <param name="consistent">
    /// Syarat per (indeks kardus, SKU tebakan): SKU tidak bertentangan dengan angka yang sudah terbaca di kardus itu
    /// (tuning case 01: Molto terbaca <c>6871156</c> tidak boleh diberi SKU Sunlight 65590059 walau tampilannya mirip).
    /// Jika gagal, kardus tidak diisi. null = tanpa syarat.
    /// </param>
    public static IReadOnlyList<SkuReading> Apply(
        IReadOnlyList<SkuReading> readings,
        IReadOnlyList<float[]> embeddings,
        double minSimilarity,
        int minReferences,
        double minMargin,
        Func<int, string, bool>? consistent = null)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(embeddings);
        if (embeddings.Count != readings.Count || minReferences <= 0)
        {
            return readings;
        }

        int[] references = [.. Enumerable.Range(0, readings.Count)
            .Where(i => readings[i].Status is ReadStatus.Matched or ReadStatus.Corrected)];
        SkuReading[] result = [.. readings];
        for (int i = 0; i < result.Length; i++)
        {
            if (readings[i].Status is not (ReadStatus.Unknown or ReadStatus.LabelNotVisible))
            {
                continue;
            }

            // Per SKU acuan: kemiripan tertinggi dan jumlah kardus yang lolos ambang.
            Dictionary<string, (double Best, int Similar)> bySku = new(StringComparer.Ordinal);
            foreach (int j in references)
            {
                double similarity = Dot(embeddings[i], embeddings[j]);
                (double best, int similar) = bySku.GetValueOrDefault(readings[j].Sku);
                bySku[readings[j].Sku] = (Math.Max(best, similarity), similar + (similarity >= minSimilarity ? 1 : 0));
            }

            KeyValuePair<string, (double Best, int Similar)>[] ranked = [.. bySku.OrderByDescending(s => s.Value.Best)];
            if (ranked.Length == 0 || ranked[0].Value.Similar < minReferences)
            {
                continue;
            }

            double runnerUp = ranked.Length > 1 ? ranked[1].Value.Best : double.NegativeInfinity;
            if (ranked[0].Value.Best - runnerUp >= minMargin && (consistent is null || consistent(i, ranked[0].Key)))
            {
                // Confidence = kemiripan tampilan ke acuan terbaik (bukan keyakinan OCR)
                result[i] = readings[i] with { Sku = ranked[0].Key, Confidence = ranked[0].Value.Best, Status = ReadStatus.InferredByAppearance };
            }
        }

        return result;
    }

    /// <summary>Kemiripan tertinggi kardus <paramref name="index"/> ke kardus terbaca ber-SKU <paramref name="sku"/>.</summary>
    public static double BestSimilarity(
        IReadOnlyList<SkuReading> readings, IReadOnlyList<float[]> embeddings, int index, string sku)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(embeddings);
        double best = double.NegativeInfinity;
        for (int j = 0; j < readings.Count; j++)
        {
            if (j != index && readings[j].Status is ReadStatus.Matched or ReadStatus.Corrected
                && string.Equals(readings[j].Sku, sku, StringComparison.Ordinal))
            {
                best = Math.Max(best, Dot(embeddings[index], embeddings[j]));
            }
        }

        return best;
    }

    public static double Dot(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length != b.Length)
        {
            return 0;
        }

        double sum = 0;
        for (int k = 0; k < a.Length; k++)
        {
            sum += a[k] * b[k];
        }

        return sum;
    }
}
