using System.Globalization;
using System.Net;
using System.Text;
using StackInspection.Application.Contracts;
using StackInspection.Domain;

namespace StackInspection.Api.Rendering;

/// <summary>
/// Halaman HTML mandiri (tanpa file/library eksternal) yang mensimulasikan render susunan dus dari hasil Collect
/// SKU (dan Analyze bila ada): grid tampilan seperti di UI, posisi dus di foto beserta garis tumpuan, ringkasan
/// skor, pelanggaran, dan jalur tumpukan. Sel dengan <c>id</c> sama digabung menjadi satu kotak.
/// </summary>
public static class StackLayoutHtmlRenderer
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private sealed record Tile(int Id, int Row, int Column, int RowSpan, int ColumnSpan, CellDto Cell);

    /// <summary>Membuat halaman HTML; <paramref name="analyze"/> <c>null</c> = tanpa skor dan pelanggaran.</summary>
    public static string Render(CollectSkuResponse collect, AnalyzeStackResponse? analyze)
    {
        ArgumentNullException.ThrowIfNull(collect);
        List<Tile> tiles = [.. collect.Cells
            .GroupBy(c => c.Id ?? -1)
            .Select(g => new Tile(
                g.Key,
                g.Min(c => c.Row),
                g.Min(c => c.Column),
                g.Max(c => c.Row) - g.Min(c => c.Row) + 1,
                g.Max(c => c.Column) - g.Min(c => c.Column) + 1,
                g.First()))
            .OrderBy(t => t.Id)];
        Dictionary<string, string> palette = Palette(tiles.Select(t => t.Cell.Sku));
        Dictionary<int, List<ViolationDto>> violations = analyze?.Violations
            .GroupBy(v => v.CartonId)
            .ToDictionary(g => g.Key, g => g.ToList()) ?? [];

        StringBuilder html = new();
        html.Append("<!doctype html><html lang=\"id\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>Simulasi Susunan Dus</title><style>").Append(Css).Append("</style></head><body><main>");
        AppendSummary(html, collect, analyze, tiles.Count);
        html.Append("<div class=\"panels\">");
        AppendGrid(html, collect, tiles, violations, palette);
        AppendPhoto(html, collect, tiles, violations, palette);
        html.Append("</div>");
        AppendLegend(html, tiles, analyze, palette);
        if (analyze is not null)
        {
            AppendViolations(html, analyze);
        }

        AppendStacks(html, collect);
        html.Append("</main><script>").Append(Script).Append("</script></body></html>");
        return html.ToString();
    }

    private static void AppendSummary(StringBuilder html, CollectSkuResponse collect, AnalyzeStackResponse? analyze, int cartons)
    {
        html.Append("<header><div><h1>Simulasi Susunan Dus</h1><p class=\"muted\">Inspeksi ")
            .Append(E(collect.InspectionId.ToString())).Append(" · model ").Append(E(collect.Model))
            .Append(" · foto ").Append(collect.ImageWidth).Append('×').Append(collect.ImageHeight).Append(" px</p></div>");
        if (analyze is not null)
        {
            html.Append("<div class=\"score\"><span class=\"score-value\">")
                .Append(analyze.PercentageScore.ToString("0.##", Invariant)).Append("%</span><span class=\"muted\">")
                .Append(analyze.TotalScore).Append(" dari ").Append(analyze.MaxPossibleScore).Append(" dus tanpa pelanggaran</span></div>");
        }

        html.Append("</header><dl class=\"stats\">");
        Stat(html, "Dus", cartons.ToString(Invariant));
        Stat(html, "Grid", $"{collect.RowCount} × {collect.ColumnCount}");
        Stat(html, "Jalur tumpukan", collect.Stacks.Count.ToString(Invariant));
        Stat(html, "Lapisan belakang dibuang", collect.ExcludedBackLayerCount.ToString(Invariant));
        if (analyze is not null)
        {
            Stat(html, "Pelanggaran", analyze.Violations.Count.ToString(Invariant));
        }

        Stat(html, "Waktu proses", $"{collect.ProcessingTimeMs.ToString("N0", Invariant)} ms");
        html.Append("</dl>");
        if (collect.Warnings.Count > 0)
        {
            html.Append("<div class=\"chips\">");
            foreach (string warning in collect.Warnings)
            {
                html.Append("<span class=\"chip\">").Append(E(warning)).Append("</span>");
            }

            html.Append("</div>");
        }

        if (analyze is null)
        {
            html.Append("<p class=\"muted\">masterSkus tidak diisi: skor dan pelanggaran tidak dihitung.</p>");
        }
    }

    private static void Stat(StringBuilder html, string label, string value) =>
        html.Append("<div><dt>").Append(E(label)).Append("</dt><dd>").Append(E(value)).Append("</dd></div>");

    private static void AppendGrid(
        StringBuilder html,
        CollectSkuResponse collect,
        List<Tile> tiles,
        Dictionary<int, List<ViolationDto>> violations,
        Dictionary<string, string> palette)
    {
        // proporsi grid = proporsi area dus di foto, agar bentuknya sebanding dengan panel foto
        double width = collect.Cells.Count == 0 ? 1 : collect.Cells.Max(c => c.BoundingBox.X2) - collect.Cells.Min(c => c.BoundingBox.X1);
        double height = collect.Cells.Count == 0 ? 1 : collect.Cells.Max(c => c.BoundingBox.Y2) - collect.Cells.Min(c => c.BoundingBox.Y1);
        html.Append("<section class=\"panel\"><h2>Render grid (seperti UI)</h2><div class=\"grid\" style=\"")
            .Append("--ratio:").Append(N(Math.Max(1, width))).Append(" / ").Append(N(Math.Max(1, height))).Append(';')
            .Append("grid-template-columns:repeat(").Append(Math.Max(1, collect.ColumnCount)).Append(",minmax(0,1fr));")
            .Append("grid-template-rows:repeat(").Append(Math.Max(1, collect.RowCount)).Append(",minmax(14px,1fr));\">");
        foreach (Tile tile in tiles)
        {
            CellDto c = tile.Cell;
            bool violating = violations.ContainsKey(tile.Id);
            html.Append("<div class=\"tile").Append(violating ? " violation" : string.Empty)
                .Append(IsUnread(c) ? " unread" : string.Empty).Append("\" data-id=\"").Append(tile.Id)
                .Append("\" style=\"grid-column:").Append(tile.Column + 1).Append(" / span ").Append(tile.ColumnSpan)
                .Append(";grid-row:").Append(tile.Row + 1).Append(" / span ").Append(tile.RowSpan)
                .Append(";--sku:").Append(palette[c.Sku]).Append("\" title=\"").Append(E(Tooltip(tile, violations))).Append("\">")
                .Append("<span class=\"sku\">").Append(E(ShortSku(c.Sku))).Append("</span>")
                .Append("<span class=\"meta\">#").Append(tile.Id).Append(" · L").Append(c.Level).Append("</span>")
                .Append(ConfidenceLabel(c) is { Length: > 0 } confidence ? $"<span class=\"conf\">{E(confidence)}</span>" : string.Empty)
                .Append(violating ? "<span class=\"flag\" aria-label=\"melanggar\">!</span>" : string.Empty)
                .Append("</div>");
        }

        html.Append("</div><p class=\"muted\">Kotak = satu dus (id sama digabung). L = level fisik (0 = lantai). Persen = keyakinan OCR; ✎ = SKU dikoreksi; ≈ = ditebak dari kemiripan tampilan. Arahkan kursor/ketuk untuk detail.</p></section>");
    }

    private static void AppendPhoto(
        StringBuilder html,
        CollectSkuResponse collect,
        List<Tile> tiles,
        Dictionary<int, List<ViolationDto>> violations,
        Dictionary<string, string> palette)
    {
        Dictionary<int, CellDto> byId = tiles.ToDictionary(t => t.Id, t => t.Cell);
        html.Append("<section class=\"panel\"><h2>Posisi di foto</h2><svg class=\"photo\" viewBox=\"0 0 ")
            .Append(collect.ImageWidth).Append(' ').Append(collect.ImageHeight)
            .Append("\" preserveAspectRatio=\"xMidYMid meet\" role=\"img\" aria-label=\"Posisi dus di foto\">")
            .Append("<rect class=\"frame\" x=\"0\" y=\"0\" width=\"").Append(collect.ImageWidth).Append("\" height=\"")
            .Append(collect.ImageHeight).Append("\"/>");
        double stroke = Math.Max(collect.ImageWidth, collect.ImageHeight) / 300.0;
        foreach (Tile tile in tiles)
        {
            BoundingBoxDto b = tile.Cell.BoundingBox;
            foreach (int below in tile.Cell.RestsOn ?? [])
            {
                if (byId.TryGetValue(below, out CellDto? support))
                {
                    BoundingBoxDto s = support.BoundingBox;
                    html.Append("<line class=\"support\" x1=\"").Append(N((b.X1 + b.X2) / 2.0)).Append("\" y1=\"").Append(N(b.Y2))
                        .Append("\" x2=\"").Append(N((s.X1 + s.X2) / 2.0)).Append("\" y2=\"").Append(N(s.Y1))
                        .Append("\" stroke-width=\"").Append(N(stroke)).Append("\"/>");
                }
            }
        }

        foreach (Tile tile in tiles)
        {
            BoundingBoxDto b = tile.Cell.BoundingBox;
            html.Append("<rect class=\"box").Append(violations.ContainsKey(tile.Id) ? " violation" : string.Empty)
                .Append(IsUnread(tile.Cell) ? " unread" : string.Empty).Append("\" data-id=\"").Append(tile.Id)
                .Append("\" x=\"").Append(b.X1).Append("\" y=\"").Append(b.Y1).Append("\" width=\"").Append(b.X2 - b.X1)
                .Append("\" height=\"").Append(b.Y2 - b.Y1).Append("\" style=\"--sku:").Append(palette[tile.Cell.Sku])
                .Append("\" stroke-width=\"").Append(N(stroke)).Append("\"><title>").Append(E(Tooltip(tile, violations)))
                .Append("</title></rect>");
        }

        html.Append("</svg><p class=\"muted\">Koordinat kotak asli di foto; garis = dus menumpu dus di bawahnya (restsOn).</p></section>");
    }

    private static void AppendLegend(
        StringBuilder html, List<Tile> tiles, AnalyzeStackResponse? analyze, Dictionary<string, string> palette)
    {
        Dictionary<string, SkuSummaryDto> summary = analyze?.SkuSummary.ToDictionary(s => s.Sku, StringComparer.Ordinal) ?? [];
        html.Append("<section class=\"panel wide\"><h2>SKU</h2><ul class=\"legend\">");
        foreach (IGrouping<string, Tile> group in tiles.GroupBy(t => t.Cell.Sku, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            SkuSummaryDto? info = summary.GetValueOrDefault(group.Key);
            html.Append("<li><span class=\"swatch\" style=\"--sku:").Append(palette[group.Key]).Append("\"></span><b>")
                .Append(E(group.Key)).Append("</b>");
            if (info?.Name is not null)
            {
                html.Append(' ').Append(E(info.Name)).Append(" · kelas ").Append(E(info.Class ?? "-"));
            }

            html.Append(" · ").Append(group.Count()).Append(" dus");
            if (info is { ViolationCount: > 0 })
            {
                html.Append(" · <span class=\"bad\">").Append(info.ViolationCount).Append(" melanggar</span>");
            }

            html.Append("</li>");
        }

        html.Append("</ul></section>");
    }

    private static void AppendViolations(StringBuilder html, AnalyzeStackResponse analyze)
    {
        html.Append("<section class=\"panel wide\"><h2>Pelanggaran</h2>");
        if (analyze.Violations.Count == 0)
        {
            html.Append("<p>Tidak ada pelanggaran.</p></section>");
            return;
        }

        html.Append("<div class=\"table\"><table><thead><tr><th>Dus</th><th>Jenis</th><th>SKU</th><th>Level</th><th>Keterangan</th></tr></thead><tbody>");
        foreach (ViolationDto v in analyze.Violations)
        {
            html.Append("<tr data-id=\"").Append(v.CartonId).Append("\"><td>#").Append(v.CartonId).Append("</td><td>")
                .Append(E(v.Type)).Append("</td><td>").Append(E(v.Sku)).Append("</td><td>").Append(v.Level)
                .Append("</td><td>").Append(E(v.Message)).Append("</td></tr>");
        }

        html.Append("</tbody></table></div></section>");
    }

    private static void AppendStacks(StringBuilder html, CollectSkuResponse collect)
    {
        html.Append("<section class=\"panel wide\"><h2>Jalur tumpukan (bawah → atas)</h2><ol class=\"paths\">");
        foreach (StackDto stack in collect.Stacks)
        {
            html.Append("<li data-path=\"").Append(string.Join(' ', stack.Cartons.Select(c => c.Id!.Value))).Append("\">")
                .Append(string.Join(" → ", stack.Cartons.Select(c => "#" + c.Id!.Value.ToString(Invariant))))
                .Append(" <span class=\"muted\">(").Append(stack.Height).Append(" dus)</span></li>");
        }

        html.Append("</ol></section>");
    }

    private static string Tooltip(Tile tile, Dictionary<int, List<ViolationDto>> violations)
    {
        CellDto c = tile.Cell;
        StringBuilder text = new();
        text.Append('#').Append(tile.Id).Append(" · SKU ").Append(c.Sku).Append(" (").Append(c.ReadStatus).Append(')')
            .Append("\nOCR: ").Append(c.OcrText ?? "-")
            .Append(c.ReadStatus switch
            {
                ReadStatus.InferredByAppearance => $"\nKemiripan tampilan {Percent(c.OcrConfidence)} (bukan hasil baca label)",
                ReadStatus.LabelNotVisible => string.Empty,
                _ => $"\nKeyakinan OCR {Percent(c.OcrConfidence)}",
            })
            .Append("\nLevel ").Append(c.Level).Append(" · menumpu: ")
            .Append(c.RestsOn is { Count: > 0 } r ? string.Join(", ", r.Select(i => "#" + i.ToString(Invariant))) : "lantai");
        foreach (ViolationDto v in violations.GetValueOrDefault(tile.Id) ?? [])
        {
            text.Append("\n⚠ ").Append(v.Message);
        }

        return text.ToString();
    }

    /// <summary>Label keyakinan di tile: "87%", "✎ 87%" (dikoreksi), "≈91%" (kemiripan tampilan), kosong jika tidak terbaca.</summary>
    private static string ConfidenceLabel(CellDto cell) => cell.ReadStatus switch
    {
        ReadStatus.LabelNotVisible => string.Empty,
        ReadStatus.InferredByAppearance => "≈" + Percent(cell.OcrConfidence),
        ReadStatus.Corrected => "✎ " + Percent(cell.OcrConfidence),
        _ => Percent(cell.OcrConfidence),
    };

    private static string Percent(double value) => string.Create(Invariant, $"{Math.Clamp(value, 0, 1) * 100:0}%");

    private static bool IsUnread(CellDto cell) => cell.ReadStatus is ReadStatus.Unknown or ReadStatus.LabelNotVisible;

    private static string ShortSku(string sku) => sku switch
    {
        SkuCodes.Unknown => "?",
        SkuCodes.LabelNotVisible => "label?",
        _ => sku,
    };

    /// <summary>
    /// Warna per SKU: SKU yang muncul diurutkan lalu hue disebar dengan sudut emas (137,5°) agar SKU berbeda mudah
    /// dibedakan; SKU tidak terbaca abu-abu.
    /// </summary>
    private static Dictionary<string, string> Palette(IEnumerable<string> skus)
    {
        Dictionary<string, string> palette = new(StringComparer.Ordinal);
        int index = 0;
        foreach (string sku in skus.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            palette[sku] = sku is SkuCodes.Unknown or SkuCodes.LabelNotVisible
                ? "hsl(0 0% 72%)"
                : string.Create(Invariant, $"hsl({((index++ * 137.508) + 25) % 360:0} 62% 62%)");
        }

        return palette;
    }

    private static string N(double value) => value.ToString("0.#", Invariant);

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private const string Css = """
        :root{--bg:#f6f7f9;--panel:#fff;--text:#1d2330;--muted:#5d6678;--line:#d9dde5;--bad:#d92d20;--support:#7a5a00}
        @media (prefers-color-scheme:dark){:root{--bg:#12151b;--panel:#1b2029;--text:#e7eaf0;--muted:#9aa3b5;--line:#2c3340;--bad:#ff6b5e;--support:#ffd166}}
        *{box-sizing:border-box}
        body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif}
        main{max-width:1400px;margin:0 auto;padding:16px}
        header{display:flex;flex-wrap:wrap;justify-content:space-between;gap:12px;align-items:flex-end}
        h1{font-size:22px;margin:0}h2{font-size:16px;margin:0 0 10px}
        .muted{color:var(--muted);margin:4px 0}
        .score{display:flex;flex-direction:column;align-items:flex-end}
        .score-value{font-size:32px;font-weight:700}
        .stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:8px;margin:14px 0}
        .stats div{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:8px 10px}
        dt{color:var(--muted);font-size:12px}dd{margin:0;font-weight:600;font-size:16px}
        .chips{display:flex;flex-wrap:wrap;gap:6px;margin-bottom:12px}
        .chip{border:1px solid var(--line);background:var(--panel);border-radius:999px;padding:2px 10px;font-size:12px}
        .panels{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:16px}
        @media (max-width:900px){.panels{grid-template-columns:minmax(0,1fr)}}
        .panel{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px;min-width:0}
        .wide{margin-top:16px}
        .grid{display:grid;gap:2px;aspect-ratio:var(--ratio,auto);min-height:240px}
        .tile{position:relative;background:var(--sku);border:1px solid rgba(0,0,0,.35);border-radius:4px;color:#111;overflow:hidden;
          display:flex;flex-direction:column;justify-content:center;align-items:center;line-height:1.1;cursor:default;min-height:0;
          container-type:size;font-size:11px}
        .tile .sku{font-weight:700;white-space:nowrap;font-size:clamp(7px,16cqw,13px)}
        .tile .meta{opacity:.75;white-space:nowrap;font-size:clamp(6px,12cqw,10px)}
        .tile .conf{opacity:.85;white-space:nowrap;font-size:clamp(6px,12cqw,10px);font-weight:600}
        .tile.unread{background:repeating-linear-gradient(45deg,#bbb 0 6px,#ddd 6px 12px)}
        .tile.violation{outline:3px solid var(--bad);outline-offset:-3px}
        .flag{position:absolute;top:2px;right:4px;color:var(--bad);font-weight:800}
        .photo{width:100%;height:auto;max-height:80vh;display:block}
        .frame{fill:none;stroke:var(--line);stroke-width:4}
        .box{fill:var(--sku);fill-opacity:.55;stroke:#222}
        .box.unread{fill:#bbb}.box.violation{stroke:var(--bad);stroke-width:6}
        .support{stroke:var(--support);stroke-linecap:round}
        .hl{filter:brightness(1.15);outline:3px solid #0a84ff !important;outline-offset:-3px}
        rect.hl{stroke:#0a84ff;stroke-width:10;fill-opacity:.85}
        .legend{list-style:none;margin:0;padding:0;display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:6px}
        .swatch{display:inline-block;width:14px;height:14px;border-radius:3px;background:var(--sku);vertical-align:-2px;margin-right:6px;border:1px solid rgba(0,0,0,.3)}
        .bad{color:var(--bad);font-weight:600}
        .table{overflow-x:auto}table{border-collapse:collapse;width:100%;font-size:13px}
        th,td{border-bottom:1px solid var(--line);padding:6px 8px;text-align:left;vertical-align:top}
        tr[data-id]{cursor:pointer}
        .paths{margin:0;padding-left:22px;columns:2 320px}.paths li{break-inside:avoid;cursor:pointer}
        """;

    /// <summary>Menyorot dus yang sama di grid, foto, dan tabel saat kursor/ketukan berada di salah satunya.</summary>
    private const string Script = """
        (function(){
          function mark(ids,on){ids.forEach(function(id){document.querySelectorAll('[data-id="'+id+'"]').forEach(function(el){el.classList.toggle('hl',on);});});}
          function ids(el){return el.dataset.path?el.dataset.path.split(' '):[el.dataset.id];}
          document.querySelectorAll('[data-id],[data-path]').forEach(function(el){
            el.addEventListener('mouseenter',function(){mark(ids(el),true);});
            el.addEventListener('mouseleave',function(){mark(ids(el),false);});
            el.addEventListener('click',function(){var on=!el.classList.contains('pinned');document.querySelectorAll('.pinned').forEach(function(p){p.classList.remove('pinned');});document.querySelectorAll('.hl').forEach(function(h){h.classList.remove('hl');});if(on){el.classList.add('pinned');mark(ids(el),true);}});
          });
        })();
        """;
}
