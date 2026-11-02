namespace StackInspection.Infrastructure.Vision;

/// <summary>
/// Satu potongan foto (<c>Source</c>, koordinat foto) yang ditempel di halaman pada (<c>X</c>, <c>Y</c>),
/// diperbesar <c>Scale</c> kali (ukuran di halaman = <see cref="Width"/> × <see cref="Height"/>).
/// </summary>
public sealed record OcrPiece(int Page, int X, int Y, OcrRect Source, double Scale = 1)
{
    public int Width => Math.Max(1, (int)Math.Round(Source.Width * Scale));

    public int Height => Math.Max(1, (int)Math.Round(Source.Height * Scale));
}

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
/// <remarks>
/// Area yang lebih sempit dari <c>minWidth</c> diperbesar sampai selebar itu (maksimal <c>maxScale</c> kali):
/// teks label di dus kecil/foto beresolusi rendah hanya ±8–13 px, di bawah ukuran yang andal untuk detektor teks.
/// </remarks>
public static class OcrPacker
{
    public static OcrPacking Pack(
        IReadOnlyList<OcrRect> areas, int pageSize, int gap, int overlap, int minWidth = 0, double maxScale = 1)
    {
        ArgumentNullException.ThrowIfNull(areas);
        int usable = pageSize - (2 * gap);
        List<(OcrRect Source, double Scale)> pieces = [];
        foreach (OcrRect area in areas.Where(a => a.Width > 0 && a.Height > 0))
        {
            double scale = UpscaleOf(area.Width, minWidth, maxScale);
            int limit = (int)Math.Floor(usable / scale);
            if (area.Width <= limit && area.Height <= limit)
            {
                pieces.Add((area, scale));
                continue;
            }

            int tileOverlap = Math.Min((int)(overlap / scale), limit / 2);
            foreach (OcrTile tile in OcrTiling.Tiles(area.Width, area.Height, limit, tileOverlap))
            {
                pieces.Add((new OcrRect(area.X + tile.X, area.Y + tile.Y, tile.Width, tile.Height), scale));
            }
        }

        List<OcrPiece> placed = [];
        int page = 0;
        int x = gap;
        int y = gap;
        int shelf = 0;
        foreach (OcrPiece piece in pieces
            .Select(p => new OcrPiece(0, 0, 0, p.Source, p.Scale))
            .OrderByDescending(p => p.Height)
            .ThenByDescending(p => p.Width))
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

            placed.Add(piece with { Page = page, X = x, Y = y });
            x += piece.Width + gap;
            shelf = Math.Max(shelf, piece.Height);
        }

        return new OcrPacking(placed, placed.Count == 0 ? 0 : page + 1);
    }

    /// <summary>Faktor perbesaran: area selebar <paramref name="width"/> dibesarkan sampai <paramref name="minWidth"/>, maksimal <paramref name="maxScale"/>.</summary>
    public static double UpscaleOf(int width, int minWidth, double maxScale) =>
        minWidth > width && width > 0 ? Math.Min(Math.Max(1, maxScale), (double)minWidth / width) : 1;
}
