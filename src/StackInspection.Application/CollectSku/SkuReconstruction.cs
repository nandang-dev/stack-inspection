using StackInspection.Domain;

namespace StackInspection.Application.CollectSku;

/// <summary>
/// Merekonstruksi SKU dari bacaan yang kehilangan satu digit. OCR angka kadang melewatkan satu digit di posisi mana
/// pun dengan keyakinan tetap tinggi (tuning case 01: Molto 64871156 terbaca <c>6871156</c> dan <c>6487156</c>,
/// Pepsodent 64955462 terbaca <c>6495562</c> dan <c>6495546</c>). Satu bacaan seperti itu tidak cukup (ada puluhan
/// kemungkinan digit yang hilang), tetapi dua bacaan dari kardus yang terlihat sama biasanya hanya cocok dengan satu
/// SKU utuh: SKU yang jika dihapus satu digitnya menghasilkan masing-masing bacaan.
/// </summary>
/// <remarks>
/// Kandidat harus dijelaskan oleh minimal dua kardus berkemiripan tampilan ≥ <c>minSimilarity</c>. Urutan pemilihan:
/// jumlah kardus pendukung, lalu rata-rata keyakinan OCR bacaan pendukung (Pepsodent: 64955462 dari bacaan 0,99 dan
/// 0,98 mengalahkan 64955465 dari 0,97 dan 0,98), lalu kedekatan (≤ 1 digit) dengan bacaan utuh di kelompok yang sama;
/// jika tetap seri, tidak ada yang dipakai.
/// Hasilnya berstatus <see cref="ReadStatus.Corrected"/> dan menjadi acuan untuk SKU kembar, potongan nomor, dan
/// inferensi tampilan.
/// </remarks>
public static class SkuReconstruction
{
    /// <summary>Satu SKU hasil rekonstruksi, untuk log.</summary>
    public sealed record Reconstruction(string Sku, IReadOnlyList<string> Reads, int Cartons);

    /// <param name="readings">Hasil baca per kardus.</param>
    /// <param name="digitVariants">String digit hasil OCR per kardus (urutan sama dengan <paramref name="readings"/>).</param>
    /// <param name="embeddings">Vektor tampilan per kardus (panjang 1); kosong = tidak ada rekonstruksi.</param>
    /// <param name="skuLength">Panjang SKU utuh; bacaan sepanjang <c>skuLength − 1</c> yang direkonstruksi.</param>
    /// <param name="minSimilarity">Kemiripan tampilan minimal antar kardus pendukung.</param>
    /// <param name="found">SKU yang direkonstruksi.</param>
    public static IReadOnlyList<SkuReading> Apply(
        IReadOnlyList<SkuReading> readings,
        IReadOnlyList<IReadOnlyList<string>> digitVariants,
        IReadOnlyList<float[]> embeddings,
        int skuLength,
        double minSimilarity,
        out IReadOnlyList<Reconstruction> found)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(digitVariants);
        ArgumentNullException.ThrowIfNull(embeddings);
        List<Reconstruction> done = [];
        found = done;
        if (embeddings.Count != readings.Count || digitVariants.Count != readings.Count || minSimilarity <= 0)
        {
            return readings;
        }

        // Kardus yang belum terbaca tetapi punya bacaan kurang satu digit
        Dictionary<int, string[]> shortReads = Enumerable.Range(0, readings.Count)
            .Where(i => readings[i].Status is ReadStatus.Unknown or ReadStatus.LabelNotVisible)
            .Select(i => (Index: i, Reads: digitVariants[i].Where(v => v.Length == skuLength - 1).Distinct(StringComparer.Ordinal).ToArray()))
            .Where(x => x.Reads.Length > 0)
            .ToDictionary(x => x.Index, x => x.Reads);
        if (shortReads.Count < 2)
        {
            return readings;
        }

