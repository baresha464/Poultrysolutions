using AmrPoultryFarmWeb.Models;
using QuestPDF.Fluent;
using static AmrPoultryFarmWeb.Services.Reports.Pdf.PdfKit;

namespace AmrPoultryFarmWeb.Services.Reports.Pdf;

/// <summary>
/// Farmer-side money view across batches: batch P&amp;L, cost per bird / per kg, a category-by-batch
/// cost matrix that shows where spend drifted, incentives vs deductions, and general farm overheads.
/// </summary>
public static class FinancialPdf
{
    public static byte[] Render(ReportBranding brand, List<BatchSummary> batches, List<Expense> generalExpenses)
    {
        var primary = brand.Primary;
        var list = batches.OrderBy(b => b.Batch.PlacementDate).ToList();

        return Build(brand, col =>
        {
            if (list.Count == 0)
            {
                Callout(col, "No batches in this selection", "Change the house/year filter on the Reports page and download again.", Warn, WarnSoft);
                return;
            }

            var settled = list.Where(b => b.Income.HasValue).ToList();
            decimal income = settled.Sum(b => b.Income!.Value);
            decimal settledCost = settled.Sum(b => b.TotalCost);
            decimal overhead = generalExpenses.Sum(e => e.Amount);
            decimal net = income - settledCost - overhead;
            decimal liveKg = settled.Sum(b => b.LiveWeightKg);
            decimal birds = settled.Sum(b => b.Batch.ChicksPlaced);

            KpiRow(col, new Kpi[]
            {
                new("Settled income", Rs(income), $"{settled.Count} of {list.Count} batches settled"),
                new("Batch costs (settled)", Rs(settledCost), liveKg > 0 ? $"{Rs2(settledCost / liveKg)} per kg live" : null),
                new("Farm overheads", Rs(overhead), "Expenses not linked to a batch"),
                new("Net profit", Rs(net), "Income - batch costs - overheads", net >= 0 ? Good : Bad),
                new("Income per kg", liveKg > 0 ? Rs2(income / liveKg) : "-", "Incl. incentives & deductions"),
                new("Net per bird", birds > 0 ? Rs2(net / birds) : "-", income > 0 ? $"Margin {net / income * 100:0.0}%" : null, net >= 0 ? Good : Bad),
            }, primary);

            // ---------------- P&L per batch ----------------
            Section(col, "Profit & loss by batch", "Farmer-side view: chicks and feed are supplied by the integrator and paid for through the growing charge", primary);
            var rows = list.Select(b => new[]
            {
                b.Code, b.HouseName, b.Batch.Status == BatchStatus.Closed ? "Closed" : "Active", N0(b.LiveWeightKg),
                b.Data.Settlement is { } s1 ? s1.GrowingChargePerKg.ToString("0.00") : "-",
                b.Data.Settlement is { } s2 ? N0(s2.PerformanceIncentive) : "-",
                b.Data.Settlement is { } s3 ? N0(s3.Deductions) : "-",
                Rs(b.Income), Rs(b.Expenses), Rs(b.HealthCost), Rs(b.TotalCost), Rs(b.NetProfit), Rs2(b.ProfitPerBird),
                b.CostPerKg > 0 ? b.CostPerKg.ToString("0.00") : "-",
                b.Income is { } i && i > 0 ? Pct(b.NetProfit!.Value / i * 100) : "-",
            }).ToList();
            rows.Add(new[]
            {
                "Total", "", "", N0(list.Sum(b => b.LiveWeightKg)), "", N0(settled.Sum(b => b.Data.Settlement!.PerformanceIncentive)), N0(settled.Sum(b => b.Data.Settlement!.Deductions)),
                Rs(income), Rs(list.Sum(b => b.Expenses)), Rs(list.Sum(b => b.HealthCost)), Rs(list.Sum(b => b.TotalCost)), Rs(income - settledCost), birds > 0 ? Rs2((income - settledCost) / birds) : "-", "", "",
            });
            Table(col, primary,
                new[] { "Batch", "House", "Status", "Live kg", "Rs/kg", "Incentive", "Deduct.", "Income", "Expenses", "Health", "Total cost", "Net profit", "Per bird", "Cost/kg", "Margin" },
                new[] { 1.2f, 1, 0.7f, 0.9f, 0.6f, 0.8f, 0.7f, 1, 1, 0.8f, 1, 1, 0.8f, 0.7f, 0.7f },
                rows,
                cellColor: (r, c) => r < list.Count && c == 11 && list[r].NetProfit is { } np ? (np >= 0 ? Good : Bad) : null,
                rightAligned: Enumerable.Range(3, 12).ToArray(),
                boldRow: r => r == rows.Count - 1);
            Note(col, "Net profit and per-bird figures appear once a settlement is entered. The total row's net profit covers settled batches only and excludes farm overheads.");

            // Cost comparisons use finished/settled batches only: an active batch has paid most of its
            // brooding costs but grown little weight yet, so its cost per kg/bird looks falsely high.
            var complete = list.Where(b => b.IsComplete || b.Income.HasValue).ToList();

            // ---------------- Charts ----------------
            var withProfit = list.Where(b => b.ProfitPerBird.HasValue).ToList();
            var withCost = complete.Where(b => b.CostPerKg > 0).ToList();
            if (withProfit.Count > 0 || withCost.Count > 0)
                col.Item().ShowEntire().Row(row =>
                {
                    row.Spacing(12);
                    if (withProfit.Count > 0)
                        row.RelativeItem().Column(c =>
                        {
                            var avg = withProfit.Average(b => (double)b.ProfitPerBird!.Value);
                            c.Item().Text("Net profit per bird placed (Rs)").Bold().FontSize(9);
                            c.Item().Svg(SvgCharts.Bars(withProfit.Select(b => b.Code).ToList(), withProfit.Select(b => Math.Max(0, (double)b.ProfitPerBird!.Value)).ToList(),
                                withProfit.Select(b => b.ProfitPerBird!.Value < 0 ? Bad : (double)b.ProfitPerBird.Value >= avg ? Good : Warn).ToList(), avg, $"avg {avg:0.00}", 380, 170, "0.00")).FitWidth();
                        });
                    if (withCost.Count > 0)
                        row.RelativeItem().Column(c =>
                        {
                            var avg = withCost.Average(b => (double)b.CostPerKg);
                            c.Item().Text("Farm cost per kg live weight, completed batches (Rs, lower is better)").Bold().FontSize(9);
                            c.Item().Svg(SvgCharts.Bars(withCost.Select(b => b.Code).ToList(), withCost.Select(b => (double)b.CostPerKg).ToList(),
                                withCost.Select(b => (double)b.CostPerKg <= avg ? primary : Warn).ToList(), avg, $"avg {avg:0.00}", 380, 170, "0.00")).FitWidth();
                        });
                });

            // ---------------- Cost matrix ----------------
            var categories = complete.SelectMany(b => b.ExpensesByCategory.Keys).Distinct().OrderBy(c => c).ToList();
            if (categories.Count > 0)
            {
                var cats = categories.Concat(new[] { "Medicine & vaccine" }).ToList();
                decimal PerBird(BatchSummary b, string cat) =>
                    b.Batch.ChicksPlaced <= 0 ? 0 : (cat == "Medicine & vaccine" ? b.HealthCost : b.ExpensesByCategory.GetValueOrDefault(cat)) / b.Batch.ChicksPlaced;
                var avgs = cats.ToDictionary(c => c, c => complete.Average(b => PerBird(b, c)));

                var shown = complete.TakeLast(10).ToList();
                var headers = new[] { "Category" }.Concat(shown.Select(b => b.Code)).Concat(new[] { "Average" }).ToArray();
                var widths = new[] { 1.5f }.Concat(shown.Select(_ => 0.8f)).Concat(new[] { 0.8f }).ToArray();
                var matrix = cats.Select(c => new[] { c }.Concat(shown.Select(b => PerBird(b, c).ToString("0.00"))).Concat(new[] { avgs[c].ToString("0.00") }).ToArray()).ToList();
                matrix.Add(new[] { "Total per bird" }.Concat(shown.Select(b => b.CostPerBird.ToString("0.00"))).Concat(new[] { complete.Average(b => b.CostPerBird).ToString("0.00") }).ToArray());
                Block(col, "Cost per bird placed, by category", "Completed batches only. Red = more than 20% above this category's average - the first place to look for savings", primary, mc =>
                {
                    Table(mc, primary, headers, widths, matrix,
                        cellColor: (r, c) =>
                        {
                            if (r >= cats.Count || c == 0 || c > shown.Count) return null;
                            var v = PerBird(shown[c - 1], cats[r]);
                            var a = avgs[cats[r]];
                            return a > 0 && v > a * 1.2m ? Bad : null;
                        },
                        boldRow: r => r == matrix.Count - 1);
                    if (complete.Count > shown.Count) Note(mc, $"Showing the latest {shown.Count} batches; averages use all {complete.Count}.");
                }, keepWith: 150);

                // Category share & drift
                var totals = cats.Select(c => (Cat: c, Total: complete.Sum(b => c == "Medicine & vaccine" ? b.HealthCost : b.ExpensesByCategory.GetValueOrDefault(c))))
                    .Where(x => x.Total > 0).OrderByDescending(x => x.Total).ToList();
                var grand = totals.Sum(x => x.Total);
                if (grand > 0)
                {
                    Block(col, "Where the money goes", null, primary, wc => wc.Item().Row(row =>
                    {
                        row.Spacing(12);
                        row.RelativeItem().Column(c => Table(c, primary, new[] { "Category", "Total", "Share", "Per bird (avg)" }, new[] { 1.5f, 1, 0.7f, 0.9f },
                            totals.Select(x => new[] { x.Cat, Rs(x.Total), Pct(x.Total / grand * 100), avgs[x.Cat].ToString("0.00") })));
                        row.RelativeItem().Column(c =>
                        {
                            var findings = new List<string>();
                            foreach (var x in totals.Take(3))
                                findings.Add($"{x.Cat} is {x.Total / grand * 100:0}% of batch costs (avg Rs {avgs[x.Cat]:0.00} per bird).");
                            if (complete.Count >= 3)
                            {
                                var latest = complete[^1];
                                var drift = cats.Select(ct => (ct, Delta: PerBird(latest, ct) - avgs[ct]))
                                    .Where(x => x.Delta > 0.1m).OrderByDescending(x => x.Delta).Take(2).ToList();
                                foreach (var (ct, delta) in drift)
                                    findings.Add($"Latest batch {latest.Code} spent Rs {delta:0.00} per bird more than average on {ct}.");
                            }
                            var ded = settled.Where(b => b.Data.Settlement!.Deductions > 0).ToList();
                            if (ded.Count > 0)
                                findings.Add($"Integrator deductions totalled {Rs(ded.Sum(b => b.Data.Settlement!.Deductions))} across {ded.Count} batch(es) - usually for mortality or FCR above norm. Reducing week-1 losses and FCR recovers this directly.");
                            var inc = settled.Where(b => b.Data.Settlement!.PerformanceIncentive > 0).ToList();
                            if (inc.Count > 0)
                                findings.Add($"Incentives earned: {Rs(inc.Sum(b => b.Data.Settlement!.PerformanceIncentive))} across {inc.Count} batch(es); best-FCR batches earn the most.");
                            var losses = settled.Where(b => b.NetProfit < 0).ToList();
                            if (losses.Count > 0)
                                findings.Add($"Loss-making batches: {string.Join(", ", losses.Select(b => $"{b.Code} ({Rs(b.NetProfit)})"))}.");
                            c.Item().Text("Findings").Bold().FontSize(9);
                            Bullets(c, findings);
                        });
                    }), keepWith: 150);
                }
            }

            // ---------------- Overheads ----------------
            if (generalExpenses.Count > 0)
            {
                Block(col, "Farm overheads (not linked to a batch)", birds > 0 ? $"Spread over settled birds: {Rs2(overhead / birds)} per bird" : null, primary, oc =>
                    Table(oc, primary, new[] { "Category", "Entries", "Amount", "Share" }, new[] { 1.5f, 0.7f, 1, 0.7f },
                        generalExpenses.GroupBy(e => e.Category).OrderByDescending(g => g.Sum(e => e.Amount))
                            .Select(g => new[] { g.Key, g.Count().ToString(), Rs(g.Sum(e => e.Amount)), Pct(overhead > 0 ? g.Sum(e => e.Amount) / overhead * 100 : 0) })), keepWith: 90);
            }

            Signatures(col, "Prepared by", "Checked by", "Farm owner");
        }, landscape: true);
    }
}
