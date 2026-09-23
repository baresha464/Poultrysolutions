using System.Globalization;
using System.Net;
using System.Text;

namespace AmrPoultryFarmWeb.Services.Reports.Pdf;

/// <summary>
/// Tiny dependency-free SVG chart builders for the PDF reports (QuestPDF renders SVG natively and
/// keeps it vector-sharp when printed). Deliberately minimal: line vs benchmark, and bars with an
/// optional target line — the two shapes the reports need.
/// </summary>
public static class SvgCharts
{
    private const string Font = "font-family=\"Lato, Arial, sans-serif\"";
    // The SVG renderer doesn't fall back per glyph, so text containing Telugu must name the Telugu
    // font itself (it also carries Latin digits/letters for mixed labels).
    private static string FontFor(string text) =>
        text.Any(ch => ch is >= 'ఀ' and <= '౿') ? "font-family=\"Noto Sans Telugu\"" : Font;
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Esc(string s) => WebUtility.HtmlEncode(s);

    public record Series(string Name, string Color, IReadOnlyList<double?> Values, bool Dashed = false);

    /// <summary>Multi-series line chart over shared x labels.</summary>
    public static string Lines(IReadOnlyList<string> labels, IReadOnlyList<Series> series, int width = 520, int height = 190, string yFormat = "0", bool drawLegend = true)
    {
        const int padL = 40, padR = 10, padT = 22, padB = 22;
        var all = series.SelectMany(s => s.Values).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (all.Count == 0 || labels.Count == 0) return Empty(width, height);
        double max = NiceMax(all.Max()), min = 0;
        double plotW = width - padL - padR, plotH = height - padT - padB;
        double X(int i) => padL + (labels.Count == 1 ? plotW / 2 : plotW * i / (labels.Count - 1));
        double Y(double v) => padT + plotH - (v - min) / (max - min) * plotH;

        var sb = Start(width, height);
        Grid(sb, padL, padT, plotW, plotH, min, max, yFormat);
        int step = Math.Max(1, (int)Math.Ceiling(labels.Count / 14.0));
        for (int i = 0; i < labels.Count; i += step)
            sb.Append($"<text x=\"{F(X(i))}\" y=\"{height - 6}\" font-size=\"8\" fill=\"#51697a\" text-anchor=\"middle\" {Font}>{Esc(labels[i])}</text>");

        foreach (var s in series)
        {
            var path = new StringBuilder();
            bool pen = false;
            for (int i = 0; i < s.Values.Count && i < labels.Count; i++)
            {
                if (s.Values[i] is not { } v) { pen = false; continue; }
                path.Append(pen ? " L" : " M").Append(F(X(i))).Append(' ').Append(F(Y(v)));
                pen = true;
            }
            var dash = s.Dashed ? " stroke-dasharray=\"5 4\"" : "";
            sb.Append($"<path d=\"{path}\" fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"{(s.Dashed ? 1.4 : 2.2)}\"{dash} stroke-linejoin=\"round\"/>");
            if (!s.Dashed)
                for (int i = 0; i < s.Values.Count && i < labels.Count; i++)
                    if (s.Values[i] is { } v)
                        sb.Append($"<circle cx=\"{F(X(i))}\" cy=\"{F(Y(v))}\" r=\"2.2\" fill=\"{s.Color}\"/>");
        }
        // Callers with non-Latin series names draw the legend as PDF text instead (see PdfKit.ChartLegend):
        // the SVG renderer can't shape Telugu conjuncts.
        if (drawLegend) Legend(sb, series.Select(s => (s.Name, s.Color, s.Dashed)).ToList(), padL);
        return sb.Append("</svg>").ToString();
    }