        int[] fullReads = [.. Enumerable.Range(0, readings.Count).Where(i => readings[i].Status is ReadStatus.Matched or ReadStatus.Corrected)];
        bool Similar(int a, int b) => AppearanceInference.Dot(embeddings[a], embeddings[b]) >= minSimilarity;

        // Kandidat dari setiap pasangan kardus mirip; pendukung = kardus mirip yang bacaannya kandidat minus satu digit
        Dictionary<string, Candidate> candidates = new(StringComparer.Ordinal);
        int[] keys = [.. shortReads.Keys];
        for (int a = 0; a < keys.Length; a++)
        {
            for (int b = a + 1; b < keys.Length; b++)
            {
                int i = keys[a];
                int j = keys[b];
                if (!Similar(i, j))
                {
                    continue;
                }

                foreach (string sku in shortReads[i].SelectMany(Insertions).Intersect(shortReads[j].SelectMany(Insertions), StringComparer.Ordinal))
                {
                    if (candidates.ContainsKey(sku))
                    {
                        continue;
                    }

                    HashSet<int> support = [.. keys.Where(k => (k == i || Similar(k, i)) && shortReads[k].Any(r => IsOneDeletion(r, sku)))];
                    // Bacaan utuh di kelompok yang sama yang selisih ≤ 1 digit: pemecah seri terakhir
                    int nearFull = fullReads.Count(f => Similar(f, i) && SkuMatcher.Levenshtein(readings[f].Sku, sku) <= 1);
                    candidates[sku] = new Candidate(support, support.Average(k => readings[k].Confidence), nearFull);
                }
            }
        }

        SkuReading[] result = [.. readings];
        HashSet<int> assigned = [];
        foreach ((string sku, Candidate candidate) in candidates
            .Where(c => c.Value.Support.Count >= 2)
            .OrderByDescending(c => c.Value.Rank))
        {
            int[] free = [.. candidate.Support.Where(k => !assigned.Contains(k))];
            if (free.Length < 2)
            {
                continue;
            }

            // Kandidat lain yang sama kuat untuk kardus yang sama: ambigu, jangan dipilih
            bool tie = candidates.Any(c => c.Key != sku && c.Value.Rank.Equals(candidate.Rank) && c.Value.Support.Overlaps(free));
            if (tie)
            {
                continue;
            }

            foreach (int k in free)
            {
                result[k] = result[k] with { Sku = sku, Status = ReadStatus.Corrected };
                assigned.Add(k);
            }

            done.Add(new Reconstruction(sku, [.. free.Select(k => shortReads[k].First(r => IsOneDeletion(r, sku)))], free.Length));
        }

        return result;
    }

    /// <summary>Kandidat SKU: kardus pendukung, rata-rata keyakinan OCR-nya, dan jumlah bacaan utuh yang dekat.</summary>
    private sealed record Candidate(HashSet<int> Support, double Confidence, int NearFull)
    {
        /// <summary>Urutan pemilihan (keyakinan dibulatkan supaya selisih pembulatan float tidak dianggap beda).</summary>
        public (int Support, double Confidence, int NearFull) Rank => (Support.Count, Math.Round(Confidence, 4), NearFull);
    }

    /// <summary>Semua string yang didapat dengan menyisipkan satu digit ke <paramref name="read"/>.</summary>
    private static IEnumerable<string> Insertions(string read)
    {
        for (int position = 0; position <= read.Length; position++)
        {
            for (char digit = '0'; digit <= '9'; digit++)
            {
                yield return string.Concat(read.AsSpan(0, position), new ReadOnlySpan<char>(in digit), read.AsSpan(position));
            }
        }
    }

    /// <summary><paramref name="read"/> = <paramref name="sku"/> dengan tepat satu digit dihapus.</summary>
    public static bool IsOneDeletion(string read, string sku)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(sku);
        if (read.Length != sku.Length - 1)
        {
            return false;
        }

        int i = 0;
        while (i < read.Length && read[i] == sku[i])
        {
            i++;
        }

        return read.AsSpan(i).SequenceEqual(sku.AsSpan(i + 1));
    }
}
