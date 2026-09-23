using System.Globalization;
using AmrPoultryFarmWeb.Models;
using Microsoft.Extensions.Localization;

namespace AmrPoultryFarmWeb.Services;

/// <summary>
/// Turns a batch's live performance numbers into a plain-language advisory: a 0-100 Flock Health
/// Score broken into five factors, plus the specific suggestions behind any factor that is
/// dragging on FCR or income. Everything here is derived from data already on the batch — no
/// separate data entry, no persistence, recomputed fresh every time the Advisor tab is opened.
///
/// Benchmarks (target weight/FCR-by-day, brooding temperature-by-week) are the commonly published
/// Cobb/Ross broiler targets used across the industry — approximate by design, since the point is
/// to flag deviations worth investigating, not to grade against a specific breed's exact spec.
///
/// Text is written as composite-format English keys; pass a localizer to get it in the user's
/// language (omit it — e.g. for AI grounding — to get English). Component labels stay English keys
/// (they drive the score weighting) and are translated where they're displayed.
/// </summary>
public static class FlockAdvisor
{
    // (Day, target body weight gm, target cumulative FCR)
    private static readonly (int Day, double WeightGm, double Fcr)[] Benchmark =
    {
        (0, 42, 0.80),   // day-old chick — without this, days 1-6 were judged against the day-7 target
        (7, 180, 0.95),
        (14, 430, 1.15),
        (21, 780, 1.35),
        (28, 1250, 1.50),
        (35, 1800, 1.65),
        (42, 2300, 1.80),
        (49, 2750, 1.95),
    };

    public static double TargetWeightGmAt(int ageDays) => Interpolate(ageDays, b => b.WeightGm);
    public static double TargetFcrAt(int ageDays) => Interpolate(ageDays, b => b.Fcr);

    private static double Interpolate(int ageDays, Func<(int Day, double WeightGm, double Fcr), double> select)
    {
        if (ageDays <= Benchmark[0].Day) return select(Benchmark[0]);
        if (ageDays >= Benchmark[^1].Day) return select(Benchmark[^1]);

        for (int i = 1; i < Benchmark.Length; i++)
        {
            if (ageDays > Benchmark[i].Day) continue;
            var lo = Benchmark[i - 1];
            var hi = Benchmark[i];
            var t = (ageDays - lo.Day) / (double)(hi.Day - lo.Day);
            return select(lo) + (select(hi) - select(lo)) * t;
        }
        return select(Benchmark[^1]);
    }

    private static InsightLevel LevelFor(double score) => score switch
    {
        >= 85 => InsightLevel.Good,
        >= 65 => InsightLevel.Info,
        >= 45 => InsightLevel.Warning,
        _ => InsightLevel.Critical,
    };

    /// <summary>Formats an English key (translated when a localizer is given) with invariant-safe en-IN numbers.</summary>
    private sealed class Tr
    {
        private readonly IStringLocalizer? l;
        public Tr(IStringLocalizer? l) => this.l = l;
        public string this[string key, params object[] args] =>
            l is null ? string.Format(CultureInfo.GetCultureInfo("en-IN"), key, args) : l[key, args].Value;
        public List<string> List(params string[] keys) => keys.Select(k => this[k]).ToList();
    }