    /// <summary>Vertical bars, each with its own colour, plus an optional horizontal target line.</summary>
    public static string Bars(IReadOnlyList<string> labels, IReadOnlyList<double> values, IReadOnlyList<string> colors,
        double? target = null, string? targetLabel = null, int width = 520, int height = 180, string valueFormat = "0", double? minY = null)
    {
        const int padL = 40, padR = 10, padT = 22;
        if (values.Count == 0) return Empty(width, height);
        double slot0 = (width - padL - padR) / (double)values.Count;
        // Many bars (e.g. 40 days of mortality): label every nth bar and only print values on bars
        // that stand out by colour. Few bars with long names (batch codes): tilt the labels instead.
        bool dense = values.Count > 20;
        int labelStep = dense ? (int)Math.Ceiling(values.Count / 14.0) : 1;
        string baseColor = colors.GroupBy(c => c).MaxBy(g => g.Count())!.Key;
        bool tilt = !dense && labels.Any(l => l.Length * 4.3 > slot0 - 4);
        int padB = tilt ? 50 : 30;
        double max = NiceMax(Math.Max(values.Max(), target ?? 0));
        double min = minY ?? 0;
        if (min >= max) min = 0;
        double plotW = width - padL - padR, plotH = height - padT - padB;
        double Y(double v) => padT + plotH - (Math.Max(v, min) - min) / (max - min) * plotH;
        double slot = plotW / values.Count, barW = Math.Min(38, slot * 0.62);

        var sb = Start(width, height);
        Grid(sb, padL, padT, plotW, plotH, min, max, valueFormat);
        for (int i = 0; i < values.Count; i++)
        {
            double x = padL + slot * i + (slot - barW) / 2, y = Y(values[i]);
            sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(barW)}\" height=\"{F(padT + plotH - y)}\" rx=\"2\" fill=\"{colors[i % colors.Count]}\"/>");
            if (!dense || colors[i % colors.Count] != baseColor)
                sb.Append($"<text x=\"{F(x + barW / 2)}\" y=\"{F(y - 3)}\" font-size=\"7.5\" font-weight=\"bold\" fill=\"#0f1b24\" text-anchor=\"middle\" {Font}>{values[i].ToString(valueFormat, CultureInfo.InvariantCulture)}</text>");
            if (i % labelStep != 0) continue;
            var label = labels[i].Length > 16 ? labels[i][..15] + "." : labels[i];
            double lx = x + barW / 2, ly = padT + plotH + 11;
            sb.Append(tilt
                ? $"<text x=\"{F(lx)}\" y=\"{F(ly)}\" font-size=\"7\" fill=\"#51697a\" text-anchor=\"end\" transform=\"rotate(-35 {F(lx)} {F(ly)})\" {FontFor(label)}>{Esc(label)}</text>"
                : $"<text x=\"{F(lx)}\" y=\"{F(ly + 3)}\" font-size=\"7.5\" fill=\"#51697a\" text-anchor=\"middle\" {FontFor(label)}>{Esc(label)}</text>");
        }
        if (target is { } t && t > min)
        {
            sb.Append($"<line x1=\"{padL}\" x2=\"{F(padL + plotW)}\" y1=\"{F(Y(t))}\" y2=\"{F(Y(t))}\" stroke=\"#ba1a1a\" stroke-width=\"1.2\" stroke-dasharray=\"5 4\"/>");
            if (targetLabel is not null)
            {
                // Keyed in the top-right corner, clear of the bar value labels.
                double kx = padL + plotW;
                sb.Append($"<text x=\"{F(kx)}\" y=\"10\" font-size=\"7.5\" fill=\"#ba1a1a\" text-anchor=\"end\" {FontFor(targetLabel)}>{Esc(targetLabel)}</text>");
                sb.Append($"<line x1=\"{F(kx - targetLabel.Length * 4.2 - 22)}\" x2=\"{F(kx - targetLabel.Length * 4.2 - 6)}\" y1=\"7\" y2=\"7\" stroke=\"#ba1a1a\" stroke-width=\"1.2\" stroke-dasharray=\"4 3\"/>");
            }
        }
        return sb.Append("</svg>").ToString();
    }

    private static StringBuilder Start(int w, int h) =>
        new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">");

    private static void Grid(StringBuilder sb, double padL, double padT, double plotW, double plotH, double min, double max, string fmt)
    {
        for (int i = 0; i <= 4; i++)
        {
            double v = min + (max - min) * i / 4;
            double y = padT + plotH - plotH * i / 4;
            sb.Append($"<line x1=\"{F(padL)}\" x2=\"{F(padL + plotW)}\" y1=\"{F(y)}\" y2=\"{F(y)}\" stroke=\"#dbe8f0\" stroke-width=\"0.8\"/>");
            sb.Append($"<text x=\"{F(padL - 4)}\" y=\"{F(y + 3)}\" font-size=\"7.5\" fill=\"#51697a\" text-anchor=\"end\" {Font}>{v.ToString(fmt, CultureInfo.InvariantCulture)}</text>");
        }
    }

    private static void Legend(StringBuilder sb, List<(string Name, string Color, bool Dashed)> items, double x)
    {
        foreach (var (name, color, dashed) in items)
        {
            var dash = dashed ? " stroke-dasharray=\"4 3\"" : "";
            sb.Append($"<line x1=\"{F(x)}\" x2=\"{F(x + 16)}\" y1=\"9\" y2=\"9\" stroke=\"{color}\" stroke-width=\"2.2\"{dash}/>");
            sb.Append($"<text x=\"{F(x + 20)}\" y=\"12\" font-size=\"8\" fill=\"#0f1b24\" {FontFor(name)}>{Esc(name)}</text>");
            x += 28 + name.Length * 4.6;
        }
    }

    private static double NiceMax(double v)
    {
        if (v <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            if (m * mag >= v * 1.05) return m * mag;
        return 10 * mag;
    }

    private static string Empty(int w, int h) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\"><text x=\"{w / 2}\" y=\"{h / 2}\" font-size=\"9\" fill=\"#51697a\" text-anchor=\"middle\" {Font}>Not enough data to chart</text></svg>";
}
