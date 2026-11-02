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
    public static IReadOnlyList<SkuReading> Apply(
        IReadOnlyList<SkuReading> readings,
        IReadOnlyList<IReadOnlyList<string>> partialDigits,
        IReadOnlyCollection<string> candidates,
        int minDigits)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(partialDigits);
        ArgumentNullException.ThrowIfNull(candidates);
        if (minDigits <= 0)
        {
            return readings;
        }

        string[] references = [.. readings
            .Where(r => r.Status is ReadStatus.Matched or ReadStatus.Corrected)
            .Select(r => r.Sku)
            .Concat(candidates)
            .Distinct(StringComparer.Ordinal)];
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
                if (matches.Length == 1)
                {
                    result[i] = reading with { Sku = matches[0], OcrText = reading.OcrText ?? digits, Status = ReadStatus.Corrected };
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// <paramref name="digits"/> sama dengan bagian berurutan <paramref name="sku"/> (lebih pendek dari SKU), dengan
    /// toleransi 1 digit berbeda jika panjangnya ≥ <see cref="OneMismatchMinDigits"/>.
    /// </summary>
    public static bool IsPartOf(string digits, string sku)
    {
        ArgumentNullException.ThrowIfNull(digits);
        ArgumentNullException.ThrowIfNull(sku);
        if (digits.Length == 0 || digits.Length >= sku.Length)
        {
            return false;
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
