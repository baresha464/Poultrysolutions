using AmrPoultryFarmWeb.Models;
using QuestPDF.Fluent;
using static AmrPoultryFarmWeb.Services.Reports.Pdf.PdfKit;

namespace AmrPoultryFarmWeb.Services.Reports.Pdf;

/// <summary>
/// Single-batch report: what happened in this flock, week by week, measured against the breed
/// standard and against the farm's own other batches, ending with lessons for the next placement.
/// Works for active batches too (titled "Progress Report") so it can be shared mid-cycle.
/// All text goes through <see cref="ReportBranding.Text"/>, so it renders in English or Telugu.
/// </summary>
public static class BatchCloseoutPdf
{
    public static byte[] Render(ReportBranding brand, BatchSummary s, List<BatchSummary> others)
    {
        var T = brand.Text;
        var d = s.Data;
        var b = s.Batch;
        var p = s.Perf;
        var weekly = BatchAnalytics.Weekly(d);
        // English run drives the which-findings-to-highlight logic; the localized run (same order)
        // supplies the text the reader sees.
        var advisorEn = FlockAdvisor.Analyze(b, p, d.Daily, d.Health);
        var advisor = FlockAdvisor.Analyze(b, p, d.Daily, d.Health, T.Localizer);
        var primary = brand.Primary;
        string Day(int n) => T["Day {0}", n];

        return Build(brand, col =>
        {
            // ---------------- Headline KPIs ----------------
            KpiRow(col, new Kpi[]
            {
                new(T["Chicks Placed"], N0(b.ChicksPlaced), D(b.PlacementDate)),
                new(T["Birds Lifted"], N0(p.BirdsLifted), p.LiveBirds > 0 ? T["{0} still in house", N0(p.LiveBirds)] : T["{0} days cycle", s.CycleDays]),
                new(T["Livability"], Pct(p.LivabilityPct), T["{0} dead · {1} culls", N0(p.TotalMortality), N0(p.TotalCulls)], ToneVs(p.LivabilityPct, 96, true, 1)),
                new(T["Avg Weight"], s.FinalWeightKg > 0 ? $"{s.FinalWeightKg:0.000} kg" : "-", T["Target {0:0.00} kg", b.TargetWeightKg]),
                new(T["Flock score"], $"{advisor.OverallScore:0}/100", T["Advisor composite"], advisor.OverallScore >= 85 ? Good : advisor.OverallScore >= 65 ? Warn : Bad),
            }, primary);
            KpiRow(col, new Kpi[]
            {
                new("FCR", p.Fcr > 0 ? p.Fcr.ToString("0.000") : "-", T["Std {0:0.00} at day {1}", FlockAdvisor.TargetFcrAt(Math.Max(1, s.CycleDays)), s.CycleDays], p.Fcr > 0 ? ToneVs(p.Fcr, (decimal)FlockAdvisor.TargetFcrAt(Math.Max(1, s.CycleDays)), false) : null),
                s.IsComplete
                    ? new(T["Corrected FCR"], s.CorrectedFcr > 0 ? s.CorrectedFcr.ToString("0.000") : "-", T["Adjusted to {0:0.0} kg", BatchAnalytics.ReferenceWeightKg])
                    : WeightVsStandard(T, s),
                s.IsComplete
                    ? new("EEF / EPEF", p.Eef > 0 ? p.Eef.ToString("0") : "-", T["Target 350+"], p.Eef > 0 ? ToneVs(p.Eef, 350, true, 10) : null)
                    : new(T["EEF (interim)"], p.Eef > 0 ? p.Eef.ToString("0") : "-", T["Final EEF is set at lifting"]),
                new(T["Avg daily gain"], s.AdgGm > 0 ? $"{s.AdgGm:0.0} g" : "-", T["Weight / age"]),
                new(T["Net Profit"], Rs(s.NetProfit), s.ProfitPerBird is { } ppb ? T["{0} per bird", Rs2(ppb)] : T["Settlement pending"], s.NetProfit is { } np ? (np >= 0 ? Good : Bad) : null),
            }, primary);

            // ---------------- Batch details ----------------
            var itemValue = new[] { T["Item"], T["Value"] };
            Block(col, T["Batch details"], null, primary, bd => bd.Item().Row(row =>
            {
                row.Spacing(12);
                row.RelativeItem().Column(c => Table(c, primary, itemValue, new[] { 1.2f, 1.6f }, new[]
                {
                    new[] { T["Batch code"], b.BatchCode },
                    new[] { T["House"], $"{b.House?.Name}" + (b.House is { CapacityBirds: > 0 } h ? " " + T["(capacity {0}, {1:0}% filled)", N0(h.CapacityBirds), s.HouseUtilizationPct ?? 0] : "") },
                    new[] { T["Integrator / branch"], $"{b.Integrator?.Name}" + (string.IsNullOrWhiteSpace(b.BranchCode) ? "" : $" / {b.BranchCode}") },
                    new[] { T["Breed"], b.Breed },
                    new[] { T["Season"], T[s.Season] },
                }, rightAligned: Array.Empty<int>()));
                row.RelativeItem().Column(c => Table(c, primary, itemValue, new[] { 1.2f, 1.6f }, new[]
                {
                    new[] { T["Placement date"], D(b.PlacementDate) },
                    new[] { T["Status"], b.Status == BatchStatus.Closed ? T["Closed"] + (b.ClosedDate is { } cd ? " " + D(cd) : "") : T["Active, day {0}", b.AgeDays] },
                    new[] { T["Chick cost (integrator DC)"], T["{0} / bird", Rs2(b.ChickCostPerBird)] },
                    new[] { T["Feed received / consumed"], $"{N0(p.FeedReceivedKg)} / {N0(p.FeedConsumedKg)} kg" },
                    new[] { T["Feed balance"], $"{N0(p.FeedStockKg)} kg" + (s.FeedPerBirdKg > 0 ? " · " + T["{0:0.00} kg eaten per bird", s.FeedPerBirdKg] : "") },
                }, rightAligned: Array.Empty<int>()));
            }));

            // ---------------- Benchmarks ----------------
            // A batch still in the house can't be compared with finished flocks on age-dependent
            // metrics (weight, FCR, EEF, livability all move with age) — those use the standard only.
            bool active = !s.IsComplete;
            var rows = new List<(string Metric, string This, string Avg, string Best, string Std, string Tone)>();
            void Cmp(string metric, decimal thisV, Func<BatchSummary, decimal> sel, bool higherBetter, decimal? std, string fmt, string suffix = "", bool ageDependent = true)
            {
                var vals = active && ageDependent ? new List<decimal>() : others.Select(sel).Where(v => v > 0).ToList();
                decimal? avg = vals.Count > 0 ? vals.Average() : null;
                decimal? best = vals.Count > 0 ? (higherBetter ? vals.Max() : vals.Min()) : null;
                var reference = avg ?? std ?? 0;
                var tone = thisV > 0 && reference > 0 ? ToneVs(thisV, reference, higherBetter) : Ink;
                rows.Add((T[metric], thisV > 0 ? thisV.ToString(fmt) + suffix : "-", Opt(avg, fmt, suffix), Opt(best, fmt, suffix), Opt(std, fmt, suffix), tone));
            }
            int age = Math.Max(1, s.CycleDays);
            Cmp("Livability", s.LivabilityPct, x => x.LivabilityPct, true, 96m, "0.0", "%");
            if (s.CycleDays >= 7)
                Cmp("First-week mortality", s.FirstWeekMortalityPct, x => x.FirstWeekMortalityPct, false, BatchAnalytics.FirstWeekMortalityTargetPct, "0.00", "%", ageDependent: false);
            Cmp("7-day weight", s.Day7WeightGm ?? 0, x => x.Day7WeightGm ?? 0, true, BatchAnalytics.Day7WeightTargetGm, "0", " g", ageDependent: false);
            Cmp(active ? "Current avg weight" : "Final avg weight", s.FinalWeightKg, x => x.FinalWeightKg, true, active ? Math.Round((decimal)FlockAdvisor.TargetWeightGmAt(age) / 1000m, 3) : null, "0.000", " kg");
            Cmp("FCR (raw)", s.Fcr, x => x.Fcr, false, (decimal)FlockAdvisor.TargetFcrAt(age), "0.000");
            if (!active) Cmp("Corrected FCR", s.CorrectedFcr, x => x.CorrectedFcr, false, 1.60m, "0.000");
            Cmp("EEF / EPEF", s.Eef, x => x.Eef, true, active ? null : 350m, "0");
            Cmp("Avg daily gain", s.AdgGm, x => x.AdgGm, true, active ? null : 55m, "0.0", " g");
            Cmp("Farm cost per kg", s.CostPerKg, x => x.CostPerKg, false, null, "0.00");
            Block(col, T["How this batch compares"],
                active
                    ? T["In progress: early-cycle metrics vs {0} completed batch(es); age-dependent metrics vs the standard for day {1}", others.Count, s.CycleDays]
                    : T["Against the breed standard and {0} other completed batch(es) on this farm", others.Count], primary, c =>
            {
                Table(c, primary, new[] { T["Metric"], T["This batch"], T["Farm average"], T["Farm best"], T["Standard"] }, new[] { 1.6f, 1, 1, 1, 1 },
                    rows.Select(r => new[] { r.Metric, r.This, r.Avg, r.Best, r.Std }),
                    cellColor: (r, cc) => cc == 1 ? rows[r].Tone : null);
                Note(c, T["Green = better than the farm average (or standard when there is no history); amber = within 3%; red = worse. Corrected FCR adjusts each batch to {0:0.0} kg at 0.03 per 100 g so batches lifted at different weights compare fairly.", BatchAnalytics.ReferenceWeightKg]);
            });

            // ---------------- Growth ----------------
            var weighed = d.Daily.Where(r => r.AvgBodyWeightGm > 0).OrderBy(r => r.Date).ToList();
            if (weighed.Count >= 2)
            {
                var ages = weighed.Select(r => BatchAnalytics.AgeOn(b, r.Date)).ToList();
                var labels = ages.Select(a => $"D{a}").ToList();
                var growthSeries = new[]
                {
                    new SvgCharts.Series(T["Actual"], primary, weighed.Select(r => (double?)r.AvgBodyWeightGm).ToList()),
                    new SvgCharts.Series(T["Standard"], "#8aa0ae", ages.Select(a => (double?)FlockAdvisor.TargetWeightGmAt(a)).ToList(), Dashed: true),
                };
                Block(col, T["Growth curve vs standard"], T["Sampled average body weight (g) by age in days"], primary, c =>
                {
                    ChartLegend(c, growthSeries);
                    c.Item().Svg(SvgCharts.Lines(labels, growthSeries, width: 540, height: 190, drawLegend: false)).FitWidth();
                }, keepWith: 240);
            }

            // ---------------- Weekly table ----------------
            if (weekly.Count > 0)
            {
                Block(col, T["Week-by-week performance"], T["Weight and cumulative FCR are at the last sample of each week"], primary, c => Table(c, primary,
                    new[] { T["Week"], T["Dead"], T["Culls"], T["Cum. mort."], T["Weight g"], T["Std g"], T["Feed kg"], T["g/bird/wk"], T["Cum. FCR"], T["Std FCR"], T["Avg °C"], T["Target °C"], T["RH %"] },
                    new[] { 0.6f, 0.6f, 0.6f, 0.9f, 0.9f, 0.8f, 0.9f, 0.9f, 0.9f, 0.8f, 0.8f, 0.9f, 0.7f },
                    weekly.Select(w => new[]
                    {
                        w.Week.ToString(), w.Mortality.ToString(), w.Culls.ToString(), Pct(w.CumMortalityPct, 2),
                        Opt(w.AvgWeightGm, "0"), w.StdWeightGm.ToString("0"), N0(w.FeedKg), N0(w.FeedPerBirdGm),
                        Opt(w.CumFcr, "0.000"), w.StdFcr.ToString("0.00"), Opt(w.AvgTempC, "0.0"), w.TargetTempC.ToString("0"), Opt(w.AvgHumidityPct, "0"),
                    }),
                    cellColor: (r, c) =>
                    {
                        var w = weekly[r];
                        return c switch
                        {
                            4 when w.AvgWeightGm is { } wt => ToneVs(wt, (decimal)w.StdWeightGm, true, 5),
                            8 when w.CumFcr is { } f => ToneVs(f, (decimal)w.StdFcr, false, 5),
                            10 when w.AvgTempC is { } t => Math.Abs((double)t - w.TargetTempC) > 3 ? Bad : null,
                            _ => null,
                        };
                    },
                    rightAligned: Enumerable.Range(0, 13).ToArray()));
            }

            // ---------------- Mortality ----------------
            var daily = d.Daily.OrderBy(r => r.Date).ToList();
            if (daily.Count >= 3)
            {
                var losses = daily.Select(r => (double)(r.Mortality + r.Culls)).ToList();
                var avg = losses.Average();
                var colors = losses.Select(v => v > Math.Max(2, avg * 2.5) ? Bad : primary).ToList();
                var spikes = daily.Where(r => r.Mortality + r.Culls > Math.Max(2, avg * 2.5))
                    .OrderByDescending(r => r.Mortality + r.Culls).Take(5).ToList();
                Block(col, T["Daily mortality pattern"], T["Dead + culls per day of age; red bars are spikes (> 2.5x the running average)"], primary, c =>
                {
                    c.Item().Svg(SvgCharts.Bars(daily.Select(r => $"D{BatchAnalytics.AgeOn(b, r.Date)}").ToList(), losses, colors, width: 540, height: 150)).FitWidth();
                    if (spikes.Count > 0)
                        Callout(c, T["Mortality spikes to investigate"],
                            string.Join("\n", spikes.Select(r => $"{Day(BatchAnalytics.AgeOn(b, r.Date))} ({D(r.Date)}): {T["{0} birds", r.Mortality + r.Culls]}" + (string.IsNullOrWhiteSpace(r.Remarks) ? "" : $" - {r.Remarks}"))),
                            Warn, WarnSoft);
                }, keepWith: 200);
            }

            // ---------------- Feed ----------------
            if (d.Feed.Count > 0)
            {
                var byType = d.Feed.GroupBy(f => f.FeedType).Select(g => new[] { T[g.Key], g.Count().ToString(), N0(g.Sum(f => f.Bags)), N0(g.Sum(f => f.TotalKg)), Pct(p.FeedReceivedKg > 0 ? g.Sum(f => f.TotalKg) / p.FeedReceivedKg * 100 : 0) });
                Block(col, T["Feed deliveries"], T["Received {0} kg · consumed {1} kg · balance {2} kg", N0(p.FeedReceivedKg), N0(p.FeedConsumedKg), N0(p.FeedStockKg)], primary, c =>
                {
                    Table(c, primary, new[] { T["Feed Type"], T["Deliveries"], T["Bags"], "Kg", T["Share"] }, new[] { 1.5f, 1, 1, 1, 1 }, byType);
                    if (p.FeedStockKg < 0)
                        Note(c, T["Recorded consumption exceeds recorded deliveries by {0} kg - some feed delivery challans are probably not entered, which also makes FCR unreliable.", N0(-p.FeedStockKg)]);
                });
            }

            // ---------------- Health ----------------
            if (d.Health.Count > 0)
                Block(col, T["Vaccination & medication log"], T["{0} vaccination(s) · health cost {1}", s.VaccinationCount, Rs(s.HealthCost)], primary, c =>
                    Table(c, primary, new[] { T["Date"], T["Age"], T["Type"], T["Name"], T["Dose"], T["Route"], T["Cost"] }, new[] { 1f, 0.5f, 0.9f, 1.4f, 1, 1, 0.8f },
                        d.Health.OrderBy(h => h.Date).Select(h => new[] { D(h.Date), $"D{BatchAnalytics.AgeOn(b, h.Date)}", T[h.Type.ToString()], h.Name, h.Dose, T[h.Route], Rs(h.Cost) }),
                        rightAligned: new[] { 1, 6 }), keepWith: 90);

            // ---------------- Lifting ----------------
            if (d.Liftings.Count > 0)
            {
                var lifts = d.Liftings.OrderBy(l => l.Date).Select(l => new[] { D(l.Date), $"D{BatchAnalytics.AgeOn(b, l.Date)}", N0(l.BirdsLifted), N0(l.TotalWeightKg), l.AvgWeightKg.ToString("0.000"), l.VehicleNumber, l.DcNumber }).ToList();
                lifts.Add(new[] { T["Total"], "", N0(p.BirdsLifted), N0(d.Liftings.Sum(l => l.TotalWeightKg)), s.FinalWeightKg.ToString("0.000"), "", "" });
                Block(col, T["Lifting / sales"], T["{0} birds", N0(p.BirdsLifted)] + $" · {N0(d.Liftings.Sum(l => l.TotalWeightKg))} kg", primary, c =>
                    Table(c, primary, new[] { T["Date"], T["Age"], T["Birds"], "Kg", T["Avg kg"], T["Vehicle"], T["DC no."] }, new[] { 1f, 0.5f, 0.8f, 0.9f, 0.7f, 1, 1 }, lifts,
                        rightAligned: new[] { 1, 2, 3, 4 }, boldRow: r => r == lifts.Count - 1), keepWith: 90);
            }

            // ---------------- Money ----------------
            var money = new List<string[]>();
            if (d.Settlement is { } st)
            {
                money.Add(new[] { T["Growing charge"], $"{Rs2(st.GrowingChargePerKg)} / kg" });
                money.Add(new[] { T["Performance Incentive"], Rs(st.PerformanceIncentive) });
                money.Add(new[] { T["Deductions"], Rs(st.Deductions) });
                money.Add(new[] { T["Income (settlement)"], Rs(s.Income) });
            }
            else money.Add(new[] { T["Income (settlement)"], T["Not settled yet"] });
            foreach (var kv in s.ExpensesByCategory.OrderByDescending(kv => kv.Value))
                money.Add(new[] { T["Expense - {0}", T[kv.Key]], Rs(kv.Value) });
            money.Add(new[] { T["Medicine & vaccine (health log)"], Rs(s.HealthCost) });
            money.Add(new[] { T["Total farm cost"], Rs(s.TotalCost) });
            money.Add(new[] { T["Net Profit"], Rs(s.NetProfit) });
            money.Add(new[] { T["Per bird placed / per kg live"], $"{Rs2(s.ProfitPerBird)} / {Rs2(s.NetProfit is { } n && s.LiveWeightKg > 0 ? n / s.LiveWeightKg : null)}" });
            Block(col, T["Batch economics"], T["Farmer-side income and costs (chicks and feed are supplied by the integrator)"], primary, c =>
                Table(c, primary, new[] { T["Line"], T["Amount"] }, new[] { 2f, 1 }, money,
                    cellColor: (r, cc) => cc == 1 && r == money.Count - 2 && s.NetProfit is { } np2 ? (np2 >= 0 ? Good : Bad) : null,
                    boldRow: r => r >= money.Count - 3), keepWith: 200);

            // ---------------- Advisor ----------------
            // The advisor's messages are written for the on-screen card layout; drop its screen-only phrasing.
            string Clean(string m) => m
                .Replace(" — the factors on the right are the usual causes", "")
                .Replace(" " + T[" — the factors on the right are the usual causes"].Trim(), "");
            var flaggedIdx = advisorEn.Insights
                .Select((i, idx) => (i, idx))
                .Where(x => x.i.Level >= InsightLevel.Info && !x.i.Title.Contains("on target") && !x.i.Title.StartsWith("Not enough") && !x.i.Title.StartsWith("No weight"))
                .Select(x => x.idx).ToHashSet();
            var flagged = advisor.Insights.Where((_, idx) => flaggedIdx.Contains(idx)).ToList();
            var fine = advisor.Insights.Where((_, idx) => !flaggedIdx.Contains(idx)).ToList();
            Block(col, T["Flock advisor findings"], T["Overall score {0:0}/100", advisor.OverallScore] + " · " + string.Join(" · ", advisor.Components.Select(x => $"{T[x.Label]} {x.Score:0}")), primary, c =>
            {
                foreach (var i in flagged)
                {
                    var (tone, soft) = i.Level switch
                    {
                        InsightLevel.Critical => (Bad, BadSoft),
                        InsightLevel.Warning => (Warn, WarnSoft),
                        _ => (primary, Zebra),
                    };
                    Callout(c, $"{T[i.Category]}: {i.Title}", Clean(i.Message) + "\n" + T["Likely causes: {0}.", string.Join("; ", i.Factors.Take(4))], tone, soft);
                }
                if (fine.Count > 0)
                    c.Item().Column(cc => Bullets(cc, fine.Select(i => $"{T[i.Category]}: {Clean(i.Message)}"), 7.5f));
            }, keepWith: 160);

            // ---------------- Lessons ----------------
            var lessons = new List<string>();
            var best = active ? null : others.Where(o => o.Eef > 0).MaxBy(o => o.Eef);
            if (best is not null && best.Eef > s.Eef)
            {
                var diffs = BatchAnalytics.WhatWasDifferent(best, s, T);
                lessons.Add(T["Farm best is {0} (EEF {1:0} vs {2:0} here).", best.Code, best.Eef, s.Eef]
                    + (diffs.Count > 0 ? " " + T["Main differences: {0}.", string.Join("; ", diffs)] : ""));
            }
            else if (best is not null)
                lessons.Add(T["This is the farm's best batch so far by EEF ({0:0}). Document the brooding set-up, lighting and vaccination timing so it can be repeated.", s.Eef]);
            if (s.Day7WeightGm is { } d7 && d7 < 170)
                lessons.Add(T["7-day weight was {0:0} g (target ~180 g). Improve pre-heating, crop fill checks and early feed access.", d7]);
            if (s.FirstWeekMortalityPct > BatchAnalytics.FirstWeekMortalityTargetPct)
                lessons.Add(T["First-week mortality was {0:0.00}% (target < 1%). Record chick quality on arrival and raise it with the hatchery/integrator.", s.FirstWeekMortalityPct]);
            if (s.AvgTempDeviationC is > 3)
                lessons.Add(T["House temperature averaged {0:0.0} °C off the age target. Tighten curtain/fan/brooder control.", s.AvgTempDeviationC]);
            if (s.HouseUtilizationPct is > 100)
                lessons.Add(T["House was filled to {0:0}% of capacity. Place at or below capacity next time.", s.HouseUtilizationPct]);
            foreach (var i in advisor.Insights.Where(i => i.Level >= InsightLevel.Warning).Take(3))
                lessons.Add(T["{0}: check - {1}.", T[i.Category], string.Join("; ", i.Factors.Take(2))]);
            if (lessons.Count == 0) lessons.Add(T["No major gaps found. Keep the same programme and aim to match or beat this result."]);
            Block(col, active ? T["Actions for this batch"] : T["Lessons for the next batch"], null, brand.Accent, c => Bullets(c, lessons), keepWith: 90);

            Signatures(col, T["Farm supervisor"], T["Farm owner"], T["Integrator representative"]);
        });
    }

    private static Kpi WeightVsStandard(PdfText T, BatchSummary s)
    {
        var std = (decimal)FlockAdvisor.TargetWeightGmAt(Math.Max(1, s.CycleDays));
        var actual = s.FinalWeightKg * 1000m;
        if (actual <= 0 || std <= 0) return new(T["Weight vs standard"], "-", T["Std {0:0} g at day {1}", std, s.CycleDays]);
        var pct = actual / std * 100;
        return new(T["Weight vs standard"], $"{pct:0}%", T["{0:0} g vs {1:0} g at day {2}", actual, std, s.CycleDays], ToneVs(actual, std, true, 5));
    }
}
