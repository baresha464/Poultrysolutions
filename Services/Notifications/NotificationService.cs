using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Models;
using AmrPoultryFarmWeb.Services.Reports.Pdf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace AmrPoultryFarmWeb.Services.Notifications;

/// <summary>The signed-in user's own WhatsApp settings, test message and message history
/// (Settings → WhatsApp Alerts). Always scoped to the current user and tenant.</summary>
public class NotificationService
{
    private readonly IDbContextFactory<FarmDbContext> dbFactory;
    private readonly AuthService auth;
    private readonly NotificationDispatcher dispatcher;
    private readonly IStringLocalizer<SharedResource> localizer;
    private readonly TenantFeatureService features;

    public NotificationService(IDbContextFactory<FarmDbContext> dbFactory, AuthService auth,
        NotificationDispatcher dispatcher, IStringLocalizer<SharedResource> localizer, TenantFeatureService features)
    {
        this.dbFactory = dbFactory;
        this.auth = auth;
        this.dispatcher = dispatcher;
        this.localizer = localizer;
        this.features = features;
    }

    /// <summary>Whether this client has WhatsApp alerts (Super Admin-controlled).</summary>
    public Task<FeatureStatus> GetFeatureStatusAsync() => features.GetStatusAsync(auth.CurrentTenantId);

    /// <summary>Whether this client's messages are really delivered (vs recorded as test messages).</summary>
    public Task<bool> IsLiveAsync() => auth.CurrentTenantId is { } t ? dispatcher.IsLiveAsync(t) : Task.FromResult(false);

    private FarmDbContext NewContext()
    {
        var db = dbFactory.CreateDbContext();
        db.TenantId = auth.CurrentTenantId;
        return db;
    }

    public async Task<NotificationPreference> GetMyPreferenceAsync()
    {
        var userId = auth.CurrentUser?.Id ?? throw new InvalidOperationException("Not signed in.");
        using var db = NewContext();
        return await db.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId)
               ?? new NotificationPreference { UserId = userId };
    }

    /// <summary>Saves the current user's settings. Returns an error message, or null on success.</summary>
    public async Task<string?> SaveMyPreferenceAsync(NotificationPreference input)
    {
        var userId = auth.CurrentUser?.Id ?? throw new InvalidOperationException("Not signed in.");
        string number = "";
        if (!string.IsNullOrWhiteSpace(input.WhatsAppNumber))
        {
            number = WhatsAppText.NormalizeNumber(input.WhatsAppNumber) ?? "";
            if (number.Length == 0) return localizer["Enter a valid mobile number, e.g. 98765 43210 or +91 98765 43210."];
        }
        if (input.WhatsAppOptIn && number.Length == 0)
            return localizer["Enter your WhatsApp number to turn alerts on."];

        using var db = NewContext();
        var pref = await db.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId);
        if (pref is null)
        {
            pref = new NotificationPreference { UserId = userId };
            db.NotificationPreferences.Add(pref);
        }

        if (input.WhatsAppOptIn && (!pref.WhatsAppOptIn || pref.WhatsAppNumber != number))
            pref.OptInAtUtc = DateTime.UtcNow; // record when consent was given, for this number
        pref.WhatsAppNumber = number;
        pref.WhatsAppOptIn = input.WhatsAppOptIn;
        pref.DailySummary = input.DailySummary;
        pref.SummaryHour = Math.Clamp(input.SummaryHour, 0, 23);
        pref.MortalityAlerts = input.MortalityAlerts;
        pref.HeatAlerts = input.HeatAlerts;
        pref.FeedAlerts = input.FeedAlerts;
        pref.VaccinationAlerts = input.VaccinationAlerts;
        pref.MissingEntryAlerts = input.MissingEntryAlerts;
        await db.SaveChangesAsync();
        return null;
    }

    public async Task<NotificationLog?> SendTestAsync()
    {
        var user = auth.CurrentUser ?? throw new InvalidOperationException("Not signed in.");
        var pref = await GetMyPreferenceAsync();
        if (!pref.WhatsAppOptIn || string.IsNullOrWhiteSpace(pref.WhatsAppNumber)) return null;

        var T = new PdfText(localizer);
        var alert = new FarmAlert(AlertKinds.Test, $"test:{DateTime.UtcNow.Ticks}", 0,
            T["BroilIQ test message"],
            T["WhatsApp alerts are set up for {0}. You will get alerts for your farm here.", auth.CurrentTenant?.Name ?? AppBrand.Name],
            T["No action needed."]);
        return await dispatcher.SendAlertAsync(auth.CurrentTenantId!.Value, user.Id, pref.WhatsAppNumber, user.PreferredLanguage, alert);
    }

    public async Task<List<NotificationLog>> GetMyHistoryAsync(int take = 25)
    {
        var userId = auth.CurrentUser?.Id ?? 0;
        using var db = NewContext();
        return await db.NotificationLogs.AsNoTracking().Where(l => l.UserId == userId)
            .OrderByDescending(l => l.CreatedAtUtc).Take(take).ToListAsync();
    }
}
