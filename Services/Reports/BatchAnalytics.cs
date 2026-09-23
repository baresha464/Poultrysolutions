using AmrPoultryFarmWeb.Models;

namespace AmrPoultryFarmWeb.Services.Reports;

/// <summary>
/// Pure calculations behind the PDF reports: weekly roll-ups, fair cross-batch KPIs, driver
/// correlations and the rule-based "next batch" action plan. No I/O — takes datasets, returns numbers.
///
/// Industry conventions used:
///  - Corrected FCR: raw FCR is not comparable between flocks lifted at different weights (heavier
///    birds always convert worse). Each batch is corrected to a common 2.0 kg at 0.03 FCR points per
///    100 g, the factor commonly used for modern heavy broilers.
///  - 7-day weight and first-week mortality are the earliest reliable predictors of final
///    performance (target ~180 g, i.e. 4-4.5x chick weight, and under ~1% mortality).
/// </summary>
public static class BatchAnalytics
{
    public const decimal ReferenceWeightKg = 2.0m;
    public const decimal FcrPointsPerKg = 0.30m;       // 0.03 per 100 g
    public const decimal Day7WeightTargetGm = 180m;
    public const decimal FirstWeekMortalityTargetPct = 1.0m;

    public static int AgeOn(Batch batch, DateTime date) => Math.Max(0, (int)(date.Date - batch.PlacementDate.Date).TotalDays);

    /// <summary>Approximate brooding/house temperature target by age, same curve FlockAdvisor uses.</summary>
    public static double TargetTempC(int ageDays) => ageDays switch
    {
        <= 7 => 33,
        <= 14 => 30,
        <= 21 => 27,
        <= 28 => 24,
        _ => 22,
    };

    /// <summary>Indian broiler seasons — the ones that actually change shed management.</summary>
    public static string SeasonOf(DateTime date) => date.Month switch
    {
        >= 3 and <= 6 => "Summer",
        >= 7 and <= 9 => "Monsoon",
        _ => "Winter",
    };

    public static decimal CorrectFcr(decimal fcr, decimal finalWeightKg) =>
        fcr > 0 && finalWeightKg > 0 ? Math.Round(fcr + (ReferenceWeightKg - finalWeightKg) * FcrPointsPerKg, 3) : 0;

    // ---------------- Weekly roll-up ----------------
    public static List<WeeklyRow> Weekly(BatchDataset d)
    {
        var batch = d.Batch;
        var rows = new List<WeeklyRow>();
        var daily = d.Daily.OrderBy(r => r.Date).ToList();
        if (daily.Count == 0) return rows;

        int cumLoss = 0;
        decimal cumFeed = 0;
        foreach (var week in daily.GroupBy(r => Math.Max(1, (AgeOn(batch, r.Date) + 6) / 7)).OrderBy(g => g.Key))
        {
            var list = week.ToList();
            int mort = list.Sum(r => r.Mortality);
            int culls = list.Sum(r => r.Culls);
            int birdsAtStart = batch.ChicksPlaced - cumLoss - LiftedBirdsBefore(d, list[0].Date);
            cumLoss += mort + culls;
            decimal feed = list.Sum(r => r.FeedConsumedKg);
            cumFeed += feed;

            var weighed = list.Where(r => r.AvgBodyWeightGm > 0).ToList();
            decimal? weight = weighed.Count > 0 ? weighed[^1].AvgBodyWeightGm : null;
            int endDate = AgeOn(batch, list[^1].Date);

            decimal? cumFcr = null;
            if (weight is { } w)
            {
                var weekEnd = list[^1].Date;
                int lifted = LiftedBirdsBefore(d, weekEnd.AddDays(1));
                decimal liftedKg = d.Liftings.Where(l => l.Date.Date <= weekEnd.Date).Sum(l => l.TotalWeightKg);
                int alive = Math.Max(0, batch.ChicksPlaced - cumLoss - lifted);
                decimal liveKg = alive * w / 1000m + liftedKg;
                if (liveKg > 0) cumFcr = Math.Round(cumFeed / liveKg, 3);
            }

            var temps = list.Where(r => r.MinTempC != 0 || r.MaxTempC != 0).ToList();
            var hum = list.Where(r => r.HumidityPct > 0).ToList();

            rows.Add(new WeeklyRow
            {
                Week = week.Key,
                Mortality = mort,
                Culls = culls,
                CumMortalityPct = batch.ChicksPlaced > 0 ? Math.Round((decimal)cumLoss / batch.ChicksPlaced * 100, 2) : 0,
                AvgWeightGm = weight,
                StdWeightGm = FlockAdvisor.TargetWeightGmAt(endDate),
                FeedKg = feed,
                FeedPerBirdGm = birdsAtStart > 0 ? Math.Round(feed * 1000m / birdsAtStart, 0) : 0,
                CumFcr = cumFcr,
                StdFcr = FlockAdvisor.TargetFcrAt(endDate),
                AvgTempC = temps.Count > 0 ? Math.Round(temps.Average(r => (r.MinTempC + r.MaxTempC) / 2), 1) : null,
                TargetTempC = TargetTempC(endDate),
                AvgHumidityPct = hum.Count > 0 ? Math.Round(hum.Average(r => r.HumidityPct), 0) : null,
            });
        }
        return rows;
    }

