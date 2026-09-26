namespace StackInspection.Domain;

/// <summary>Jenis pelanggaran aturan tumpukan.</summary>
public enum ViolationType
{
    UnknownSku,
    LabelNotVisible,
    MaxStackExceeded,
    ClassPositionInvalid,
}

/// <summary>Pelanggaran pada satu sel.</summary>
public sealed record Violation(ViolationType Type, DetectedCell Cell, string Message);

/// <summary>Satu kolom tumpukan, sel diurutkan dari bawah ke atas.</summary>
public sealed record StackColumn(int Column, IReadOnlyList<DetectedCell> BottomToTop);

/// <summary>Data yang dibaca oleh semua aturan.</summary>
public sealed class StackEvaluationContext
{
    public StackEvaluationContext(IReadOnlyList<DetectedCell> cells, IReadOnlyDictionary<string, MasterSku> master)
    {
        ArgumentNullException.ThrowIfNull(cells);
        Cells = cells;
        Master = master;
        Columns = [.. cells
            .GroupBy(c => c.Column)
            .OrderBy(g => g.Key)
            .Select(g => new StackColumn(g.Key, [.. g.OrderBy(c => c.Level)]))];
    }

    public IReadOnlyList<DetectedCell> Cells { get; }

    public IReadOnlyDictionary<string, MasterSku> Master { get; }

    public IReadOnlyList<StackColumn> Columns { get; }

    /// <summary>Sel yang SKU-nya dikenal master (dievaluasi oleh aturan maxStack dan kelas).</summary>
    public bool IsEvaluable(DetectedCell cell) =>
        cell.ReadStatus != ReadStatus.LabelNotVisible && Master.ContainsKey(cell.Sku);
}

/// <summary>Satu aturan penilaian tumpukan.</summary>
public interface IStackRule
{
    IEnumerable<Violation> Evaluate(StackEvaluationContext context);
}

/// <summary>SKU "UNKNOWN" atau tidak ada di master: tidak dievaluasi aturan lain, dihitung gagal.</summary>
public sealed class UnknownSkuRule : IStackRule
{
    public IEnumerable<Violation> Evaluate(StackEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Cells
            .Where(c => c.ReadStatus != ReadStatus.LabelNotVisible && !context.Master.ContainsKey(c.Sku))
            .Select(c => new Violation(
                ViolationType.UnknownSku,
                c,
                $"SKU pada baris {c.Row} kolom {c.Column} tidak terbaca atau tidak ada di master"));
    }
}

/// <summary>Label SKU tidak menghadap kamera: dihitung gagal.</summary>
public sealed class LabelNotVisibleRule : IStackRule
{
    public IEnumerable<Violation> Evaluate(StackEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Cells
            .Where(c => c.ReadStatus == ReadStatus.LabelNotVisible)
            .Select(c => new Violation(
                ViolationType.LabelNotVisible,
                c,
                $"Label SKU pada baris {c.Row} kolom {c.Column} tidak terlihat - kardus harus dipasang dengan label menghadap ke luar"));
    }
}

/// <summary>Jumlah kardus SKU yang sama dalam satu kolom tidak boleh melebihi maxStack (kelebihan dihitung dari bawah).</summary>
public sealed class MaxStackRule : IStackRule
{
    public IEnumerable<Violation> Evaluate(StackEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (StackColumn column in context.Columns)
        {
            Dictionary<string, int> counts = [];
            foreach (DetectedCell cell in column.BottomToTop.Where(context.IsEvaluable))
            {
                int count = counts.GetValueOrDefault(cell.Sku) + 1;
                counts[cell.Sku] = count;
                MasterSku master = context.Master[cell.Sku];
                if (count > master.MaxStack)
                {
                    yield return new Violation(
                        ViolationType.MaxStackExceeded,
                        cell,
                        $"SKU {cell.Sku} melebihi max_stack ({master.MaxStack}) pada kolom {cell.Column}");
                }
            }
        }
    }
}

/// <summary>
/// Kelas A &lt; B &lt; C (C paling berat, harus di bawah). Kardus melanggar jika ada kardus mana pun
/// di bawahnya dalam kolom yang sama dengan kelas lebih rendah. Sel tanpa SKU dikenal dilewati.
/// </summary>
public sealed class ClassPositionRule : IStackRule
{
    public IEnumerable<Violation> Evaluate(StackEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (StackColumn column in context.Columns)
        {
            List<DetectedCell> known = [.. column.BottomToTop.Where(context.IsEvaluable)];
            for (int i = 0; i < known.Count; i++)
            {
                DetectedCell cell = known[i];
                SkuClass cellClass = context.Master[cell.Sku].Class;
                // pelanggar terdekat di bawahnya
                for (int j = i - 1; j >= 0; j--)
                {
                    DetectedCell below = known[j];
                    SkuClass belowClass = context.Master[below.Sku].Class;
                    if (belowClass < cellClass)
                    {
                        yield return new Violation(
                            ViolationType.ClassPositionInvalid,
                            cell,
                            $"Produk {cell.Sku} ({cellClass}) berada di atas produk {below.Sku} ({belowClass}) - kelas tidak sesuai");
                        break;
                    }
                }
            }
        }
    }
}
