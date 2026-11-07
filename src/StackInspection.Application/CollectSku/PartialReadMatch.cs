using StackInspection.Domain;

namespace StackInspection.Application.CollectSku;

/// <summary>
/// Melengkapi nomor SKU yang hanya terbaca sebagian karena label terpotong atau tertutup (plastik, stiker, body
/// kendaraan). Potongan digit yang merupakan bagian berurutan dari tepat satu SKU acuan dianggap SKU itu, misalnya
/// <c>1972453</c> → <c>64972453</c> (digit pertama yang terpotong sering terbaca salah). Acuan = SKU yang terbaca
/// utuh di foto yang sama ditambah <c>candidateSkus</c>. Hasilnya berstatus <see cref="ReadStatus.Corrected"/>.
/// </summary>
public static class PartialReadMatch
{
    /// <summary>Potongan sepanjang ini atau lebih boleh berbeda 1 digit dari bagian SKU.</summary>
    public const int OneMismatchMinDigits = 7;

    /// <param name="readings">Hasil baca per kardus.</param>
    /// <param name="partialDigits">Potongan digit hasil OCR per kardus (urutan sama dengan <paramref name="readings"/>).</param>
    /// <param name="candidates">SKU dari request (boleh kosong).</param>
    /// <param name="minDigits">Panjang potongan minimal (0 = nonaktif).</param>
    /// <param name="dominantMinCount">
    /// Jika potongan cocok ke beberapa SKU, SKU yang terbaca di minimal sekian kardus foto yang sama dan
    /// <paramref name="dominance"/> kali lebih sering dari pesaing terbanyaknya tetap dipakai (mis. <c>65590</c> →
    /// 65590059 di 33 kardus, bukan salah baca 65590053 di 1 kardus). 0 = harus tepat satu SKU.
    /// </param>
    /// <param name="dominance">Rasio jumlah kardus SKU dominan terhadap pesaingnya.</param>
    public static IReadOnlyList<SkuReading> Apply(
        IReadOnlyList<SkuReading> readings,
        IReadOnlyList<IReadOnlyList<string>> partialDigits,
        IReadOnlyCollection<string> candidates,
        int minDigits,
        int dominantMinCount = 0,
        int dominance = 3)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(partialDigits);
        ArgumentNullException.ThrowIfNull(candidates);
        if (minDigits <= 0)
        {
            return readings;
        }

        Dictionary<string, int> counts = readings
            .Where(r => r.Status is ReadStatus.Matched or ReadStatus.Corrected)
            .GroupBy(r => r.Sku, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        string[] references = [.. counts.Keys.Concat(candidates).Distinct(StringComparer.Ordinal)];
        if (references.Length == 0)
        {
            return readings;
        }

        SkuReading[] result = [.. readings];
        for (int i = 0; i < result.Length; i++)
        {
            SkuReading reading = result[i];
            if (reading.Status is not (ReadStatus.Unknown or ReadStatus.LabelNotVisible))
            {
                continue;
            }

            foreach (string digits in partialDigits[i].Where(d => d.Length >= minDigits).OrderByDescending(d => d.Length))
            {
                string[] matches = [.. references.Where(sku => IsPartOf(digits, sku))];
                string? chosen = matches.Length == 1 ? matches[0] : Dominant(matches, counts, dominantMinCount, dominance);
                if (chosen is not null)
                {
                    result[i] = reading with { Sku = chosen, OcrText = reading.OcrText ?? digits, Status = ReadStatus.Corrected };
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>SKU yang jauh lebih sering terbaca di foto daripada kecocokan lain; null jika tidak ada.</summary>
    private static string? Dominant(string[] matches, Dictionary<string, int> counts, int minCount, int dominance)
    {
        if (minCount <= 0 || matches.Length < 2)
        {
            return null;
        }

        (string Sku, int Count)[] ranked = [.. matches
            .Select(m => (Sku: m, Count: counts.GetValueOrDefault(m)))
            .OrderByDescending(m => m.Count)];
        return ranked[0].Count >= minCount && ranked[0].Count >= dominance * Math.Max(1, ranked[1].Count) ? ranked[0].Sku : null;
    }

    /// <summary>
    /// Semua digit <paramref name="digits"/> muncul di <paramref name="sku"/> dengan urutan yang sama, boleh ada digit
    /// SKU yang terlewat (OCR melewatkan beberapa digit: <c>655959</c> dari 65590059). Terlalu longgar untuk
    /// melengkapi nomor sendiri; dipakai hanya sebagai syarat tidak-bertentangan untuk inferensi tampilan.
    /// </summary>
    public static bool IsSubsequenceOf(string digits, string sku)
    {
        ArgumentNullException.ThrowIfNull(digits);
        ArgumentNullException.ThrowIfNull(sku);
        if (digits.Length == 0 || digits.Length >= sku.Length)
        {
            return false;
        }

        int k = 0;
        foreach (char c in sku)
        {
            if (k < digits.Length && digits[k] == c)
            {
                k++;
            }
        }

        return k == digits.Length;
    }

    /// <summary>
    /// <paramref name="digits"/> sama dengan bagian berurutan <paramref name="sku"/> (lebih pendek dari SKU), dengan
    /// toleransi 1 digit berbeda jika panjangnya ≥ <see cref="OneMismatchMinDigits"/>; atau <paramref name="sku"/> dengan
    /// tepat satu digit hilang (bacaan sepanjang SKU − 1).
    /// </summary>
    public static bool IsPartOf(string digits, string sku)
    {
        ArgumentNullException.ThrowIfNull(digits);
        ArgumentNullException.ThrowIfNull(sku);
        if (digits.Length == 0 || digits.Length >= sku.Length)
        {
            return false;
        }

        // OCR kadang melewatkan satu digit di tengah (tuning case 01: 6471156 = 64871156 tanpa angka 8)
        if (digits.Length == sku.Length - 1 && SkuReconstruction.IsOneDeletion(digits, sku))
        {
            return true;
        }

        int allowed = digits.Length >= OneMismatchMinDigits ? 1 : 0;
        for (int start = 0; start + digits.Length <= sku.Length; start++)
        {
            int mismatches = 0;
            for (int k = 0; k < digits.Length && mismatches <= allowed; k++)
            {
                if (digits[k] != sku[start + k])
                {
                    mismatches++;
                }
            }

            if (mismatches <= allowed)
            {
                return true;
            }
        }

        return false;
    }
}
