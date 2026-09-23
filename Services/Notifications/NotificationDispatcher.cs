using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AmrPoultryFarmWeb.Services.Notifications;

/// <summary>
/// Sends one WhatsApp template message and records it in NotificationLogs. The log row is written
/// *before* sending: the unique (UserId, DedupKey) index then guarantees an alert can't go out twice,
/// even if two worker runs overlap or the app restarts mid-send.
/// </summary>
public class NotificationDispatcher
{
    private readonly IDbContextFactory<FarmDbContext> dbFactory;
    private readonly IWhatsAppSender sender;
    private readonly WhatsAppOptions options;
    private readonly TenantFeatureService features;

    public NotificationDispatcher(IDbContextFactory<FarmDbContext> dbFactory, IWhatsAppSender sender, IOptions<WhatsAppOptions> options,
        TenantFeatureService features)
    {
        this.dbFactory = dbFactory;
        this.sender = sender;
        this.options = options.Value;
        this.features = features;
    }

    /// <summary>True when this client's messages really go out (feature on, credentials saved, and
    /// the platform isn't in test mode); false = recorded as Simulated only.</summary>
    public async Task<bool> IsLiveAsync(int tenantId) =>
        !options.ForceTestMode && (await features.GetStatusAsync(tenantId)).WhatsAppLive;

    public string LanguageCodeFor(string? preferredLanguage) =>
        AppLanguages.Normalize(preferredLanguage) == AppLanguages.Telugu ? "te" : "en";

    public Task<NotificationLog?> SendAlertAsync(int tenantId, int userId, string number, string language, FarmAlert alert, CancellationToken ct = default) =>
        SendAsync(tenantId, userId, number, alert.Kind, alert.DedupKey, alert.BatchId,
            options.Templates.Alert, LanguageCodeFor(language), new[] { alert.Title, alert.Details, alert.Action },
            alert.Title, ct);

    public Task<NotificationLog?> SendSummaryAsync(int tenantId, int userId, string number, string language, DailySummaryMessage summary, CancellationToken ct = default) =>
        SendAsync(tenantId, userId, number, AlertKinds.DailySummary, summary.DedupKey, summary.BatchId,
            options.Templates.DailySummary, LanguageCodeFor(language), summary.Parameters,
            string.Join(" | ", summary.Parameters), ct);

    /// <summary>Returns the log row, or null if this (user, dedupKey) was already handled or the
    /// client doesn't have WhatsApp alerts switched on.</summary>
    public async Task<NotificationLog?> SendAsync(int tenantId, int userId, string number, string kind, string dedupKey,
        int? batchId, string template, string languageCode, IReadOnlyList<string> parameters, string summary, CancellationToken ct = default)
    {
        if (!(await features.GetStatusAsync(tenantId)).WhatsAppEnabled) return null;
        var credentials = await features.GetWhatsAppCredentialsAsync(tenantId);

        using var db = dbFactory.CreateDbContext();
        db.TenantId = tenantId;
        if (await db.NotificationLogs.AnyAsync(l => l.UserId == userId && l.DedupKey == dedupKey, ct))
            return null;

        var row = new NotificationLog
        {
            UserId = userId,
            BatchId = batchId,
            Kind = kind,
            DedupKey = dedupKey,
            ToNumber = number,
            Summary = Truncate(summary, 600),
            Status = "Pending",
        };
        db.NotificationLogs.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return null; // another run claimed this alert first
        }

        var result = await sender.SendTemplateAsync(credentials, number, template, languageCode, parameters, ct);
        row.Status = !result.Success ? "Failed" : result.Simulated ? "Simulated" : "Sent";
        row.ProviderMessageId = result.MessageId;
        row.Error = result.Error is null ? null : Truncate(result.Error, 500);
        await db.SaveChangesAsync(ct);
        return row;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
