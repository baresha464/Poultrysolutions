using System.Globalization;
using System.Text.RegularExpressions;
using AmrPoultryFarmWeb.Models;
using Microsoft.Extensions.Localization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AmrPoultryFarmWeb.Services.Reports.Pdf;

/// <summary>Report text in the reader's language: an English composite-format key, translated when a
/// localizer is supplied (numbers always formatted en-IN). Without one it's plain English.</summary>
public sealed class PdfText
{
    private static readonly CultureInfo Format = CultureInfo.GetCultureInfo("en-IN");
    public IStringLocalizer? Localizer { get; }
    public PdfText(IStringLocalizer? localizer = null) => Localizer = localizer;
    public string this[string key, params object[] args] =>
        Localizer is null ? (args.Length == 0 ? key : string.Format(Format, key, args)) : Localizer[key, args].Value;
    public static readonly PdfText English = new();
}

/// <summary>Tenant identity stamped on every report page.</summary>
public class ReportBranding
{
    public PdfText Text { get; init; } = PdfText.English;
    public string FarmName { get; init; } = "Poultry Farm";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string PreparedBy { get; init; } = "";
    public string Primary { get; init; } = PdfKit.DefaultPrimary;
    public string Accent { get; init; } = PdfKit.DefaultAccent;
    public byte[]? LogoPng { get; init; }
    public string? LogoSvg { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.Now;

    public static ReportBranding From(Tenant? tenant, string title, string subtitle, string? preparedBy, PdfText? text = null)
    {
        byte[]? png = null;
        string? svg = null;
        if (tenant?.LogoBytes is { Length: > 0 } bytes)
        {
            if (tenant.LogoContentType?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true)
            {
                svg = System.Text.Encoding.UTF8.GetString(bytes);
            }
            else
            {
                // Validate up front — a corrupt/unsupported upload must not break the whole report.
                try { Image.FromBinaryData(bytes); png = bytes; } catch { png = null; }
            }
        }

        return new ReportBranding
        {
            Text = text ?? PdfText.English,
            FarmName = string.IsNullOrWhiteSpace(tenant?.Name) ? "Poultry Farm" : tenant!.Name,
            Title = title,
            Subtitle = subtitle,
            PreparedBy = preparedBy ?? "",
            Primary = PdfKit.SafeHex(tenant?.PrimaryColorHex, PdfKit.DefaultPrimary),
            Accent = PdfKit.SafeHex(tenant?.AccentColorHex, PdfKit.DefaultAccent),
            LogoPng = png,
            LogoSvg = svg,
        };
    }
}

/// <summary>Shared look &amp; feel for all PDF reports: page frame, section titles, KPI tiles,
/// tables and number formatting.</summary>
public static class PdfKit
{
    public const string DefaultPrimary = "#0284c7";
    public const string DefaultAccent = "#e8a33d";
    public const string Ink = "#0f1b24";
    public const string InkSoft = "#51697a";
    public const string Line = "#dbe8f0";
    public const string Zebra = "#f5f9fc";
    public const string Good = "#15803d";
    public const string GoodSoft = "#dcfce7";
    public const string Warn = "#b45309";
    public const string WarnSoft = "#fef3c7";
    public const string Bad = "#ba1a1a";
    public const string BadSoft = "#fee2e2";

    private static readonly CultureInfo IN = CultureInfo.GetCultureInfo("en-IN");
    public const string TeluguFont = "Noto Sans Telugu";

    /// <summary>BroilIQ mark for the report footer; loaded once at startup (Program.cs). Null = text only.</summary>
    public static string? PlatformMarkSvg { get; set; }

    public static string SafeHex(string? hex, string fallback) =>
        hex is not null && Regex.IsMatch(hex, "^#[0-9a-fA-F]{6}$") ? hex : fallback;

    // ---------------- Formatting ----------------
    public static string Rs(decimal v) => "Rs " + Math.Round(v, 0).ToString("N0", IN);
    public static string Rs(decimal? v) => v is { } x ? Rs(x) : "-";
    public static string Rs2(decimal v) => "Rs " + v.ToString("N2", IN);
    public static string Rs2(decimal? v) => v is { } x ? Rs2(x) : "-";
    public static string N0(decimal v) => v.ToString("N0", IN);
    public static string N0(int v) => v.ToString("N0", IN);
    public static string Pct(decimal v, int dp = 1) => v.ToString("F" + dp) + "%";
    public static string Opt(decimal? v, string fmt, string suffix = "") => v is { } x ? x.ToString(fmt) + suffix : "-";
    public static string D(DateTime d) => d.ToString("dd MMM yyyy");

