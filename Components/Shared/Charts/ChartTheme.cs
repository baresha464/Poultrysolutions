using System.Text.Json;

namespace AmrPoultryFarmWeb.Components.Shared.Charts;

/// <summary>
/// Single source of truth for the hex values ApexCharts options are built from in C# (JS chart
/// libraries need real color strings, not CSS custom properties). Keep this in sync with the
/// palette in wwwroot/css/app.css — it's the only other place these colors are hardcoded.
/// </summary>
public static class ChartTheme
{
    public const string Primary = "#0284c7";
    public const string PrimaryDeep = "#075985";
    public const string PrimaryBright = "#0ea5e9";
    public const string PrimarySoft = "#e0f2fe";
    public const string Amber = "#e8a33d";
    public const string AmberDeep = "#b87621";
    public const string Clay = "#a85c32";
    public const string Indigo = "#4f46e5";
    public const string Plum = "#7a4e92";
    public const string Danger = "#ba1a1a";
    public const string Ink = "#0f1b24";
    public const string InkSoft = "#51697a";
    public const string Line = "#dbe8f0";

    public static readonly string[] Series = { Primary, Amber, Clay, Indigo, Plum, Danger, PrimaryDeep, AmberDeep };

    /// <summary>
    /// Builds a JSON-object literal from ordered key/value pairs. ApexCharts' option keys are a
    /// mix of camelCase ("dataLabels") and all-lowercase ("xaxis", "yaxis"), so this takes the
    /// exact string key rather than relying on a C# property-naming policy that couldn't express
    /// both at once.
    /// </summary>
    public static Dictionary<string, object?> Opt(params (string Key, object? Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Value);

    /// <summary>
    /// Wraps a literal JS function body so charts.js' reviver turns it back into a real function
    /// before ApexCharts sees it (JSON itself can't carry executable callbacks).
    /// </summary>
    public static string Fn(string body) => body;

    public static string Json(object options) => JsonSerializer.Serialize(options);

    /// <summary>
    /// Builds a single-series ApexCharts area/bar trend from a list of (label, value) points —
    /// the FCR/weight/mortality trend mini-charts shared across the batch detail and daily-record
    /// screens.
    /// </summary>
    public static string BuildTrendJson(List<LineChartPoint> points, string color, string suffix, bool area)
    {
        var chartType = area ? "area" : "bar";
        var fill = area
            ? Opt(("type", "gradient"), ("gradient", Opt(("shadeIntensity", 1), ("opacityFrom", 0.4), ("opacityTo", 0.05))))
            : Opt(("type", "solid"));
        var plotOptions = area ? null : Opt(("bar", Opt(("borderRadius", 3), ("columnWidth", "55%"))));
        var yaxisLabels = Opt(("formatter", Fn($"function(v){{ return v.toFixed(0) + '{suffix}'; }}")));

        var options = Opt(
            ("chart", Opt(("type", chartType), ("toolbar", Opt(("show", false))), ("fontFamily", "IBM Plex Sans, sans-serif"))),
            ("series", new object[] { Opt(("name", "Value"), ("data", points.Select(p => p.Value).ToArray())) }),
            ("xaxis", Opt(("categories", points.Select(p => p.Label).ToArray()), ("labels", Opt(("style", Opt(("fontSize", "10px"))))))),
            ("yaxis", Opt(("labels", yaxisLabels))),
            ("colors", new[] { color }),
            ("fill", fill),
            ("stroke", Opt(("curve", "smooth"), ("width", area ? 2.5 : 0))),
            ("dataLabels", Opt(("enabled", false))),
            ("grid", Opt(("borderColor", Line), ("strokeDashArray", 4))),
            ("tooltip", Opt(("theme", "light")))
        );
        if (plotOptions is not null) options["plotOptions"] = plotOptions;
        return Json(options);
    }
}
