using AmrPoultryFarmWeb.Models;

namespace AmrPoultryFarmWeb.Components.Shared.Charts;

/// <summary>
/// Turns a batch's daily records into a cumulative-FCR-by-day series, using the same
/// feed/liveweight formula as FarmService.GetPerformanceAsync but evaluated at each day's
/// cursor instead of only the latest one. Days with no weight sample are skipped since FCR
/// isn't meaningful without one.
/// </summary>
public static class FcrTrend
{
    public static List<LineChartPoint> Compute(Batch batch, IEnumerable<DailyRecord> dailyRecords)
    {
        var points = new List<LineChartPoint>();
        decimal cumFeedKg = 0;
        int cumMortality = 0, cumCulls = 0;

        foreach (var d in dailyRecords.OrderBy(d => d.Date))
        {
            cumFeedKg += d.FeedConsumedKg;
            cumMortality += d.Mortality;
            cumCulls += d.Culls;

            var liveBirds = Math.Max(0, batch.ChicksPlaced - cumMortality - cumCulls);
            var totalWeightKg = liveBirds * (d.AvgBodyWeightGm / 1000m);
            if (totalWeightKg <= 0) continue;

            var fcr = cumFeedKg / totalWeightKg;
            points.Add(new LineChartPoint(d.Date.ToString("dd MMM"), (double)fcr));
        }

        return points;
    }
}