    public static AdvisorResult Analyze(Batch batch, BatchPerformance perf, List<DailyRecord> daily, List<HealthEvent> healthEvents, IStringLocalizer? localizer = null)
    {
        var result = new AdvisorResult();
        if (batch is null) return result;
        var T = new Tr(localizer);

        int ageDays = Math.Max(1, perf.AgeDays);
        var ordered = daily.OrderBy(d => d.Date).ToList();
        var latest = ordered.LastOrDefault();

        // ---------------- FCR ----------------
        double targetFcr = TargetFcrAt(ageDays);
        double fcrScore = 100;
        if (perf.Fcr > 0)
        {
            double fcrDeviationPct = (double)(perf.Fcr - (decimal)targetFcr) / targetFcr * 100;
            fcrScore = Math.Clamp(100 - Math.Max(0, fcrDeviationPct) * 3, 0, 100);
        }
        result.Components.Add(new ScoreComponent("FCR", Math.Round(fcrScore, 0)));
        result.Insights.Add(BuildFcrInsight(T, perf, targetFcr, fcrScore));

        // ---------------- Growth ----------------
        double targetWeightGm = TargetWeightGmAt(ageDays);
        double actualWeightGm = (double)(perf.AvgBodyWeightKg * 1000m);
        double growthScore = 100;
        if (actualWeightGm > 0)
        {
            double weightDeviationPct = (actualWeightGm - targetWeightGm) / targetWeightGm * 100;
            growthScore = Math.Clamp(100 + Math.Min(0, weightDeviationPct) * 2.5, 0, 100);
        }
        result.Components.Add(new ScoreComponent("Growth", Math.Round(growthScore, 0)));
        result.Insights.Add(BuildGrowthInsight(T, ageDays, actualWeightGm, targetWeightGm, growthScore));

        // ---------------- Livability ----------------
        double livabilityScore = Math.Clamp(100 - (double)Math.Max(0, 97m - perf.LivabilityPct) * 10, 0, 100);
        result.Components.Add(new ScoreComponent("Livability", Math.Round(livabilityScore, 0)));
        result.Insights.Add(BuildLivabilityInsight(T, perf, ordered, livabilityScore));

        // ---------------- Health program ----------------
        double healthScore = ScoreHealthProgram(T, ageDays, healthEvents, out var healthMsg);
        result.Components.Add(new ScoreComponent("Health Program", Math.Round(healthScore, 0)));
        result.Insights.Add(new AdvisorInsight(LevelFor(healthScore), T["Health Program"],
            healthScore >= 85 ? T["Vaccination schedule on track"] : T["Vaccination schedule needs attention"],
            healthMsg,
            T.List("Missed or delayed ND/IBD vaccination", "Poor vaccine cold-chain / storage", "Stress at vaccination reducing uptake", "No booster for extended cycles")));

        // ---------------- Environment ----------------
        double envScore = ScoreEnvironment(T, ageDays, latest, out var envMsg);
        result.Components.Add(new ScoreComponent("Environment", Math.Round(envScore, 0)));
        result.Insights.Add(new AdvisorInsight(LevelFor(envScore), T["Environment"],
            envScore >= 85 ? T["Shed temperature & humidity in range"] : T["Shed conditions may be stressing the flock"],
            envMsg,
            T.List("Brooding temperature too high/low for age", "Poor ventilation / high humidity raising litter moisture", "Draughts at bird level", "Overcrowding cutting effective airflow")));

        // ---------------- Feed stock ----------------
        double avgDailyFeed = ageDays > 0 ? (double)perf.FeedConsumedKg / ageDays : 0;
        if (avgDailyFeed > 0 && perf.FeedStockKg < (decimal)avgDailyFeed * 3 && batch.Status == BatchStatus.Active)
        {
            string stockMessage = perf.FeedStockKg <= 0
                ? T["Recorded feed deliveries are already {0:0} kg behind recorded consumption — check that all feed delivery DCs have been logged, then place the next order right away.", Math.Abs(perf.FeedStockKg)]
                : T["At the current consumption rate (~{0:0} kg/day) the {1:0} kg on hand covers roughly {2:0.#} day(s). Place the next feed order now to avoid a forced ration change, which itself hurts FCR.", avgDailyFeed, perf.FeedStockKg, perf.FeedStockKg / (decimal)avgDailyFeed];
            result.Insights.Add(new AdvisorInsight(InsightLevel.Warning, T["Feed Stock"], T["Feed stock is running low"], stockMessage,
                T.List("Running out mid-cycle forces a feed-type switch", "Emergency local purchase is usually costlier and lower quality", "Missing/unlogged feed delivery DCs make stock look lower than it is")));
        }

        result.Components.RemoveAll(c => double.IsNaN(c.Score));
        result.OverallScore = result.Components.Count > 0
            ? Math.Round(Weighted(result.Components), 0)
            : 0;

        result.Insights = result.Insights
            .OrderByDescending(i => i.Level)
            .ToList();

        return result;
    }

