using AmrPoultryFarmWeb.Models;

namespace AmrPoultryFarmWeb.Services;

/// <summary>
/// Projects a live batch's income from its current condition plus a handful of farmer-entered
/// assumptions (growing charge, expected sale weight/age). Pure calculation, no persistence — the
/// Revenue tab recomputes it live as the farmer adjusts an input.
/// </summary>
public static class RevenueCalculator
{
    /// <summary>Sensible starting assumptions seeded from the batch's own data, so the calculator
    /// shows a plausible projection before the farmer changes anything.</summary>
    public static RevenueCalcInput DefaultInput(Batch batch, BatchPerformance perf, Settlement? settlement)
    {
        int projectedAgeDays = Math.Max(perf.AgeDays, 35);
        int remainingDays = Math.Max(0, projectedAgeDays - perf.AgeDays);

        decimal projectedWeightKg = perf.AvgBodyWeightKg > 0 && perf.DailyGainGm > 0
            ? perf.AvgBodyWeightKg + (perf.DailyGainGm * remainingDays / 1000m)
            : batch.TargetWeightKg;
        if (projectedWeightKg <= 0) projectedWeightKg = batch.TargetWeightKg > 0 ? batch.TargetWeightKg : 2.3m;

        // The batch's current cumulative FCR is naturally low early in the cycle (feed intake
        // accelerates faster than weight gain as birds age), so it understates feed still needed
        // for a young batch. Default to the age-appropriate benchmark for the projected sale age
        // instead — sell-today projections still land close to the batch's own current FCR since
        // that's what the benchmark itself approximates around the current age.
        decimal benchmarkFcr = (decimal)FlockAdvisor.TargetFcrAt(projectedAgeDays);

        return new RevenueCalcInput
        {
            ProjectedSaleAgeDays = projectedAgeDays,
            ProjectedAvgWeightKg = Math.Round(projectedWeightKg, 3),
            ExpectedFcr = Math.Round(benchmarkFcr, 3),
            AdditionalMortalityPct = 0,
            GrowingChargePerKg = settlement is { GrowingChargePerKg: > 0 } ? settlement.GrowingChargePerKg : 8m,
            ExpectedIncentive = settlement?.PerformanceIncentive ?? 0,
            ExpectedDeductions = settlement?.Deductions ?? 0,
            AdditionalExpectedExpenses = 0,
        };
    }

    public static RevenueCalcResult Calculate(BatchPerformance perf, RevenueCalcInput input)
    {
        int projectedLiveBirds = Math.Max(0, (int)Math.Round(perf.LiveBirds * (1 - input.AdditionalMortalityPct / 100m)));
        decimal projectedTotalWeightKg = projectedLiveBirds * Math.Max(0, input.ProjectedAvgWeightKg);

        // Feed is supplied by the integrator free of cost — this is a planning figure (how much
        // more feed to expect/order), not a cost line.
        decimal totalFeedRequiredKg = Math.Max(0, input.ExpectedFcr) * projectedTotalWeightKg;
        decimal remainingFeedKg = Math.Max(0, totalFeedRequiredKg - perf.FeedConsumedKg);

        decimal additionalExpectedExpenses = Math.Max(0, input.AdditionalExpectedExpenses);
        decimal totalCost = perf.TotalExpenses + additionalExpectedExpenses;

        decimal grossRevenue = projectedTotalWeightKg * input.GrowingChargePerKg + input.ExpectedIncentive - input.ExpectedDeductions;
        decimal netProfit = grossRevenue - totalCost;

        return new RevenueCalcResult
        {
            ProjectedLiveBirds = projectedLiveBirds,
            ProjectedTotalWeightKg = Math.Round(projectedTotalWeightKg, 1),
            TotalFeedRequiredKg = Math.Round(totalFeedRequiredKg, 0),
            RemainingFeedKg = Math.Round(remainingFeedKg, 0),
            ExpensesSoFar = Math.Round(perf.TotalExpenses, 0),
            AdditionalExpectedExpenses = Math.Round(additionalExpectedExpenses, 0),
            TotalCost = Math.Round(totalCost, 0),
            GrossRevenue = Math.Round(grossRevenue, 0),
            NetProfit = Math.Round(netProfit, 0),
            ProfitPerBird = projectedLiveBirds > 0 ? Math.Round(netProfit / projectedLiveBirds, 2) : 0,
            ProfitMarginPct = grossRevenue > 0 ? Math.Round(netProfit / grossRevenue * 100, 1) : 0,
        };
    }
}
