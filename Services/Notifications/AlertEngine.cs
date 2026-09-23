using AmrPoultryFarmWeb.Models;
using AmrPoultryFarmWeb.Services.Reports;
using AmrPoultryFarmWeb.Services.Reports.Pdf;

namespace AmrPoultryFarmWeb.Services.Notifications;

public static class AlertKinds
{
    public const string Mortality = "Mortality";
    public const string Heat = "Heat";
    public const string Cold = "Cold";
    public const string Feed = "Feed";
    public const string VaccinationDue = "VaccinationDue";
    public const string VaccinationLate = "VaccinationLate";
    public const string MissingEntry = "MissingEntry";
    public const string DailySummary = "DailySummary";
    public const string Test = "Test";
}

/// <summary>One alert, already worded in the recipient's language. <see cref="DedupKey"/> identifies
/// the underlying event (e.g. "this batch's death spike on 12 Sep") so it is only ever sent once.</summary>
public record FarmAlert(string Kind, string DedupKey, int BatchId, string Title, string Details, string Action);

/// <summary>The six values of the daily-summary template, in template order.</summary>
public record DailySummaryMessage(string DedupKey, int BatchId, IReadOnlyList<string> Parameters);

/// <summary>
/// Decides which WhatsApp alerts a batch needs right now. Pure: records in, alerts out — no I/O, no
/// clock (the caller passes the farm-local time), so it's easy to reason about and test.
/// Thresholds are deliberately conservative: a farmer who gets pinged for noise stops reading.
/// </summary>
public static class AlertEngine
{
    /// <summary>Common broiler programme in India: ND+IB ~day 5-7, IBD ~day 12-14, ND booster ~day 21.</summary>
    public static readonly (int Day, string Name)[] VaccinationPlan =
    {
        (6, "ND + IB"),
        (13, "IBD (Gumboro)"),
        (21, "ND Lasota booster"),
    };

    public const decimal HeatMaxTempC = 35m;

    public static List<FarmAlert> Evaluate(BatchDataset d, DateTime localNow, PdfText T)
    {
        var alerts = new List<FarmAlert>();
        var b = d.Batch;
        if (b.Status != BatchStatus.Active) return alerts;

        var today = localNow.Date;
        int age = BatchAnalytics.AgeOn(b, today);
        string head = Header(b, age, T);
        string dayKey = today.ToString("yyyyMMdd");
        var daily = d.Daily.OrderBy(r => r.Date).ToList();

        // ---- Mortality spike (latest record from today or yesterday vs the 7 records before it) ----
        var latest = daily.LastOrDefault(r => r.Date.Date >= today.AddDays(-1));
        if (latest is not null)
        {
            var prior = daily.Where(r => r.Date < latest.Date).TakeLast(7).ToList();
            int loss = latest.Mortality + latest.Culls;
            if (prior.Count >= 2)
            {
                double avg = prior.Average(r => r.Mortality + r.Culls);
                if (loss >= 5 && loss >= Math.Max(3 * avg, avg + 5))
                {
                    alerts.Add(new FarmAlert(AlertKinds.Mortality, $"mort:{b.Id}:{latest.Date:yyyyMMdd}", b.Id,
                        head + " — " + T["Deaths spiked: {0} birds on {1}", loss, latest.Date.ToString("dd MMM")],
                        T["That is {0:0.#}x the recent daily average of {1:0.#}. Total mortality is now {2:0.00}%.", avg > 0 ? loss / avg : loss, avg, d.Perf.MortalityPct],
                        T["Check the shed now, remove dead birds, note the symptoms and call your vet if deaths continue. You can use the Photo Health Check in BroilIQ."]));
                }
            }
        }

        // ---- Heat / chilling (today's reading only — yesterday's heat is no longer actionable) ----
        var todayRecord = daily.LastOrDefault(r => r.Date.Date == today);
        if (todayRecord is not null && (todayRecord.MinTempC != 0 || todayRecord.MaxTempC != 0))
        {
            double target = BatchAnalytics.TargetTempC(age);
            decimal avgTemp = (todayRecord.MinTempC + todayRecord.MaxTempC) / 2;
            if (todayRecord.MaxTempC >= HeatMaxTempC || (double)avgTemp >= target + 6)
            {
                alerts.Add(new FarmAlert(AlertKinds.Heat, $"heat:{b.Id}:{dayKey}", b.Id,
                    head + " — " + T["Heat stress risk: shed at {0:0.#} °C", todayRecord.MaxTempC],
                    T["The target for day {0} is about {1:0} °C. A hot shed causes panting, lower feed intake and sudden deaths.", age, target],
                    T["Open curtains, run fans and foggers, give cool water with electrolytes, and avoid feeding in the hottest hours."]));
            }
            else if (age <= 14 && (double)todayRecord.MinTempC <= target - 6)
            {
                alerts.Add(new FarmAlert(AlertKinds.Cold, $"cold:{b.Id}:{dayKey}", b.Id,
                    head + " — " + T["Chilling risk: shed down to {0:0.#} °C", todayRecord.MinTempC],
                    T["Young chicks need about {0:0} °C at day {1}. Cold chicks huddle, eat less and fall behind on weight.", target, age],
                    T["Close curtains on the wind side, check brooders and gas, and watch the chicks for huddling."]));
            }
        }

        // ---- Feed running out ----
        var recentUse = daily.TakeLast(3).Where(r => r.FeedConsumedKg > 0).ToList();
        if (recentUse.Count > 0)
        {
            decimal perDay = recentUse.Average(r => r.FeedConsumedKg);
            decimal stock = d.Perf.FeedStockKg;
            decimal daysLeft = perDay > 0 ? stock / perDay : 99;
            if (daysLeft <= 2)
            {
                alerts.Add(new FarmAlert(AlertKinds.Feed, $"feed:{b.Id}:{dayKey}", b.Id,
                    head + " — " + T["Feed running low: about {0:0.#} day(s) left", Math.Max(0, daysLeft)],
                    T["{0:N0} kg in stock by the records, using about {1:N0} kg a day.", Math.Max(0, stock), perDay],
                    T["Ask the integrator for the next feed delivery today, and log the DC in BroilIQ when it arrives (a missing DC also makes stock look low)."]));
            }
        }

        // ---- Vaccinations: reminder the day before, and one nudge if a dose looks missed ----
        int given = d.Health.Count(h => h.Type == HealthEventType.Vaccination);
        for (int i = 0; i < VaccinationPlan.Length; i++)
        {
            var (day, name) = VaccinationPlan[i];
            if (given > i) continue;
            if (age == day - 1)
            {
                alerts.Add(new FarmAlert(AlertKinds.VaccinationDue, $"vaxdue:{b.Id}:{i}", b.Id,
                    head + " — " + T["Vaccination due tomorrow: {0}", name],
                    T["{0} is usually given around day {1}. Tomorrow the batch is on day {2}.", name, day, age + 1],
                    T["Keep the vaccine cold, stop water sanitiser 24 hours before, and log the dose in BroilIQ after giving it."]));
            }
            else if (age >= day + 2)
            {
                alerts.Add(new FarmAlert(AlertKinds.VaccinationLate, $"vaxlate:{b.Id}:{i}", b.Id,
                    head + " — " + T["Vaccination not logged: {0}", name],
                    T["{0} is usually given around day {1}; the batch is on day {2} with {3} vaccination(s) logged.", name, day, age, given],
                    T["If it was given, log it in BroilIQ. If it was missed, ask your vet about a catch-up dose."]));
            }
            break; // only ever talk about the next dose in the programme
        }

        // ---- Today's record missing (evening check) ----
        if (localNow.Hour >= 20 && age >= 1 && todayRecord is null)
        {
            alerts.Add(new FarmAlert(AlertKinds.MissingEntry, $"missing:{b.Id}:{dayKey}", b.Id,
                head + " — " + T["Today's record is not entered yet"],
                T["No daily record for {0} yet. Deaths, feed and weight every day are what let BroilIQ spot problems early.", today.ToString("dd MMM")],
                T["Open BroilIQ and add today's record before the day ends."]));
        }

        return alerts;
    }

