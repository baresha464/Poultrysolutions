namespace AmrPoultryFarmWeb.Models;

/// <summary>Farmer-entered assumptions used to project a live batch's income before it is
/// actually lifted/settled. Not persisted — recomputed on the fly in the Revenue tab.
///
/// This app's batches are integrator contract-grown (see <see cref="Batch.Integrator"/>,
/// <see cref="Settlement"/>): the integrator supplies the chicks and feed free of cost and pays a
/// growing charge per kg of live weight produced, so chick and feed cost are not the farmer's
/// expense — the farmer's real cost is their own running <see cref="Expense"/> entries (labour,
/// electricity, litter, diesel...). Feed quantity is still projected below, but only as a planning
/// figure (how much more to expect/order from the integrator), never as a cost.</summary>
public class RevenueCalcInput
{
    public int ProjectedSaleAgeDays { get; set; }
    public decimal ProjectedAvgWeightKg { get; set; }
    public decimal ExpectedFcr { get; set; }
    public decimal AdditionalMortalityPct { get; set; }
    public decimal GrowingChargePerKg { get; set; } = 8m;
    public decimal ExpectedIncentive { get; set; }
    public decimal ExpectedDeductions { get; set; }
    public decimal AdditionalExpectedExpenses { get; set; }
}

/// <summary>Computed projection from <see cref="Services.RevenueCalculator"/> for the current
/// RevenueCalcInput assumptions.</summary>
public class RevenueCalcResult
{
    public int ProjectedLiveBirds { get; set; }
    public decimal ProjectedTotalWeightKg { get; set; }
    public decimal TotalFeedRequiredKg { get; set; }
    public decimal RemainingFeedKg { get; set; }
    public decimal ExpensesSoFar { get; set; }
    public decimal AdditionalExpectedExpenses { get; set; }
    public decimal TotalCost { get; set; }
    public decimal GrossRevenue { get; set; }
    public decimal NetProfit { get; set; }
    public decimal ProfitPerBird { get; set; }
    public decimal ProfitMarginPct { get; set; }
}