    private static int LiftedBirdsBefore(BatchDataset d, DateTime date) =>
        d.Liftings.Where(l => l.Date.Date < date.Date).Sum(l => l.BirdsLifted);

    // ---------------- Per-batch summary ----------------
    public static BatchSummary Summarize(BatchDataset d)
    {
        var batch = d.Batch;
        var perf = d.Perf;
        var daily = d.Daily.OrderBy(r => r.Date).ToList();

        int firstWeekLoss = daily.Where(r => AgeOn(batch, r.Date) <= 7).Sum(r => r.Mortality + r.Culls);
        var day7 = daily.Where(r => r.AvgBodyWeightGm > 0 && AgeOn(batch, r.Date) is >= 6 and <= 8)
            .OrderBy(r => Math.Abs(AgeOn(batch, r.Date) - 7)).FirstOrDefault();

        int liftedBirds = d.Liftings.Sum(l => l.BirdsLifted);
        decimal finalWeight = liftedBirds > 0 ? d.Liftings.Sum(l => l.TotalWeightKg) / liftedBirds : perf.AvgBodyWeightKg;
        int cycleDays = d.Liftings.Count > 0 ? AgeOn(batch, d.Liftings.Max(l => l.Date)) : perf.AgeDays;

        decimal? income = null;
        if (d.Settlement is { } s)
        {
            income = s.AmountReceived > 0
                ? s.AmountReceived
                : d.Liftings.Sum(l => l.TotalWeightKg) * s.GrowingChargePerKg + s.PerformanceIncentive - s.Deductions;
        }

        var tempDeviations = daily.Where(r => r.MinTempC != 0 || r.MaxTempC != 0)
            .Select(r => Math.Abs((double)(r.MinTempC + r.MaxTempC) / 2 - TargetTempC(AgeOn(batch, r.Date))))
            .ToList();

        int birdsOut = liftedBirds + perf.LiveBirds;
        var advisor = FlockAdvisor.Analyze(batch, perf, d.Daily, d.Health);

        return new BatchSummary
        {
            Data = d,
            Season = SeasonOf(batch.PlacementDate),
            CycleDays = cycleDays,
            FirstWeekMortalityPct = batch.ChicksPlaced > 0 ? Math.Round((decimal)firstWeekLoss / batch.ChicksPlaced * 100, 2) : 0,
            Day7WeightGm = day7?.AvgBodyWeightGm,
            FinalWeightKg = Math.Round(finalWeight, 3),
            CorrectedFcr = CorrectFcr(perf.Fcr, finalWeight),
            AdgGm = cycleDays > 0 ? Math.Round(finalWeight * 1000m / cycleDays, 1) : 0,
            FeedPerBirdKg = birdsOut > 0 ? Math.Round(perf.FeedConsumedKg / birdsOut, 2) : 0,
            Expenses = d.Expenses.Sum(e => e.Amount),
            HealthCost = d.Health.Sum(h => h.Cost),
            Income = income,
            HouseUtilizationPct = batch.House is { CapacityBirds: > 0 } h ? Math.Round((decimal)batch.ChicksPlaced / h.CapacityBirds * 100, 0) : null,
            VaccinationCount = d.Health.Count(h => h.Type == HealthEventType.Vaccination),
            AvgTempDeviationC = tempDeviations.Count > 0 ? Math.Round(tempDeviations.Average(), 1) : null,
            HealthScore = advisor.OverallScore,
            ExpensesByCategory = d.Expenses.GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount)),
        };
    }

    // ---------------- Cross-batch analysis ----------------
    public static double? Pearson(IReadOnlyList<(double X, double Y)> pts)
    {
        if (pts.Count < 4) return null;
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double sxy = 0, sxx = 0, syy = 0;
        foreach (var (x, y) in pts)
        {
            sxy += (x - mx) * (y - my);
            sxx += (x - mx) * (x - mx);
            syy += (y - my) * (y - my);
        }
        if (sxx <= 0 || syy <= 0) return null;
        return sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>Which management factors moved with EEF across this farm's own batches. Only links
    /// with |r| &gt;= 0.4 and at least 4 batches are reported.</summary>
    public static List<DriverLink> Drivers(List<BatchSummary> batches)
    {
        var drivers = new (string Name, string Unit, Func<BatchSummary, double?> Get)[]
        {
            ("7-day body weight", "g", b => (double?)b.Day7WeightGm),
            ("first-week mortality", "%", b => (double)b.FirstWeekMortalityPct),
            ("temperature deviation from target", "°C", b => b.AvgTempDeviationC),
            ("house fill (birds placed vs capacity)", "%", b => (double?)b.HouseUtilizationPct),
            ("vaccinations logged", "", b => b.VaccinationCount),
            ("final lifting age", " days", b => b.CycleDays),
            ("farm spend per bird", "", b => (double)b.CostPerBird),
        };

        var withResult = batches.Where(b => b.HasResult).ToList();
        var links = new List<DriverLink>();
        foreach (var (name, _, get) in drivers)
        {
            var pts = withResult.Where(b => get(b).HasValue).Select(b => (get(b)!.Value, (double)b.Eef)).ToList();
            if (Pearson(pts) is not { } r || Math.Abs(r) < 0.4) continue;
            string strength = Math.Abs(r) >= 0.7 ? "strongly" : "noticeably";
            string direction = r > 0 ? "higher" : "lower";
            links.Add(new DriverLink(name, "EEF", r, pts.Count,
                $"Batches with higher {name} {strength} tended to finish with {direction} EEF (r = {r:0.00}, {pts.Count} batches)."));
        }
        return links.OrderByDescending(l => Math.Abs(l.R)).ToList();
    }

    public static List<GroupStat> GroupBy(List<BatchSummary> batches, Func<BatchSummary, string> key)
    {
        return batches.Where(b => b.HasResult).GroupBy(key)
            .Select(g => new GroupStat(g.Key, g.Count(),
                Math.Round(g.Average(b => b.LivabilityPct), 2),
                Math.Round(g.Average(b => b.CorrectedFcr), 3),
                Math.Round(g.Average(b => b.Eef), 1),
                g.Any(b => b.ProfitPerBird.HasValue) ? Math.Round(g.Where(b => b.ProfitPerBird.HasValue).Average(b => b.ProfitPerBird!.Value), 2) : null))
            .OrderByDescending(g => g.Eef)
            .ToList();
    }

    /// <summary>Next-batch goals: the farm's own top-quartile result, never looser than the breed benchmark.</summary>
    public static List<TargetRow> Targets(List<BatchSummary> batches)
    {
        var done = batches.Where(b => b.HasResult).ToList();
        var rows = new List<TargetRow>();
        if (done.Count == 0) return rows;

        static List<decimal> TopQuartile(IEnumerable<decimal> values, bool higherIsBetter)
        {
            var sorted = higherIsBetter ? values.OrderByDescending(v => v).ToList() : values.OrderBy(v => v).ToList();
            return sorted.Take(Math.Max(1, (int)Math.Ceiling(sorted.Count / 4.0))).ToList();
        }

        void Add(string metric, List<decimal> values, bool higherIsBetter, decimal benchmark, string fmt, string suffix = "")
        {
            if (values.Count == 0) return;
            var best = higherIsBetter ? values.Max() : values.Min();
            var tq = TopQuartile(values, higherIsBetter).Average();
            var target = higherIsBetter ? Math.Max(tq, benchmark) : Math.Min(tq, benchmark);
            rows.Add(new TargetRow(metric, values.Average().ToString(fmt) + suffix, best.ToString(fmt) + suffix,
                (higherIsBetter ? ">= " : "<= ") + target.ToString(fmt) + suffix));
        }

        Add("7-day body weight", done.Where(b => b.Day7WeightGm.HasValue).Select(b => b.Day7WeightGm!.Value).ToList(), true, Day7WeightTargetGm, "0", " g");
        Add("First-week mortality", done.Select(b => b.FirstWeekMortalityPct).ToList(), false, FirstWeekMortalityTargetPct, "0.00", " %");
        Add("Livability", done.Select(b => b.LivabilityPct).ToList(), true, 96m, "0.0", " %");
        Add($"Corrected FCR ({ReferenceWeightKg:0.0} kg)", done.Select(b => b.CorrectedFcr).ToList(), false, 1.60m, "0.000");
        Add("EEF / EPEF", done.Select(b => b.Eef).ToList(), true, 350m, "0");
        Add("Avg daily gain", done.Select(b => b.AdgGm).ToList(), true, 55m, "0.0", " g");
        var costed = done.Where(b => b.CostPerKg > 0).Select(b => b.CostPerKg).ToList();
        if (costed.Count > 0)
        {
            var tq = TopQuartile(costed, false).Average();
            rows.Add(new TargetRow("Farm cost per kg live", "Rs " + costed.Average().ToString("0.00"), "Rs " + costed.Min().ToString("0.00"), "<= Rs " + tq.ToString("0.00")));
        }
        return rows;
    }

    /// <summary>Rule-based "what to change next batch" plan. Every item quotes the numbers behind it
    /// so the farmer can see why it was raised.</summary>
    public static List<ActionItem> ActionPlan(List<BatchSummary> batches)
    {
        var items = new List<ActionItem>();
        var done = batches.Where(b => b.HasResult).OrderBy(b => b.Batch.PlacementDate).ToList();
        if (done.Count == 0) return items;

        // ---- Brooding / 7-day weight ----
        var d7 = done.Where(b => b.Day7WeightGm.HasValue).ToList();
        if (d7.Count > 0)
        {
            var avg = d7.Average(b => b.Day7WeightGm!.Value);
            if (avg < 170)
                items.Add(new ActionItem(ActionPriority.High, "Brooding (week 1)",
                    $"7-day weight averages {avg:0} g across {d7.Count} batch(es), below the ~{Day7WeightTargetGm:0} g target. Low 7-day weight is the single best early predictor of poor final weight and FCR.",
                    new() { "Pre-heat the house 24-36 h before chick arrival so litter reaches 30-32 °C", "Check crop fill at 8 h and 24 h after placement (target > 80% / > 95%)", "Use chick paper / extra feeder trays for the first 3-4 days", "Keep 23 h light for the first week, fresh water within reach of every chick" },
                    $">= {Day7WeightTargetGm:0} g at day 7"));
        }

        var fwm = done.Average(b => b.FirstWeekMortalityPct);
        if (fwm > FirstWeekMortalityTargetPct)
            items.Add(new ActionItem(ActionPriority.High, "Chick quality & early mortality",
                $"First-week mortality averages {fwm:0.00}% (target under {FirstWeekMortalityTargetPct:0.0}%). Every early loss also raises later mortality risk in the same flock.",
                new() { "Record hatchery, breeder flock age and chick weight on arrival; raise a claim when week-1 loss exceeds 1%", "Grade chicks on arrival - separate weak chicks near heat and water", "Review transport time and chick-box temperature on delivery" },
                $"< {FirstWeekMortalityTargetPct:0.0}% in week 1"));

        // ---- Consistency: best vs worst ----
        if (done.Count >= 2)
        {
            var best = done.MinBy(b => b.CorrectedFcr)!;
            var worst = done.MaxBy(b => b.CorrectedFcr)!;
            var gap = worst.CorrectedFcr - best.CorrectedFcr;
            if (gap >= 0.08m)
            {
                var avgKg = done.Average(b => b.LiveWeightKg);
                var feedKg = gap * avgKg;
                var diffs = WhatWasDifferent(best, worst);
                items.Add(new ActionItem(ActionPriority.High, "Batch-to-batch consistency",
                    $"Corrected FCR ranges from {best.CorrectedFcr:0.000} ({best.Code}) to {worst.CorrectedFcr:0.000} ({worst.Code}) - a {gap:0.000} gap, about {feedKg:N0} kg of feed on a typical batch of {avgKg:N0} kg live weight."
                    + (diffs.Count > 0 ? " What differed: " + string.Join("; ", diffs) + "." : ""),
                    new() { $"Use {best.Code} as the farm's standard operating reference: same brooding set-up, feeder/drinker heights and vaccination timing", "Write a one-page checklist from the best batch and tick it daily in the next cycle" },
                    $"Corrected FCR <= {best.CorrectedFcr + gap / 3:0.000}"));
            }
        }

        // ---- Trend: latest vs earlier ----
        if (done.Count >= 3)
        {
            var latest = done[^1];
            var earlier = done.Take(done.Count - 1).ToList();
            var earlierEef = earlier.Average(b => b.Eef);
            if (earlierEef > 0 && latest.Eef < earlierEef * 0.93m)
            {
                var causes = WhatWasDifferent(earlier.MaxBy(b => b.Eef)!, latest);
                items.Add(new ActionItem(ActionPriority.High, "Latest batch slipped",
                    $"{latest.Code} finished at EEF {latest.Eef:0} vs {earlierEef:0} average for earlier batches ({(latest.Eef / earlierEef - 1) * 100:0}%)."
                    + (causes.Count > 0 ? " Compared with the best earlier batch: " + string.Join("; ", causes) + "." : ""),
                    new() { "Review the daily log of the latest batch week by week against the weekly-standard table in its closeout report", "Check whether anything changed in feed supplier, chick source, staff or equipment" }));
            }
        }

        // ---- Seasonal pattern ----
        var seasons = GroupBy(done, b => b.Season).Where(g => g.Batches >= 2).ToList();
        if (seasons.Count >= 2)
        {
            var bestS = seasons.MaxBy(g => g.Eef)!;
            var worstS = seasons.MinBy(g => g.Eef)!;
            if (bestS.Eef > 0 && worstS.Eef < bestS.Eef * 0.92m)
            {
                var actions = worstS.Group switch
                {
                    "Summer" => new List<string> { "Plan foggers/cool pads and extra fans before placement; feed in the cool hours", "Add electrolytes/vitamin C in water on hot days; reduce density 5-10% for summer flocks", "Lift earlier in the day to cut heat stress losses" },
                    "Monsoon" => new List<string> { "Top up and turn litter frequently - wet litter drives ammonia, foot pad lesions and coccidiosis", "Tighten anticoccidial program and drinker leak checks", "Secure feed storage against moisture/mould" },
                    _ => new List<string> { "Budget more brooding fuel; seal curtains but keep minimum ventilation for ammonia", "Check night-time temperatures - cold nights cut week-1 growth", "Increase brooder guard monitoring in the first 10 days" },
                };
                items.Add(new ActionItem(ActionPriority.Medium, $"{worstS.Group} batches underperform",
                    $"{worstS.Group} placements average EEF {worstS.Eef:0} vs {bestS.Eef:0} in {bestS.Group} ({worstS.Batches} vs {bestS.Batches} batches).",
                    actions));
            }
        }

        // ---- House pattern ----
        var houses = GroupBy(done, b => b.HouseName).ToList();
        if (houses.Count >= 2)
        {
            var bestH = houses[0];
            var worstH = houses[^1];
            if (bestH.Eef > 0 && worstH.Eef < bestH.Eef * 0.92m)
                items.Add(new ActionItem(ActionPriority.Medium, $"House gap: {worstH.Group}",
                    $"{worstH.Group} averages EEF {worstH.Eef:0} / corrected FCR {worstH.CorrectedFcr:0.000}, behind {bestH.Group} at EEF {bestH.Eef:0} / {bestH.CorrectedFcr:0.000}.",
                    new() { $"Inspect {worstH.Group}: air speed at bird level, curtain leaks, drinker line pressure and feeder height", $"Compare stocking density of {worstH.Group} to its capacity", $"Consider placing a slightly lower number in {worstH.Group} next cycle and compare results" }));
        }

        // ---- Overstocking ----
        var over = done.Where(b => b.HouseUtilizationPct > 100).ToList();
        if (over.Count > 0)
            items.Add(new ActionItem(ActionPriority.Medium, "Stocking density",
                $"{over.Count} batch(es) were placed above the house's rated capacity ({string.Join(", ", over.Select(b => $"{b.Code} {b.HouseUtilizationPct:0}%"))}).",
                new() { "Place at or below rated capacity - overcrowding increases feeder competition, heat load and leg problems", "If extra birds must go in, add feeders/drinkers and ventilation to match" },
                "<= 100% of rated capacity"));

        // ---- Vaccination record ----
        var fewVax = done.Where(b => b.CycleDays >= 18 && b.VaccinationCount < 2).ToList();
        if (fewVax.Count > 0)
            items.Add(new ActionItem(ActionPriority.Medium, "Vaccination records",
                $"{fewVax.Count} batch(es) show fewer than 2 vaccinations logged ({string.Join(", ", fewVax.Take(6).Select(b => b.Code))}).",
                new() { "Log every dose (ND, IBD and boosters) on the day it is given so the program can be checked", "Agree a written vaccination schedule with the integrator's vet before placement" }));

        // ---- Environment ----
        var envBatches = done.Where(b => b.AvgTempDeviationC.HasValue).ToList();
        if (envBatches.Count > 0 && envBatches.Average(b => b.AvgTempDeviationC!.Value) > 3)
            items.Add(new ActionItem(ActionPriority.Medium, "House temperature control",
                $"House temperature averaged {envBatches.Average(b => b.AvgTempDeviationC!.Value):0.0} °C away from the age target across {envBatches.Count} batch(es).",
                new() { "Record min/max temperature every day at bird height, not at the roof", "Adjust curtains/fans/brooders against the age target, not by feel" },
                "Within 2 °C of the age target"));

        // ---- Cost trend ----
        var costed = done.Where(b => b.CostPerKg > 0).ToList();
        if (costed.Count >= 3)
        {
            var latest = costed[^1];
            var avgPrev = costed.Take(costed.Count - 1).Average(b => b.CostPerKg);
            if (latest.CostPerKg > avgPrev * 1.10m)
            {
                var prev = costed.Take(costed.Count - 1).ToList();
                var jump = latest.ExpensesByCategory
                    .Select(kv => (kv.Key, Delta: kv.Value / Math.Max(1, latest.Batch.ChicksPlaced)
                        - prev.Average(b => b.ExpensesByCategory.GetValueOrDefault(kv.Key) / Math.Max(1, b.Batch.ChicksPlaced))))
                    .OrderByDescending(x => x.Delta).FirstOrDefault();
                items.Add(new ActionItem(ActionPriority.Medium, "Rising cost per kg",
                    $"{latest.Code} cost Rs {latest.CostPerKg:0.00}/kg live vs Rs {avgPrev:0.00} average before."
                    + (jump.Key is not null && jump.Delta > 0 ? $" Biggest increase: {jump.Key} (+Rs {jump.Delta:0.00} per bird placed)." : ""),
                    new() { "Check the expense breakdown in the Financial report and set a per-bird budget for each category", "Compare electricity/diesel use against brooding days - long brooding is usually the driver" }));
            }
        }

        // ---- Replicate the best ----
        var top = done.MaxBy(b => b.Eef)!;
        items.Add(new ActionItem(ActionPriority.Low, "Repeat what worked",
            $"Best batch so far: {top.Code} ({top.HouseName}, {top.Season}, {top.IntegratorName}) - EEF {top.Eef:0}, livability {top.LivabilityPct:0.0}%, corrected FCR {top.CorrectedFcr:0.000}"
            + (top.Day7WeightGm is { } w ? $", 7-day weight {w:0} g" : "") + $", first-week mortality {top.FirstWeekMortalityPct:0.00}%.",
            new() { "Keep the same brooding, lighting and vaccination timing as this batch", "Share this report with the supervisor and integrator field staff before the next placement" }));

        return items.OrderBy(i => i.Priority).ToList();
    }

    /// <summary>Plain-language differences between a good and a weaker batch on the early indicators.</summary>
    public static List<string> WhatWasDifferent(BatchSummary good, BatchSummary weak, Pdf.PdfText? text = null)
    {
        var T = text ?? Pdf.PdfText.English;
        var diffs = new List<string>();
        if (good.Day7WeightGm is { } g7 && weak.Day7WeightGm is { } w7 && g7 - w7 >= 10)
            diffs.Add(T["7-day weight {0:0} g vs {1:0} g", w7, g7]);
        if (weak.FirstWeekMortalityPct - good.FirstWeekMortalityPct >= 0.3m)
            diffs.Add(T["first-week mortality {0:0.00}% vs {1:0.00}%", weak.FirstWeekMortalityPct, good.FirstWeekMortalityPct]);
        if (good.LivabilityPct - weak.LivabilityPct >= 1)
            diffs.Add(T["livability {0:0.0}% vs {1:0.0}%", weak.LivabilityPct, good.LivabilityPct]);
        if (weak.AvgTempDeviationC is { } wt && good.AvgTempDeviationC is { } gt && wt - gt >= 1)
            diffs.Add(T["temperature off-target by {0:0.0} °C vs {1:0.0} °C", wt, gt]);
        if (weak.HouseUtilizationPct is { } wu && good.HouseUtilizationPct is { } gu && wu - gu >= 5)
            diffs.Add(T["house fill {0:0}% vs {1:0}%", wu, gu]);
        if (weak.Season != good.Season) diffs.Add(T["{0} vs {1} placement", T[weak.Season], T[good.Season]]);
        if (weak.HouseName != good.HouseName) diffs.Add(T["{0} vs {1}", weak.HouseName, good.HouseName]);
        if (weak.VaccinationCount < good.VaccinationCount) diffs.Add(T["{0} vs {1} vaccinations logged", weak.VaccinationCount, good.VaccinationCount]);
        return diffs;
    }
}
