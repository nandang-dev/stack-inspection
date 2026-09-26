namespace StackInspection.Domain;

/// <summary>
/// Membuang kotak ganda bersarang: kotak kecil yang sebagian besar luasnya berada di dalam kotak lain
/// (misalnya satu kardus terdeteksi utuh sekaligus hanya bagian labelnya). NMS biasa tidak menangkap
/// kasus ini karena IoU-nya kecil. Dari setiap pasangan, kotak dengan confidence lebih rendah dibuang.
/// </summary>
public static class ContainmentFilter
{
    public static IReadOnlyList<CartonBox> Apply(IReadOnlyList<CartonBox> cartons, double containmentThreshold)
    {
        ArgumentNullException.ThrowIfNull(cartons);
        if (containmentThreshold <= 0 || containmentThreshold > 1)
        {
            return cartons; // 0 = nonaktif
        }

        List<CartonBox> sorted = [.. cartons.OrderByDescending(c => c.Confidence)];
        List<CartonBox> kept = [];
        foreach (CartonBox candidate in sorted)
        {
            if (!kept.Any(k => Containment(k.Box, candidate.Box) >= containmentThreshold))
            {
                kept.Add(candidate);
            }
        }

        // pertahankan urutan asli agar hasil deterministik terhadap input
        return [.. cartons.Where(kept.Contains)];
    }

    /// <summary>Luas irisan dibagi luas kotak yang lebih kecil (1 = kotak kecil sepenuhnya di dalam).</summary>
    public static double Containment(BoundingBox a, BoundingBox b)
    {
        double interW = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        double interH = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        double smaller = Math.Min(Math.Max(0, a.Width) * Math.Max(0, a.Height), Math.Max(0, b.Width) * Math.Max(0, b.Height));
        return smaller <= 0 ? 0 : interW * interH / smaller;
    }
}
