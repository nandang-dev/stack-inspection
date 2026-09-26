using System.Text;

namespace StackInspection.Domain;

/// <summary>Hasil pencocokan teks OCR ke kode SKU.</summary>
public sealed record SkuMatch(string Sku, ReadStatus Status, int Distance);

/// <summary>Menormalisasi teks OCR ke digit lalu mencocokkannya ke candidate SKU (Levenshtein).</summary>
public static class SkuMatcher
{
    public const int SkuLength = 8;

    /// <summary>Huruf yang sering tertukar dengan angka oleh OCR diubah ke angka, sisanya dibuang.</summary>
    public static string NormalizeDigits(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder builder = new(text.Length);
        foreach (char c in text)
        {
            char mapped = c switch
            {
                'O' or 'o' or 'Q' or 'D' => '0',
                'I' or 'l' or '|' or 'i' => '1',
                'Z' or 'z' => '2',
                'S' or 's' => '5',
                'G' => '6',
                'B' => '8',
                _ => c,
            };
            if (char.IsAsciiDigit(mapped))
            {
                builder.Append(mapped);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Tanpa candidate: diterima hanya jika tepat 8 digit. Dengan candidate: sama persis → Matched,
    /// jarak terbaik ≤ <paramref name="maxDistance"/> dan unik → Corrected, selain itu Unknown.
    /// </summary>
    public static SkuMatch Match(string ocrText, IReadOnlyCollection<string> candidates, int maxDistance)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        string digits = NormalizeDigits(ocrText);
        if (candidates.Count == 0)
        {
            return digits.Length == SkuLength
                ? new SkuMatch(digits, ReadStatus.Matched, 0)
                : new SkuMatch(SkuCodes.Unknown, ReadStatus.Unknown, int.MaxValue);
        }

        int best = int.MaxValue;
        string? bestSku = null;
        bool tie = false;
        foreach (string candidate in candidates)
        {
            int distance = Levenshtein(digits, candidate);
            if (distance < best)
            {
                best = distance;
                bestSku = candidate;
                tie = false;
            }
            else if (distance == best && !string.Equals(candidate, bestSku, StringComparison.Ordinal))
            {
                tie = true;
            }
        }

        if (bestSku is null || tie || best > maxDistance)
        {
            return new SkuMatch(SkuCodes.Unknown, ReadStatus.Unknown, best);
        }

        return new SkuMatch(bestSku, best == 0 ? ReadStatus.Matched : ReadStatus.Corrected, best);
    }

    public static int Levenshtein(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
