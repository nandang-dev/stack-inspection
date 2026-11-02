namespace StackInspection.Infrastructure.Vision;

/// <summary>Satu potongan foto (<c>Source</c>, koordinat foto) yang ditempel di halaman pada (<c>X</c>, <c>Y</c>).</summary>
public sealed record OcrPiece(int Page, int X, int Y, OcrRect Source);

/// <summary>Persegi panjang integer dalam koordinat foto.</summary>
public readonly record struct OcrRect(int X, int Y, int Width, int Height);

/// <summary>Hasil pengemasan: potongan per halaman deteksi berukuran <c>pageSize</c> × <c>pageSize</c>.</summary>
public sealed record OcrPacking(IReadOnlyList<OcrPiece> Pieces, int PageCount);

/// <summary>
/// Mengemas area-area kecil (mis. area label setiap kardus) serapat mungkin ke beberapa halaman persegi
/// (shelf packing, tinggi menurun), dengan celah putih di antaranya. Deteksi teks cukup dijalankan per halaman,
/// sehingga yang diproses hanya luas area itu sendiri, bukan seluruh foto/muatan. Area yang lebih besar dari
/// satu halaman dipotong menjadi tile bertumpang tindih lebih dulu.
/// </summary>
public static class OcrPacker
{
    public static OcrPacking Pack(IReadOnlyList<OcrRect> areas, int pageSize, int gap, int overlap)
    {
        ArgumentNullException.ThrowIfNull(areas);
        int usable = pageSize - gap;
        List<OcrRect> pieces = [];
        foreach (OcrRect area in areas.Where(a => a.Width > 0 && a.Height > 0))
        {
            if (area.Width <= usable && area.Height <= usable)
            {
                pieces.Add(area);
                continue;
            }

            foreach (OcrTile tile in OcrTiling.Tiles(area.Width, area.Height, usable, Math.Min(overlap, usable / 2)))
            {
                pieces.Add(new OcrRect(area.X + tile.X, area.Y + tile.Y, tile.Width, tile.Height));
            }
        }

        List<OcrPiece> placed = [];
        int page = 0;
        int x = gap;
        int y = gap;
        int shelf = 0;
        foreach (OcrRect piece in pieces.OrderByDescending(p => p.Height).ThenByDescending(p => p.Width))
        {
            if (x + piece.Width + gap > pageSize)
            {
                x = gap;
                y += shelf + gap;
                shelf = 0;
            }

            if (y + piece.Height + gap > pageSize)
            {
                page++;
                x = gap;
                y = gap;
                shelf = 0;
            }

            placed.Add(new OcrPiece(page, x, y, piece));
            x += piece.Width + gap;
            shelf = Math.Max(shelf, piece.Height);
        }

        return new OcrPacking(placed, placed.Count == 0 ? 0 : page + 1);
    }
}
