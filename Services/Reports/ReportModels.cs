using AmrPoultryFarmWeb.Models;

namespace AmrPoultryFarmWeb.Services.Reports;

/// <summary>Everything recorded against one batch, loaded once so every report section reads from
/// the same snapshot instead of re-querying.</summary>
public class BatchDataset
{
    public required Batch Batch { get; init; }
    public required BatchPerformance Perf { get; init; }
    public List<DailyRecord> Daily { get; init; } = new();
    public List<FeedDelivery> Feed { get; init; } = new();
    public List<HealthEvent> Health { get; init; } = new();
    public List<Lifting> Liftings { get; init; } = new();
    public List<Expense> Expenses { get; init; } = new();
    public Settlement? Settlement { get; init; }
}

/// <summary>One week of the cycle, rolled up from daily records and set against the breed benchmark.</summary>
public class WeeklyRow
{
    public int Week { get; init; }
    public int Mortality { get; init; }
    public int Culls { get; init; }
    public decimal CumMortalityPct { get; init; }
    public decimal? AvgWeightGm { get; init; }
    public double StdWeightGm { get; init; }
    public decimal FeedKg { get; init; }
    public decimal FeedPerBirdGm { get; init; }
    public decimal? CumFcr { get; init; }
    public double StdFcr { get; init; }
    public decimal? AvgTempC { get; init; }
    public double TargetTempC { get; init; }
    public decimal? AvgHumidityPct { get; init; }
}

/// <summary>Per-batch KPIs normalised so different batches can be compared fairly — the row type
/// behind every multi-batch report.</summary>
public class BatchSummary
{
    public required BatchDataset Data { get; init; }
    public Batch Batch => Data.Batch;
    public BatchPerformance Perf => Data.Perf;

    public string Code => Batch.BatchCode;
    public string HouseName => Batch.House?.Name ?? "-";
    public string IntegratorName => Batch.Integrator?.Name ?? "-";
    public string Season { get; init; } = "";

    public int CycleDays { get; init; }
    public decimal LivabilityPct => Perf.LivabilityPct;
    public decimal MortalityPct => Perf.MortalityPct;
    public decimal FirstWeekMortalityPct { get; init; }
    public decimal? Day7WeightGm { get; init; }
    public decimal FinalWeightKg { get; init; }
    public decimal Fcr => Perf.Fcr;
    /// <summary>FCR corrected to <see cref="BatchAnalytics.ReferenceWeightKg"/> so heavier and lighter
    /// flocks can be ranked on the same footing.</summary>
    public decimal CorrectedFcr { get; init; }
    public decimal Eef => Perf.Eef;
    public decimal AdgGm { get; init; }
    public decimal FeedPerBirdKg { get; init; }
    public decimal LiveWeightKg => Perf.TotalLiveWeightKg;

    public decimal Expenses { get; init; }
    public decimal HealthCost { get; init; }
    public decimal TotalCost => Expenses + HealthCost;
    public decimal CostPerBird => Batch.ChicksPlaced > 0 ? TotalCost / Batch.ChicksPlaced : 0;
    public decimal CostPerKg => LiveWeightKg > 0 ? TotalCost / LiveWeightKg : 0;
    public decimal? Income { get; init; }
    public decimal? IncomePerKg => Income is { } i && LiveWeightKg > 0 ? i / LiveWeightKg : null;
    public decimal? NetProfit => Income is { } i ? i - TotalCost : null;
    public decimal? ProfitPerBird => NetProfit is { } p && Batch.ChicksPlaced > 0 ? p / Batch.ChicksPlaced : null;

    public decimal? HouseUtilizationPct { get; init; }
    public int VaccinationCount { get; init; }
    public double? AvgTempDeviationC { get; init; }
    public double HealthScore { get; init; }
    public Dictionary<string, decimal> ExpensesByCategory { get; init; } = new();

    public bool HasResult => Fcr > 0;

    /// <summary>Cycle finished (closed, or every bird lifted). Only completed flocks are ranked
    /// against each other — EEF, FCR, weight and livability all depend on age, so a young active
    /// batch would otherwise look artificially "best".</summary>
    public bool IsComplete => HasResult && (Batch.Status == BatchStatus.Closed || (Perf.BirdsLifted > 0 && Perf.LiveBirds == 0));
}

public enum ActionPriority { High = 0, Medium = 1, Low = 2 }

/// <summary>One item in a "what to do differently next batch" plan.</summary>
public record ActionItem(ActionPriority Priority, string Area, string Finding, List<string> Actions, string? Target = null);

/// <summary>A measured link between a management factor and flock outcome across the farm's own batches.</summary>
public record DriverLink(string Driver, string Outcome, double R, int N, string Sentence);

/// <summary>Averages for a group of batches (one house, integrator, season or breed).</summary>
public record GroupStat(string Group, int Batches, decimal Livability, decimal CorrectedFcr, decimal Eef, decimal? ProfitPerBird);

/// <summary>Suggested goals for the next placement, drawn from the farm's own best results.</summary>
public record TargetRow(string Metric, string FarmAverage, string BestAchieved, string NextBatchTarget);
