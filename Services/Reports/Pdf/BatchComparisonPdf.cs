using QuestPDF.Fluent;
using static AmrPoultryFarmWeb.Services.Reports.Pdf.PdfKit;

namespace AmrPoultryFarmWeb.Services.Reports.Pdf;

/// <summary>
/// Multi-batch report built to answer "why was one batch better than another, and what do we change
/// next time": fair ranking (corrected FCR / EEF), best-vs-worst head-to-head, house / season /
/// integrator splits, measured performance drivers, next-batch targets and a prioritised action plan.
/// </summary>
public static class BatchComparisonPdf
{
    private static readonly string[] Palette = { "#0284c7", "#e8a33d", "#a85c32", "#4f46e5", "#7a4e92", "#15803d", "#075985", "#b87621" };

    public static byte[] Render(ReportBranding brand, List<BatchSummary> all)
    {
        var primary = brand.Primary;
        var done = all.Where(b => b.IsComplete).ToList();
        var inProgress = all.Where(b => !b.IsComplete && b.Batch.Status == Models.BatchStatus.Active).ToList();

        return Build(brand, col =>
        {
            if (done.Count == 0)
            {
                Callout(col, "No completed batches to compare yet",
                    "Rankings, targets and the action plan are built from completed (closed or fully lifted) batches only, because EEF, FCR, weight and livability all depend on age. Active batches are shown below against the breed standard for their age.",
                    Warn, WarnSoft);
                InProgress(col, inProgress, primary);
                return;
            }

            var avgEef = done.Average(b => b.Eef);
            var avgCfcr = done.Average(b => b.CorrectedFcr);
            var profits = done.Where(b => b.NetProfit.HasValue).ToList();

            // ---------------- Summary ----------------
            KpiRow(col, new Kpi[]
            {
                new("Batches compared", done.Count.ToString(), inProgress.Count > 0 ? $"completed · {inProgress.Count} in progress" : "completed"),
                new("Birds placed", N0(done.Sum(b => b.Batch.ChicksPlaced)), $"{N0(done.Sum(b => b.Perf.BirdsLifted))} lifted"),
                new("Avg livability", Pct(done.Average(b => b.LivabilityPct)), "Target 96%+", ToneVs(done.Average(b => b.LivabilityPct), 96, true, 1)),
                new("Avg corrected FCR", avgCfcr.ToString("0.000"), $"Best {done.Min(b => b.CorrectedFcr):0.000}"),
                new("Avg EEF", avgEef.ToString("0"), $"Best {done.Max(b => b.Eef):0}", ToneVs(avgEef, 350, true, 10)),
                new("Net profit", Rs(profits.Sum(b => b.NetProfit!.Value)), $"{profits.Count} settled batch(es)"),
            }, primary);

            // ---------------- Ranking ----------------
            Section(col, "Batch ranking", "Sorted by EEF (European Efficiency Factor) - combines livability, weight, age and FCR in one number", primary);
            var ranked = done.OrderByDescending(b => b.Eef).ToList();
            Table(col, primary,
                new[] { "#", "Batch", "House", "Season", "Placed", "Days", "Livab.", "Wk-1 mort.", "D7 wt g", "Final kg", "FCR", "Corr. FCR", "EEF", "Cost/kg", "Profit/bird", "Score" },
                new[] { 0.35f, 1.3f, 1.1f, 0.9f, 0.9f, 0.55f, 0.75f, 0.8f, 0.75f, 0.75f, 0.7f, 0.8f, 0.6f, 0.8f, 0.9f, 0.6f },
                ranked.Select((b, i) => new[]
                {
                    (i + 1).ToString(), b.Code, b.HouseName, b.Season, N0(b.Batch.ChicksPlaced), b.CycleDays.ToString(),
                    Pct(b.LivabilityPct), Pct(b.FirstWeekMortalityPct, 2), Opt(b.Day7WeightGm, "0"), b.FinalWeightKg.ToString("0.000"),
                    b.Fcr.ToString("0.000"), b.CorrectedFcr.ToString("0.000"), b.Eef.ToString("0"), b.CostPerKg > 0 ? b.CostPerKg.ToString("0.00") : "-",
                    Rs2(b.ProfitPerBird), b.HealthScore.ToString("0"),
                }),
                cellColor: (r, c) =>
                {
                    var b = ranked[r];
                    return c switch
                    {
                        6 => ToneVs(b.LivabilityPct, 96, true, 1),
                        7 => b.FirstWeekMortalityPct > BatchAnalytics.FirstWeekMortalityTargetPct ? Bad : Good,
                        8 when b.Day7WeightGm is { } w => ToneVs(w, BatchAnalytics.Day7WeightTargetGm, true, 5),
                        11 => ToneVs(b.CorrectedFcr, avgCfcr, false, 2),
                        12 => ToneVs(b.Eef, avgEef, true, 3),
                        14 when b.ProfitPerBird is { } pp => pp >= 0 ? Good : Bad,
                        _ => null,
                    };
                },
                rightAligned: new[] { 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 });
            Note(col, $"Corrected FCR = FCR + ({BatchAnalytics.ReferenceWeightKg:0.0} kg - final weight) x 0.30, so heavier and lighter flocks rank fairly. Colours for corrected FCR and EEF are relative to the average of these batches; livability, week-1 mortality and D7 weight are against industry targets (96%, < 1%, 180 g).");

            InProgress(col, inProgress, primary);

            // ---------------- Trend charts ----------------
            var chrono = done.OrderBy(b => b.Batch.PlacementDate).ToList();
            col.Item().ShowEntire().Row(row =>
            {
                row.Spacing(12);
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("EEF by batch (placement order)").Bold().FontSize(9);
                    c.Item().Svg(SvgCharts.Bars(chrono.Select(b => b.Code).ToList(), chrono.Select(b => (double)b.Eef).ToList(),
                        chrono.Select(b => b.Eef >= avgEef ? primary : Warn).ToList(), (double)avgEef, $"avg {avgEef:0}", 380, 170)).FitWidth();
                });
                row.RelativeItem().Column(c =>
                {
                    var minY = Math.Max(0, (double)chrono.Min(b => b.CorrectedFcr) - 0.2);
                    c.Item().Text("Corrected FCR by batch (lower is better)").Bold().FontSize(9);
                    c.Item().Svg(SvgCharts.Bars(chrono.Select(b => b.Code).ToList(), chrono.Select(b => (double)b.CorrectedFcr).ToList(),
                        chrono.Select(b => b.CorrectedFcr <= avgCfcr ? primary : Warn).ToList(), (double)avgCfcr, $"avg {avgCfcr:0.000}", 380, 170, "0.00", minY)).FitWidth();
                });
            });

            // ---------------- Growth overlay ----------------
            // Growth is compared week-for-week, so active batches can be overlaid fairly too.
            var recent = done.Concat(inProgress).OrderBy(b => b.Batch.PlacementDate).TakeLast(6).Select(b => (b, Weekly: BatchAnalytics.Weekly(b.Data))).Where(x => x.Weekly.Any(w => w.AvgWeightGm.HasValue)).ToList();
            if (recent.Count >= 2)
            {
                int maxWeek = recent.Max(x => x.Weekly.Max(w => w.Week));
                var labels = Enumerable.Range(1, maxWeek).Select(w => $"Wk {w}").ToList();
                var series = recent.Select((x, i) => new SvgCharts.Series(x.b.Code, Palette[i % Palette.Length],
                    Enumerable.Range(1, maxWeek).Select(w => (double?)x.Weekly.FirstOrDefault(r => r.Week == w)?.AvgWeightGm).ToList())).ToList();
                series.Add(new SvgCharts.Series("Standard", "#8aa0ae", Enumerable.Range(1, maxWeek).Select(w => (double?)FlockAdvisor.TargetWeightGmAt(w * 7)).ToList(), Dashed: true));
                col.Item().ShowEntire().Column(c =>
                {
                    Section(c, "Growth curves side by side", "End-of-week sampled body weight (g) for the most recent batches", primary);
                    c.Item().PaddingTop(4).Svg(SvgCharts.Lines(labels, series, 780, 200)).FitWidth();
                });
            }

            // ---------------- Best vs worst ----------------
            if (done.Count >= 2)
            {
                var best = ranked[0];
                var worst = ranked[^1];
                var h2h = new List<(string Metric, string Best, string Worst, string Gap, string Verdict)>();
                // Outcome rows show the size of the gap; factor rows are flagged when they plausibly explain it.
                void Row(string m, decimal bv, decimal wv, string fmt, bool higherBetter, decimal flagAt, string suffix = "", bool outcome = false)
                {
                    var gap = bv - wv;
                    bool flag = (higherBetter ? gap : -gap) >= flagAt;
                    h2h.Add((m, bv.ToString(fmt) + suffix, wv.ToString(fmt) + suffix, (gap >= 0 ? "+" : "") + gap.ToString(fmt) + suffix,
                        outcome ? "Outcome" : flag ? "Likely cause" : ""));
                }
                if (best.Day7WeightGm is { } b7 && worst.Day7WeightGm is { } w7) Row("7-day weight", b7, w7, "0", true, 10, " g");
                Row("First-week mortality", best.FirstWeekMortalityPct, worst.FirstWeekMortalityPct, "0.00", false, 0.3m, "%");
                Row("Livability", best.LivabilityPct, worst.LivabilityPct, "0.0", true, 1, "%");
                Row("Cycle length", best.CycleDays, worst.CycleDays, "0", false, 3, " d");
                if (best.HouseUtilizationPct is { } bu && worst.HouseUtilizationPct is { } wu) Row("House fill", bu, wu, "0", false, 5, "%");
                Row("Vaccinations logged", best.VaccinationCount, worst.VaccinationCount, "0", true, 1);
                if (best.AvgTempDeviationC is { } bt && worst.AvgTempDeviationC is { } wt) Row("Temp. off target", (decimal)bt, (decimal)wt, "0.0", false, 1, " °C");
                Row("Feed per bird", best.FeedPerBirdKg, worst.FeedPerBirdKg, "0.00", false, 0.1m, " kg");
                Row("Final weight", best.FinalWeightKg, worst.FinalWeightKg, "0.000", true, 0.1m, " kg", outcome: true);
                Row("Avg daily gain", best.AdgGm, worst.AdgGm, "0.0", true, 3, " g", outcome: true);
                Row("Corrected FCR", best.CorrectedFcr, worst.CorrectedFcr, "0.000", false, 0.05m, outcome: true);
                Row("EEF", best.Eef, worst.Eef, "0", true, 20, outcome: true);
                Row("Farm cost per kg", best.CostPerKg, worst.CostPerKg, "0.00", false, 0.5m, outcome: true);
                Block(col, $"Head to head: best ({best.Code}) vs weakest ({worst.Code})", "Early-cycle factors first, then the outcomes they produced", primary, c =>
                {
                    Table(c, primary, new[] { "Metric", best.Code, worst.Code, "Difference", "Role" }, new[] { 1.6f, 1, 1, 1, 0.9f },
                        h2h.Select(x => new[] { x.Metric, x.Best, x.Worst, x.Gap, x.Verdict }),
                        cellColor: (r, cc) => cc == 4 ? (h2h[r].Verdict == "Likely cause" ? Bad : h2h[r].Verdict == "Outcome" ? InkSoft : null) : null);
                    c.Item().Text($"Context: {best.Code} - {best.HouseName}, {best.Season}, {best.IntegratorName}, placed {D(best.Batch.PlacementDate)}.   {worst.Code} - {worst.HouseName}, {worst.Season}, {worst.IntegratorName}, placed {D(worst.Batch.PlacementDate)}.").FontSize(7.5f).FontColor(InkSoft);
                }, keepWith: 200);
            }

            // ---------------- Group splits ----------------
            var splits = new (string Title, List<GroupStat> Stats)[]
            {
                ("By house", BatchAnalytics.GroupBy(done, b => b.HouseName)),
                ("By season of placement", BatchAnalytics.GroupBy(done, b => b.Season)),
                ("By integrator", BatchAnalytics.GroupBy(done, b => b.IntegratorName)),
                ("By breed", BatchAnalytics.GroupBy(done, b => b.Batch.Breed)),
            }.Where(x => x.Stats.Count >= 2).ToList();
            if (splits.Count > 0)
            {
                Block(col, "Where results differ", "Averages per group - look for a group that is consistently behind", primary, gc =>
                {
                foreach (var chunk in splits.Chunk(2))
                {
                    gc.Item().ShowEntire().Row(row =>
                    {
                        row.Spacing(12);
                        foreach (var (title, stats) in chunk)
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().PaddingBottom(2).Text(title).Bold().FontSize(8.5f);
                                Table(c, primary, new[] { "Group", "Batches", "Livab.", "Corr. FCR", "EEF", "Profit/bird" }, new[] { 1.6f, 0.7f, 0.8f, 0.9f, 0.7f, 1 },
                                    stats.Select(g => new[] { g.Group, g.Batches.ToString(), Pct(g.Livability), g.CorrectedFcr.ToString("0.000"), g.Eef.ToString("0"), Rs2(g.ProfitPerBird) }),
                                    cellColor: (r, cc) => cc == 4 ? (r == 0 ? Good : r == stats.Count - 1 ? Bad : null) : null);
                            });
                        if (chunk.Length == 1) row.RelativeItem();
                    });
                }
                }, keepWith: 150);
            }

            // ---------------- Drivers ----------------
            var drivers = BatchAnalytics.Drivers(done);
            Block(col, "What drives results on this farm", "Measured from your own batch history (correlation with EEF)", primary, c =>
            {
                if (done.Count < 4)
                    Note(c, "At least 4 completed batches are needed to measure drivers. This section fills in automatically as more batches close.");
                else if (drivers.Count == 0)
                    Note(c, "No single factor shows a clear link with EEF yet. Results vary for mixed reasons; keep recording weights, temperatures and vaccinations consistently.");
                else
                {
                    Bullets(c, drivers.Select(dl => dl.Sentence));
                    Note(c, "Correlation shows factors that move together with results on this farm; it is a strong hint where to look, not proof of cause. |r| of 0.7+ is strong, 0.4-0.7 moderate.");
                }
            }, keepWith: 90);

            // ---------------- Targets ----------------
            var targets = BatchAnalytics.Targets(done);
            if (targets.Count > 0)
            {
                col.Item().ShowEntire().Column(c =>
                {
                    Section(c, "Targets for the next batch", "Set from your own top-quartile results, never looser than the industry benchmark", brand.Accent);
                    Table(c, brand.Accent, new[] { "Metric", "Farm average", "Best achieved", "Next batch target" }, new[] { 1.6f, 1, 1, 1.1f },
                        targets.Select(t => new[] { t.Metric, t.FarmAverage, t.BestAchieved, t.NextBatchTarget }),
                        cellColor: (r, cc) => cc == 3 ? Good : null);
                });
            }

            // ---------------- Action plan ----------------
            Block(col, "Next-batch action plan", "Prioritised from the gaps above", brand.Accent, ap =>
            {
            foreach (var item in BatchAnalytics.ActionPlan(done))
            {
                var (tone, soft, label) = item.Priority switch
                {
                    ActionPriority.High => (Bad, BadSoft, "HIGH"),
                    ActionPriority.Medium => (Warn, WarnSoft, "MEDIUM"),
                    _ => (Good, GoodSoft, "KEEP"),
                };
                ap.Item().ShowEntire().BorderLeft(3).BorderColor(tone).Background(soft).Padding(8).Column(c =>
                {
                    c.Item().Row(r =>
                    {
                        r.AutoItem().Background(tone).PaddingHorizontal(4).PaddingVertical(1).Text(label).FontSize(6.5f).Bold().FontColor("#ffffff");
                        r.ConstantItem(6);
                        r.RelativeItem().Text(item.Area).Bold().FontColor(tone);
                        if (item.Target is not null) r.AutoItem().Text($"Target: {item.Target}").FontSize(7.5f).Bold();
                    });
                    c.Item().PaddingTop(3).Text(item.Finding).FontSize(8);
                    c.Item().PaddingTop(2).Column(a => Bullets(a, item.Actions, 7.8f));
                });
            }
            }, keepWith: 150);

            Signatures(col, "Farm supervisor", "Farm owner", "Integrator field officer");
        }, landscape: true);
    }

    /// <summary>Active batches judged against the breed standard for their current age — the only
    /// fair yardstick mid-cycle.</summary>
    private static void InProgress(ColumnDescriptor col, List<BatchSummary> active, string primary)
    {
        if (active.Count == 0) return;
        var rows = active.OrderBy(b => b.Batch.PlacementDate).Select(b =>
        {
            int age = Math.Max(1, b.Perf.AgeDays);
            var wt = b.Perf.AvgBodyWeightKg * 1000m;
            return (b, age, wt, StdWt: (decimal)FlockAdvisor.TargetWeightGmAt(age), StdFcr: (decimal)FlockAdvisor.TargetFcrAt(age));
        }).ToList();
        Block(col, "Batches in progress vs standard", "Judged against the breed standard for their current age, not against completed batches", primary, c => Table(c, primary,
            new[] { "Batch", "House", "Day", "Live birds", "Livab.", "Wk-1 mort.", "D7 wt g", "Weight g", "Std g", "FCR", "Std FCR", "Advisor" },
            new[] { 1.3f, 1.1f, 0.5f, 0.9f, 0.7f, 0.8f, 0.7f, 0.8f, 0.7f, 0.7f, 0.7f, 0.7f },
            rows.Select(r => new[]
            {
                r.b.Code, r.b.HouseName, r.age.ToString(), N0(r.b.Perf.LiveBirds), Pct(r.b.LivabilityPct), Pct(r.b.FirstWeekMortalityPct, 2),
                Opt(r.b.Day7WeightGm, "0"), r.wt > 0 ? r.wt.ToString("0") : "-", r.StdWt.ToString("0"),
                r.b.Fcr > 0 ? r.b.Fcr.ToString("0.000") : "-", r.StdFcr.ToString("0.00"), $"{r.b.HealthScore:0}/100",
            }),
            cellColor: (i, c) =>
            {
                var r = rows[i];
                return c switch
                {
                    5 => r.b.FirstWeekMortalityPct > BatchAnalytics.FirstWeekMortalityTargetPct ? Bad : Good,
                    6 when r.b.Day7WeightGm is { } w => ToneVs(w, BatchAnalytics.Day7WeightTargetGm, true, 5),
                    7 when r.wt > 0 => ToneVs(r.wt, r.StdWt, true, 5),
                    9 when r.b.Fcr > 0 => ToneVs(r.b.Fcr, r.StdFcr, false, 5),
                    11 => r.b.HealthScore >= 85 ? Good : r.b.HealthScore >= 65 ? Warn : Bad,
                    _ => null,
                };
            },
            rightAligned: Enumerable.Range(2, 10).ToArray()), keepWith: 90);
    }
}
