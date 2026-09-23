using AmrPoultryFarmWeb.Models;
using AmrPoultryFarmWeb.Services.Reports.Pdf;
using Microsoft.Extensions.Localization;

namespace AmrPoultryFarmWeb.Services.Reports;

/// <summary>
/// Loads report data through <see cref="FarmService"/> (so tenant + house scoping apply exactly as
/// they do on screen) and renders the downloadable PDF reports.
/// </summary>
public class ReportService
{
    private readonly FarmService farm;
    private readonly AuthService auth;
    private readonly IStringLocalizer<SharedResource> localizer;

    public ReportService(FarmService farm, AuthService auth, IStringLocalizer<SharedResource> localizer)
    {
        this.farm = farm;
        this.auth = auth;
        this.localizer = localizer;
    }

    public async Task<BatchDataset?> LoadBatchAsync(int batchId)
    {
        var batch = await farm.GetBatchAsync(batchId);
        if (batch is null) return null;
        return await LoadAsync(batch);
    }

    private async Task<BatchDataset> LoadAsync(Batch batch) => new()
    {
        Batch = batch,
        Perf = await farm.GetPerformanceAsync(batch.Id),
        Daily = await farm.GetDailyRecordsAsync(batch.Id),
        Feed = await farm.GetFeedDeliveriesAsync(batch.Id),
        Health = await farm.GetHealthEventsAsync(batch.Id),
        Liftings = await farm.GetLiftingsAsync(batch.Id),
        Expenses = await farm.GetExpensesAsync(batch.Id),
        Settlement = await farm.GetSettlementAsync(batch.Id),
    };

    public async Task<List<BatchSummary>> LoadSummariesAsync(int? houseId, int? year)
    {
        var batches = await farm.GetBatchesAsync(houseId: houseId, year: year);
        var list = new List<BatchSummary>();
        foreach (var b in batches.OrderBy(b => b.PlacementDate))
            list.Add(BatchAnalytics.Summarize(await LoadAsync(b)));
        return list;
    }

    private ReportBranding Branding(string title, string subtitle, PdfText? text = null) =>
        ReportBranding.From(auth.CurrentTenant, title, subtitle, auth.CurrentUser?.DisplayName is { Length: > 0 } n ? n : auth.CurrentUser?.Username, text);

    private async Task<string> FilterLabelAsync(int? houseId, int? year)
    {
        string house = "All houses";
        if (houseId is { } id) house = (await farm.GetHouseAsync(id))?.Name ?? "House";
        return $"{house} · {(year is { } y ? y.ToString() : "All years")}";
    }

    // ---------------- Reports ----------------

    public async Task<(byte[] Pdf, string BatchCode)?> BatchCloseoutPdfAsync(int batchId)
    {
        var data = await LoadBatchAsync(batchId);
        if (data is null) return null;

        var summary = BatchAnalytics.Summarize(data);
        // Compare against the farm's other completed batches (all years/houses the user can see).
        var others = (await LoadSummariesAsync(null, null)).Where(s => s.Batch.Id != batchId && s.IsComplete).ToList();

        // Translated into the requester's language (culture cookie); English when they use English.
        var text = new PdfText(localizer);
        var title = text[data.Batch.Status == BatchStatus.Closed ? "Batch Closeout Report" : "Batch Progress Report"];
        var brand = Branding(title, $"{text["Batch"]} {data.Batch.BatchCode} · {data.Batch.House?.Name} · {data.Batch.Integrator?.Name}", text);
        return (BatchCloseoutPdf.Render(brand, summary, others), data.Batch.BatchCode);
    }

    public async Task<byte[]> BatchComparisonPdfAsync(int? houseId, int? year)
    {
        var summaries = await LoadSummariesAsync(houseId, year);
        var brand = Branding("Batch Comparison & Next-Batch Plan", await FilterLabelAsync(houseId, year));
        return BatchComparisonPdf.Render(brand, summaries);
    }

    public async Task<byte[]> FinancialPdfAsync(int? houseId, int? year)
    {
        var summaries = await LoadSummariesAsync(houseId, year);
        var general = (await farm.GetAllExpensesAsync())
            .Where(e => e.BatchId is null && (year is null || e.Date.Year == year))
            .ToList();
        var brand = Branding("Financial & Cost Analysis", await FilterLabelAsync(houseId, year));
        return FinancialPdf.Render(brand, summaries, houseId is null ? general : new List<Expense>());
    }
}
