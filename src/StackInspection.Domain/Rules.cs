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

/// <summary>Data yang dibaca oleh semua aturan. Satu <see cref="DetectedCell"/> = satu kardus (id unik).</summary>
public sealed class StackEvaluationContext
{
    private readonly Dictionary<int, DetectedCell> _byId;

    public StackEvaluationContext(IReadOnlyList<DetectedCell> cells, IReadOnlyDictionary<string, MasterSku> master)
    {
        ArgumentNullException.ThrowIfNull(cells);
        Cells = cells;
        Master = master;
        _byId = cells.ToDictionary(c => c.Id);
    }

    public IReadOnlyList<DetectedCell> Cells { get; }

    public IReadOnlyDictionary<string, MasterSku> Master { get; }

    /// <summary>Sel yang SKU-nya dikenal master (dievaluasi oleh aturan maxStack dan kelas).</summary>
    public bool IsEvaluable(DetectedCell cell) =>
        cell.ReadStatus != ReadStatus.LabelNotVisible && Master.ContainsKey(cell.Sku);

    /// <summary>Kardus yang langsung ditumpu <paramref name="cell"/>.</summary>
    public IEnumerable<DetectedCell> Supports(DetectedCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return cell.RestsOn.Select(id => _byId[id]);
    }

    /// <summary>Semua kardus di bawah <paramref name="cell"/> (menelusuri tumpuan sampai lantai), terdekat dulu.</summary>
    public IEnumerable<DetectedCell> Below(DetectedCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        HashSet<int> seen = [];
        Queue<DetectedCell> queue = new(Supports(cell));
        while (queue.Count > 0)
        {
            DetectedCell next = queue.Dequeue();
            if (!seen.Add(next.Id))
            {
                continue;
            }

            yield return next;
            foreach (DetectedCell support in Supports(next))
            {
                queue.Enqueue(support);
            }
        }
    }
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

/// <summary>
/// Jumlah kardus SKU yang sama dalam satu jalur tumpukan (bawah → atas mengikuti tumpuan) tidak boleh melebihi
/// maxStack; kelebihan dihitung dari bawah. Kardus yang tidak bisa dievaluasi dilewati tanpa memutus jalur.
/// </summary>
public sealed class MaxStackRule : IStackRule
{
    public IEnumerable<Violation> Evaluate(StackEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Dictionary<(int Id, string Sku), int> memo = [];

        // jumlah terbanyak kardus SKU ini di satu jalur dari lantai sampai cell (termasuk cell)
        int Count(DetectedCell cell, string sku)
        {
            if (memo.TryGetValue((cell.Id, sku), out int cached))
            {
                return cached;
            }

            int self = context.IsEvaluable(cell) && cell.Sku == sku ? 1 : 0;
            int below = context.Supports(cell).Select(s => Count(s, sku)).DefaultIfEmpty(0).Max();
            memo[(cell.Id, sku)] = self + below;
            return self + below;
        }

        foreach (DetectedCell cell in context.Cells.Where(context.IsEvaluable))
        {
            MasterSku master = context.Master[cell.Sku];
            if (Count(cell, cell.Sku) > master.MaxStack)
            {
                yield return new Violation(
                    ViolationType.MaxStackExceeded,
                    cell,
                    $"SKU {cell.Sku} melebihi max_stack ({master.MaxStack}) pada kolom {cell.Column}");
            }
        }
    }
}

/// <summary>
/// Kelas A &lt; B &lt; C (C paling berat, harus di bawah). Kardus melanggar jika ada kardus mana pun di bawahnya
/// (menelusuri tumpuan sampai lantai) dengan kelas lebih rendah; pesan menyebut yang terdekat. Sel tanpa SKU
/// dikenal dilewati.
/// </summary>
public sealed class ClassPositionRule : IStackRule
{
    public IEnumerable<Violation> Evaluate(StackEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (DetectedCell cell in context.Cells.Where(context.IsEvaluable))
        {
            SkuClass cellClass = context.Master[cell.Sku].Class;
            DetectedCell? below = context.Below(cell)
                .Where(context.IsEvaluable)
                .FirstOrDefault(b => context.Master[b.Sku].Class < cellClass);
            if (below is not null)
            {
                SkuClass belowClass = context.Master[below.Sku].Class;
                yield return new Violation(
                    ViolationType.ClassPositionInvalid,
                    cell,
                    $"Produk {cell.Sku} ({cellClass}) berada di atas produk {below.Sku} ({belowClass}) - kelas tidak sesuai");
            }
        }
    }
}
