using StackInspection.Domain;

namespace StackInspection.Infrastructure.Vision;

/// <summary>Satu tile (koordinat relatif terhadap area yang dipotong).</summary>
public readonly record struct OcrTile(int X, int Y, int Width, int Height);

/// <summary>
/// Perhitungan murni untuk OCR sekali jalan: membagi area kardus menjadi tile berukuran tetap
/// (bentuk input selalu sama → cache oneDNN terpakai) dan membuang deteksi teks ganda dari tile yang
/// saling tumpang tindih.
/// </summary>
public static class OcrTiling
{
    public static IReadOnlyList<OcrTile> Tiles(int width, int height, int tileSize, int overlap)
    {
        if (tileSize <= 0 || overlap < 0 || overlap >= tileSize)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), "Syarat: tileSize > 0 dan 0 ≤ overlap < tileSize.");
        }

        List<OcrTile> tiles = [];
        foreach (int y in Starts(height, tileSize, overlap))
        {
            foreach (int x in Starts(width, tileSize, overlap))
            {
                tiles.Add(new OcrTile(x, y, Math.Min(tileSize, width - x), Math.Min(tileSize, height - y)));
            }
        }

        return tiles;
    }

    /// <summary>Gabungan semua area (bounding box terluar) ditambah margin, di-clamp ke ukuran foto.</summary>
    public static BoundingBox Union(IReadOnlyList<BoundingBox> regions, double margin, int imageWidth, int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(regions);
        return new BoundingBox(
            Math.Clamp(regions.Min(r => r.X1) - margin, 0, imageWidth),
            Math.Clamp(regions.Min(r => r.Y1) - margin, 0, imageHeight),
            Math.Clamp(regions.Max(r => r.X2) + margin, 0, imageWidth),
            Math.Clamp(regions.Max(r => r.Y2) + margin, 0, imageHeight));
    }

    /// <summary>
    /// Indeks deteksi yang dipertahankan: kotak diproses dari yang terbesar; kotak yang sebagian besar
    /// berada di dalam kotak yang sudah dipilih (potongan teks di tepi tile) dibuang.
    /// </summary>
    public static IReadOnlyList<int> Deduplicate(IReadOnlyList<BoundingBox> boxes, double containmentThreshold)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        List<int> kept = [];
        foreach (int index in Enumerable.Range(0, boxes.Count).OrderByDescending(i => boxes[i].Width * boxes[i].Height))
        {
            if (!kept.Exists(k => ContainmentFilter.Containment(boxes[k], boxes[index]) >= containmentThreshold))
            {
                kept.Add(index);
            }
        }

        kept.Sort();
        return kept;
    }

    private static IEnumerable<int> Starts(int length, int tileSize, int overlap)
    {
        if (length <= tileSize)
        {
            yield return 0;
            yield break;
        }

        int step = tileSize - overlap;
        int position = 0;
        while (position + tileSize < length)
        {
            yield return position;
            position += step;
        }

        yield return length - tileSize;
    }
}
