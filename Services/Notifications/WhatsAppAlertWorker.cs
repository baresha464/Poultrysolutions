using System.Globalization;
using System.Security.Claims;
using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Models;
using AmrPoultryFarmWeb.Services.Reports;
using AmrPoultryFarmWeb.Services.Reports.Pdf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace AmrPoultryFarmWeb.Services.Notifications;

/// <summary>
/// Every few minutes: for each opted-in user of each active client, look at the active batches that
/// user can see and send any new alerts (plus the evening summary) over WhatsApp.
///
/// Each user is evaluated *as that user* — AuthService is loaded from their id in a fresh DI scope —
/// so house scoping and permissions are exactly what they get on screen, and the text is in their
/// language. De-duplication lives in NotificationDispatcher, so re-running is always safe.
/// </summary>
public class WhatsAppAlertWorker : BackgroundService
{
    private readonly IServiceProvider services;
    private readonly IDbContextFactory<FarmDbContext> dbFactory;
    private readonly WhatsAppOptions options;
    private readonly ILogger<WhatsAppAlertWorker> log;

    public WhatsAppAlertWorker(IServiceProvider services, IDbContextFactory<FarmDbContext> dbFactory,
        IOptions<WhatsAppOptions> options, ILogger<WhatsAppAlertWorker> log)
    {
        this.services = services;
        this.dbFactory = dbFactory;
        this.options = options.Value;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("WhatsApp alerts running every {Minutes} min ({Mode}).", options.CheckIntervalMinutes,
            options.ForceTestMode ? "platform test mode — logged, not sent" : "per-client credentials");

        // Small delay so startup (migrations, first requests) isn't competing with the first run.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.CheckIntervalMinutes)));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "WhatsApp alert run failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, options.ResolveTimeZone());

        List<(int TenantId, NotificationPreference Pref, User User)> recipients = new();
        using (var db = dbFactory.CreateDbContext())
        {
            // Only active clients that the Super Admin has given WhatsApp alerts.
            var enabled = await services.GetRequiredService<TenantFeatureService>().GetWhatsAppEnabledTenantIdsAsync(ct);
            var tenantIds = (await db.Tenants.Where(t => t.IsActive).Select(t => t.Id).ToListAsync(ct))
                .Where(enabled.Contains).ToList();
            foreach (var tenantId in tenantIds)
            {
                db.TenantId = tenantId;
                var prefs = await db.NotificationPreferences.AsNoTracking()
                    .Where(p => p.WhatsAppOptIn && p.WhatsAppNumber != "")
                    .Include(p => p.User)
                    .ToListAsync(ct);
                recipients.AddRange(prefs.Where(p => p.User is { IsActive: true }).Select(p => (tenantId, p, p.User!)));
            }
        }

        int sent = 0;
        foreach (var (tenantId, pref, user) in recipients)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                sent += await RunForUserAsync(tenantId, pref, user, localNow, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "WhatsApp alerts failed for user {UserId} (tenant {TenantId}).", user.Id, tenantId);
            }
        }
        if (sent > 0) log.LogInformation("WhatsApp alert run: {Count} message(s) to {Users} user(s).", sent, recipients.Count);
    }

    private async Task<int> RunForUserAsync(int tenantId, NotificationPreference pref, User user, DateTime localNow, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        await auth.LoadFromPrincipalAsync(new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) }, "alerts")));
        if (auth.CurrentTenantId != tenantId || !auth.HasPermission(Permissions.Batches.View)) return 0;

        // Build every message in the recipient's language.
        var previousUi = CultureInfo.CurrentUICulture;
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentUICulture = AppLanguages.UiCultureFor(user.PreferredLanguage);
        CultureInfo.CurrentCulture = AppLanguages.FormatCulture;
        try
        {
            var farm = scope.ServiceProvider.GetRequiredService<FarmService>();
            var reports = scope.ServiceProvider.GetRequiredService<ReportService>();
            var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
            var T = new PdfText(scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>());

            int sent = 0;
            foreach (var batch in await farm.GetBatchesAsync(activeOnly: true))
            {
                var data = await reports.LoadBatchAsync(batch.Id);
                if (data is null) continue;

                foreach (var alert in AlertEngine.Evaluate(data, localNow, T).Where(a => Wanted(pref, a.Kind)))
                    if (await dispatcher.SendAlertAsync(tenantId, user.Id, pref.WhatsAppNumber, user.PreferredLanguage, alert, ct) is not null)
                        sent++;

                if (pref.DailySummary && localNow.Hour >= pref.SummaryHour && AlertEngine.Summary(data, localNow, T) is { } summary)
                    if (await dispatcher.SendSummaryAsync(tenantId, user.Id, pref.WhatsAppNumber, user.PreferredLanguage, summary, ct) is not null)
                        sent++;
            }
            return sent;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static bool Wanted(NotificationPreference p, string kind) => kind switch
    {
        AlertKinds.Mortality => p.MortalityAlerts,
        AlertKinds.Heat or AlertKinds.Cold => p.HeatAlerts,
        AlertKinds.Feed => p.FeedAlerts,
        AlertKinds.VaccinationDue or AlertKinds.VaccinationLate => p.VaccinationAlerts,
        AlertKinds.MissingEntry => p.MissingEntryAlerts,
        _ => true,
    };
}