    // ---------------- Document shell ----------------
    public static byte[] Build(ReportBranding brand, Action<ColumnDescriptor> body, bool landscape = false)
    {
        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
                page.Margin(28);
                page.PageColor(Colors.White);
                // Telugu glyphs fall back to the bundled Noto Sans Telugu (registered in Program.cs).
                page.DefaultTextStyle(t => t.FontSize(8.5f).FontColor(Ink).FontFamily("Lato", TeluguFont));

                page.Header().Element(c => Header(c, brand));
                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Spacing(12);
                    body(col);
                });
                page.Footer().Element(c => Footer(c, brand));
            });
        })
        .WithMetadata(new DocumentMetadata
        {
            Title = $"{brand.Title} - {brand.Subtitle}",
            Author = brand.FarmName,
            Creator = brand.FarmName,
            CreationDate = brand.GeneratedAt,
        })
        .GeneratePdf();
    }

    private static void Header(IContainer c, ReportBranding b)
    {
        c.Column(col =>
        {
            col.Item().Row(row =>
            {
                if (b.LogoPng is not null)
                    row.ConstantItem(42).Height(42).AlignMiddle().Image(b.LogoPng).FitArea();
                else if (b.LogoSvg is not null)
                    row.ConstantItem(42).Height(42).AlignMiddle().Svg(b.LogoSvg).FitArea();
                if (b.LogoPng is not null || b.LogoSvg is not null) row.ConstantItem(10);

                row.RelativeItem().AlignMiddle().Column(t =>
                {
                    t.Item().Text(b.FarmName.ToUpperInvariant()).FontSize(8).Bold().FontColor(b.Primary).LetterSpacing(0.08f);
                    t.Item().Text(b.Title).FontSize(16).Bold();
                    t.Item().Text(b.Subtitle).FontSize(9).FontColor(InkSoft);
                });
                row.ConstantItem(150).AlignRight().AlignMiddle().Column(t =>
                {
                    t.Item().AlignRight().Text(b.Text["Generated {0:dd MMM yyyy, HH:mm}", b.GeneratedAt]).FontSize(7.5f).FontColor(InkSoft);
                    if (!string.IsNullOrWhiteSpace(b.PreparedBy))
                        t.Item().AlignRight().Text(b.Text["By {0}", b.PreparedBy]).FontSize(7.5f).FontColor(InkSoft);
                });
            });
            col.Item().PaddingTop(8).LineHorizontal(2).LineColor(b.Primary);
        });
    }

    private static void Footer(IContainer c, ReportBranding b)
    {
        c.Column(col =>
        {
            col.Item().LineHorizontal(0.5f).LineColor(Line);
            col.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem().AlignMiddle().Text($"{b.FarmName} · {b.Title}").FontSize(7).FontColor(InkSoft);

                // Platform credit: the client's branding owns the page, BroilIQ signs the footer.
                row.AutoItem().AlignMiddle().Row(brand =>
                {
                    if (PlatformMarkSvg is { } mark)
                        brand.ConstantItem(11).Height(11).AlignMiddle().Svg(mark).FitArea();
                    brand.AutoItem().AlignMiddle().PaddingLeft(3).Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(7).FontColor(InkSoft));
                        // Word order differs by language ("Powered by X" / "X సహకారంతో"), so split the
                        // translated sentence around the name to bold just the name.
                        var parts = b.Text["Powered by {0}", "\u0001"].Split('\u0001');
                        t.Span(parts[0]);
                        t.Span(AppBrand.Name).Bold().FontColor("#0369A1");
                        if (parts.Length > 1) t.Span(parts[1]);
                    });
                });

                row.RelativeItem().AlignMiddle().AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7).FontColor(InkSoft));
                    t.Span(b.Text["Page"] + " ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        });
    }

    // ---------------- Building blocks ----------------
    public static void Section(ColumnDescriptor col, string title, string? sub = null, string? color = null)
    {
        col.Item().PaddingTop(4).Column(c =>
        {
            c.Item().Row(r =>
            {
                r.ConstantItem(4).Height(14).Background(color ?? DefaultPrimary);
                r.ConstantItem(6);
                r.RelativeItem().AlignMiddle().Text(title).FontSize(11.5f).Bold();
            });
            if (sub is not null) c.Item().PaddingLeft(10).PaddingTop(1).Text(sub).FontSize(7.5f).FontColor(InkSoft);
        });
    }

    /// <summary>A titled section whose heading never gets stranded at the bottom of a page: the
    /// heading moves with the start of its content when less than <paramref name="keepWith"/> points remain.</summary>
    public static void Block(ColumnDescriptor col, string title, string? sub, string color, Action<ColumnDescriptor> content, float keepWith = 130)
    {
        col.Item().EnsureSpace(keepWith).Column(c =>
        {
            c.Spacing(6);
            Section(c, title, sub, color);
            content(c);
        });
    }

    /// <summary>Chart legend drawn as PDF text (properly shaped for Telugu), for use above an SVG chart
    /// rendered with <c>drawLegend: false</c>.</summary>
    public static void ChartLegend(ColumnDescriptor col, IEnumerable<SvgCharts.Series> series)
    {
        col.Item().PaddingLeft(40).Row(r =>
        {
            r.Spacing(14);
            foreach (var s in series)
                r.AutoItem().Row(item =>
                {
                    item.AutoItem().AlignMiddle().Width(16).Height(s.Dashed ? 1.4f : 2.2f).Background(s.Color);
                    item.AutoItem().PaddingLeft(4).Text(s.Name).FontSize(8);
                });
        });
    }

    public record Kpi(string Label, string Value, string? Note = null, string? Tone = null);

    public static void KpiRow(ColumnDescriptor col, IEnumerable<Kpi> kpis, string accent)
    {
        col.Item().Row(row =>
        {
            row.Spacing(6);
            foreach (var k in kpis)
            {
                row.RelativeItem().Border(0.6f).BorderColor(Line).Background(Zebra).Padding(7).Column(c =>
                {
                    c.Item().Text(k.Label.ToUpperInvariant()).FontSize(6.5f).Bold().FontColor(InkSoft).LetterSpacing(0.04f);
                    c.Item().PaddingTop(2).Text(k.Value).FontSize(13).Bold().FontColor(k.Tone ?? Ink);
                    if (k.Note is not null) c.Item().Text(k.Note).FontSize(6.5f).FontColor(InkSoft);
                });
            }
        });
    }

    public static IContainer HeadCell(IContainer c, string bg) =>
        c.Background(bg).PaddingVertical(4).PaddingHorizontal(4).DefaultTextStyle(t => t.FontSize(7).Bold().FontColor(Colors.White));

    public static IContainer BodyCell(IContainer c, int rowIndex) =>
        c.Background(rowIndex % 2 == 1 ? Zebra : Colors.White).BorderBottom(0.4f).BorderColor(Line)
         .PaddingVertical(3).PaddingHorizontal(4).DefaultTextStyle(t => t.FontSize(7.5f));

    /// <summary>A simple data table: headers, rows of strings, optional per-cell colour.</summary>
    public static void Table(ColumnDescriptor col, string headBg, string[] headers, float[] widths,
        IEnumerable<string[]> rows, Func<int, int, string?>? cellColor = null, int[]? rightAligned = null, Func<int, bool>? boldRow = null)
    {
        var right = new HashSet<int>(rightAligned ?? Enumerable.Range(1, headers.Length - 1));
        var rowList = rows.ToList();
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(cd => { foreach (var w in widths) cd.RelativeColumn(w); });
            t.Header(h =>
            {
                for (int i = 0; i < headers.Length; i++)
                {
                    var cell = h.Cell().Element(c => HeadCell(c, headBg));
                    (right.Contains(i) ? cell.AlignRight() : cell).Text(headers[i]);
                }
            });
            for (int r = 0; r < rowList.Count; r++)
            {
                for (int i = 0; i < headers.Length; i++)
                {
                    var rr = r;
                    var cell = t.Cell().Element(c => BodyCell(c, rr));
                    var text = (right.Contains(i) ? cell.AlignRight() : cell).Text(i < rowList[r].Length ? rowList[r][i] : "");
                    if (cellColor?.Invoke(r, i) is { } color) text.FontColor(color).Bold();
                    if (boldRow?.Invoke(r) == true) text.Bold();
                }
            }
        });
    }

    public static void Callout(ColumnDescriptor col, string title, string body, string tone, string toneSoft)
    {
        col.Item().ShowEntire().BorderLeft(3).BorderColor(tone).Background(toneSoft).Padding(8).Column(c =>
        {
            c.Item().Text(title).Bold().FontColor(tone);
            c.Item().PaddingTop(2).Text(body).FontSize(8);
        });
    }

    public static void Bullets(ColumnDescriptor col, IEnumerable<string> items, float size = 8)
    {
        foreach (var i in items)
            col.Item().Row(r =>
            {
                r.ConstantItem(10).Text("•").FontSize(size);
                r.RelativeItem().Text(i).FontSize(size);
            });
    }

    public static void Note(ColumnDescriptor col, string text) =>
        col.Item().Text(text).FontSize(6.8f).Italic().FontColor(InkSoft);

    public static void Signatures(ColumnDescriptor col, params string[] roles)
    {
        col.Item().PaddingTop(24).ShowEntire().Row(r =>
        {
            r.Spacing(24);
            foreach (var role in roles)
                r.RelativeItem().Column(c =>
                {
                    c.Item().Height(26);
                    c.Item().LineHorizontal(0.6f).LineColor(InkSoft);
                    c.Item().PaddingTop(2).Text(role).FontSize(7.5f).FontColor(InkSoft);
                });
        });
    }

    /// <summary>Tone for "higher is better" (or lower) against a benchmark, with a tolerance band.</summary>
    public static string ToneVs(decimal actual, decimal target, bool higherIsBetter, decimal tolerancePct = 3)
    {
        if (actual == 0 || target == 0) return Ink;
        var diffPct = (actual - target) / target * 100 * (higherIsBetter ? 1 : -1);
        return diffPct >= 0 ? Good : diffPct >= -tolerancePct ? Warn : Bad;
    }
}