    public static DailySummaryMessage? Summary(BatchDataset d, DateTime localNow, PdfText T)
    {
        var b = d.Batch;
        if (b.Status != BatchStatus.Active) return null;
        var today = localNow.Date;
        int age = Math.Max(1, BatchAnalytics.AgeOn(b, today));
        var p = d.Perf;
        var todayRecord = d.Daily.LastOrDefault(r => r.Date.Date == today);

        string deaths = todayRecord is null
            ? T["not entered yet (total {0:0.00}%)", p.MortalityPct]
            : T["{0} today (total {1:0.00}%)", todayRecord.Mortality + todayRecord.Culls, p.MortalityPct];
        string weight = p.AvgBodyWeightKg > 0
            ? T["{0:0} g (standard {1:0} g)", p.AvgBodyWeightKg * 1000, FlockAdvisor.TargetWeightGmAt(age)]
            : T["no sample yet"];
        string fcr = p.Fcr > 0 ? T["{0:0.000} (standard {1:0.00})", p.Fcr, FlockAdvisor.TargetFcrAt(age)] : "-";

        var recentUse = d.Daily.OrderBy(r => r.Date).TakeLast(3).Where(r => r.FeedConsumedKg > 0).ToList();
        decimal perDay = recentUse.Count > 0 ? recentUse.Average(r => r.FeedConsumedKg) : 0;
        string feed = perDay > 0
            ? T["{0:N0} kg (~{1:0.#} days)", Math.Max(0, p.FeedStockKg), Math.Max(0, p.FeedStockKg / perDay)]
            : T["{0:N0} kg", Math.Max(0, p.FeedStockKg)];

        var advisor = FlockAdvisor.Analyze(b, p, d.Daily, d.Health, T.Localizer);
        var worst = advisor.Insights.FirstOrDefault(i => i.Level >= InsightLevel.Warning);
        string status = worst is null
            ? T["Flock score {0:0}/100 — all on track.", advisor.OverallScore]
            : T["Flock score {0:0}/100 — check: {1}", advisor.OverallScore, worst.Title];

        return new DailySummaryMessage($"summary:{b.Id}:{today:yyyyMMdd}", b.Id,
            new[] { Header(b, age, T), deaths, weight, fcr, feed, status });
    }

    private static string Header(Batch b, int age, PdfText T) =>
        $"{b.House?.Name} · {b.BatchCode} · {T["Day {0}", age]}";
}