    private static double Weighted(List<ScoreComponent> components)
    {
        var weights = new Dictionary<string, double>
        {
            ["FCR"] = 0.30,
            ["Growth"] = 0.25,
            ["Livability"] = 0.25,
            ["Health Program"] = 0.10,
            ["Environment"] = 0.10,
        };
        double sum = 0, weightSum = 0;
        foreach (var c in components)
        {
            var w = weights.GetValueOrDefault(c.Label, 0.1);
            sum += c.Score * w;
            weightSum += w;
        }
        return weightSum > 0 ? sum / weightSum : 0;
    }

    private static AdvisorInsight BuildFcrInsight(Tr T, BatchPerformance perf, double targetFcr, double score)
    {
        var factors = T.List(
            "Feed wastage — feeder height/fill level not adjusted to bird age",
            "Water spillage soaking litter (birds eat less, feed spoils)",
            "Feed particle size/quality inconsistent with age",
            "Coccidiosis, worm load or other gut-health issues",
            "Heat stress cutting feed-to-gain efficiency",
            "Overstocking increasing competition at the feeder");
        if (perf.Fcr <= 0)
            return new AdvisorInsight(InsightLevel.Info, "FCR", T["Not enough data for an FCR reading yet"],
                T["Log a body-weight sample on the next daily record to start tracking FCR."], factors);

        var level = LevelFor(score);
        var deltaPct = targetFcr > 0 ? (double)(perf.Fcr - (decimal)targetFcr) / targetFcr * 100 : 0;
        string message = level == InsightLevel.Good
            ? T["FCR of {0:0.000} is at or better than the {1:0.00} benchmark for this age — feed conversion is efficient right now.", perf.Fcr, targetFcr]
            : T["FCR of {0:0.000} is running about {1:0}% worse than the {2:0.00} benchmark for this age. Every 0.1 point of extra FCR adds real feed cost per kg of bird produced — the factors on the right are the usual causes.", perf.Fcr, deltaPct, targetFcr];
        return new AdvisorInsight(level, "FCR", level == InsightLevel.Good ? T["FCR is on target"] : T["FCR is behind benchmark"], message, factors);
    }

    private static AdvisorInsight BuildGrowthInsight(Tr T, int ageDays, double actualWeightGm, double targetWeightGm, double score)
    {
        var factors = T.List(
            "Ration density/energy too low for the growth stage",
            "Brooding temperature too low in week 1 (chicks huddle instead of eating)",
            "High stocking density limiting feeder/drinker access",
            "Disease or subclinical infection diverting energy from growth",
            "Inconsistent lighting program reducing feeding time");
        if (actualWeightGm <= 0)
            return new AdvisorInsight(InsightLevel.Info, T["Growth"], T["No weight sample yet"],
                T["Record an average body weight sample to start tracking growth against benchmark."], factors);

        var level = LevelFor(score);
        var deltaPct = (actualWeightGm - targetWeightGm) / targetWeightGm * 100;
        string message = level == InsightLevel.Good
            ? T["Average weight {0:0}g is on pace with the ~{1:0}g benchmark for day {2}.", actualWeightGm, targetWeightGm, ageDays]
            : T["Average weight {0:0}g is about {1:0}% behind the ~{2:0}g benchmark for day {3}. Falling behind now usually means more days to reach sale weight, which adds feed cost and delays the next placement.", actualWeightGm, Math.Abs(deltaPct), targetWeightGm, ageDays];
        return new AdvisorInsight(level, T["Growth"], level == InsightLevel.Good ? T["Growth is on target"] : T["Growth is behind benchmark"], message, factors);
    }

    private static AdvisorInsight BuildLivabilityInsight(Tr T, BatchPerformance perf, List<DailyRecord> ordered, double score)
    {
        var factors = T.List(
            "Disease outbreak (check for a mortality spike vs the daily average)",
            "Heat/cold stress, especially in the first and last week",
            "Ammonia build-up from wet litter / poor ventilation",
            "Feed or water access issues in part of the shed");

        string spikeNote = "";
        if (ordered.Count >= 4)
        {
            var todayMort = ordered[^1].Mortality;
            var priorAvg = ordered.Count > 8
                ? ordered.Skip(ordered.Count - 8).Take(7).Average(d => d.Mortality)
                : ordered.Take(ordered.Count - 1).Average(d => d.Mortality);
            if (todayMort > 0 && priorAvg >= 0 && todayMort > Math.Max(2, priorAvg * 2.5))
                spikeNote = " " + T["The latest day's mortality ({0}) is well above the recent average ({1:0.1}) — worth a closer look today, not at week's end.", todayMort, priorAvg];
        }

        var level = LevelFor(score);
        string message = level == InsightLevel.Good
            ? T["Livability of {0:0.0}% is healthy — total mortality/culls {1} of {2} placed.", perf.LivabilityPct, perf.TotalMortality + perf.TotalCulls, perf.ChicksPlaced] + spikeNote
            : T["Livability has dropped to {0:0.0}% ({1} lost of {2} placed), below the ~97% benchmark.", perf.LivabilityPct, perf.TotalMortality + perf.TotalCulls, perf.ChicksPlaced] + spikeNote
              + " " + T["Lost birds lower income directly — they're feed cost already spent with no sale weight to show for it."];
        return new AdvisorInsight(level, T["Livability"], level == InsightLevel.Good ? T["Livability is healthy"] : T["Livability needs attention"], message, factors);
    }

    private static double ScoreHealthProgram(Tr T, int ageDays, List<HealthEvent> events, out string message)
    {
        var vaccinations = events.Where(e => e.Type == HealthEventType.Vaccination).OrderBy(e => e.Date).ToList();
        int expectedByNow = ageDays >= 18 ? 2 : ageDays >= 5 ? 1 : 0;

        if (expectedByNow == 0)
        {
            message = vaccinations.Count > 0
                ? T["Vaccination log started — keep logging each dose as it's given."]
                : T["No vaccination due yet at this age. Log the first dose (typically ND) here once given, so it's tracked."];
            return 90;
        }

        if (vaccinations.Count >= expectedByNow)
        {
            message = T["{0} vaccination(s) logged, in line with a batch of this age.", vaccinations.Count];
            return 95;
        }

        message = T["Only {0} vaccination(s) logged for a day-{1} batch — typically {2}+ would be given by now (e.g. ND around day 5-7, IBD around day 14-18). If a dose was given but not logged, add it here; if it was missed, talk to your vet about a catch-up schedule.", vaccinations.Count, ageDays, expectedByNow];
        return vaccinations.Count == 0 ? 30 : 55;
    }

    private static double ScoreEnvironment(Tr T, int ageDays, DailyRecord? latest, out string message)
    {
        if (latest is null || (latest.MinTempC == 0 && latest.MaxTempC == 0))
        {
            message = T["No temperature/humidity readings logged yet — add them on the daily record to track brooding conditions."];
            return 80;
        }

        // Approximate brooding temperature target by week, °C.
        double targetTemp = ageDays switch
        {
            <= 7 => 33,
            <= 14 => 30,
            <= 21 => 27,
            <= 28 => 24,
            _ => 22,
        };

        double avgTemp = (double)((latest.MinTempC + latest.MaxTempC) / 2);
        double tempDelta = avgTemp - targetTemp;
        double humidity = (double)latest.HumidityPct;

        var issues = new List<string>();
        if (Math.Abs(tempDelta) > 4) issues.Add(tempDelta > 0
            ? T["shed running ~{0:0.#}°C above the ~{1:0}°C target for this age (heat stress risk)", tempDelta, targetTemp]
            : T["shed running ~{0:0.#}°C below the ~{1:0}°C target for this age (chilling risk)", Math.Abs(tempDelta), targetTemp]);
        if (humidity > 0 && (humidity < 40 || humidity > 75)) issues.Add(humidity > 75
            ? T["humidity at {0:0}% is high — check ventilation and litter moisture", humidity]
            : T["humidity at {0:0}% is low — dusty conditions can stress the respiratory tract", humidity]);

        if (issues.Count == 0)
        {
            message = T["Latest reading ({0:0.#}°C avg, {1:0}% humidity) is within the expected range for day {2}.", avgTemp, humidity, ageDays];
            return 95;
        }

        message = T["Latest reading flags: {0}. Both temperature and humidity swings push birds to spend energy on comfort instead of growth, which shows up as worse FCR.", string.Join("; ", issues)];
        return issues.Count >= 2 ? 40 : 60;
    }
}
