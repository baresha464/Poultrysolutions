using System.Security.Cryptography;
using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AmrPoultryFarmWeb.Services;

public record WhatsAppCredentials(string PhoneNumberId, string AccessToken);

/// <summary>
/// Per-client optional features (AI Assistant, WhatsApp alerts): on/off switches, each client's own
/// API key / WhatsApp credentials, and clients' requests to have a feature switched on.
///
/// Secrets are encrypted with ASP.NET Data Protection before they touch the database and are only
/// ever decrypted server-side at the moment of calling Anthropic / Meta. Nothing here returns a
/// secret to a page: the Super Admin sees "saved, ends in …abcd", clients only see enabled/ready.
///
/// Every method takes an explicit tenantId — callers pass the signed-in user's tenant (client
/// pages) or the tenant being administered (Super Admin), never an ambient one.
/// </summary>
public class TenantFeatureService
{
    private readonly IDbContextFactory<FarmDbContext> dbFactory;
    private readonly IDataProtector protector;
    private readonly ILogger<TenantFeatureService> log;

    public TenantFeatureService(IDbContextFactory<FarmDbContext> dbFactory, IDataProtectionProvider dataProtection,
        ILogger<TenantFeatureService> log)
    {
        this.dbFactory = dbFactory;
        protector = dataProtection.CreateProtector("BroilIQ.TenantFeatureSecrets.v1");
        this.log = log;
    }

    // ---------------- Reads ----------------

    /// <summary>Settings row for the Super Admin screen (secrets stay encrypted; use the Hint fields).</summary>
    public async Task<TenantFeature> GetAsync(int tenantId)
    {
        using var db = dbFactory.CreateDbContext();
        return await db.TenantFeatures.AsNoTracking().FirstOrDefaultAsync(f => f.TenantId == tenantId)
               ?? new TenantFeature { TenantId = tenantId };
    }

    /// <summary>Super Admin client list: every client's settings row (secrets stay encrypted).</summary>
    public async Task<Dictionary<int, TenantFeature>> GetAllAsync()
    {
        using var db = dbFactory.CreateDbContext();
        return await db.TenantFeatures.AsNoTracking().ToDictionaryAsync(f => f.TenantId);
    }

    public async Task<FeatureStatus> GetStatusAsync(int? tenantId)
    {
        if (tenantId is null) return FeatureStatus.None;
        var f = await GetAsync(tenantId.Value);
        return new FeatureStatus(
            f.AiEnabled, f.AiEnabled && f.AiApiKeyProtected is not null,
            f.WhatsAppEnabled, f.WhatsAppEnabled && f.WhatsAppAccessTokenProtected is not null && !string.IsNullOrWhiteSpace(f.WhatsAppPhoneNumberId));
    }

    /// <summary>The client's Anthropic key — only when the feature is switched on. Server-side use only.</summary>
    public async Task<string?> GetAiKeyAsync(int tenantId)
    {
        var f = await GetAsync(tenantId);
        return f.AiEnabled ? Unprotect(f.AiApiKeyProtected, tenantId, "AI key") : null;
    }

    /// <summary>The client's WhatsApp credentials — only when the feature is switched on and complete.</summary>
    public async Task<WhatsAppCredentials?> GetWhatsAppCredentialsAsync(int tenantId)
    {
        var f = await GetAsync(tenantId);
        if (!f.WhatsAppEnabled || string.IsNullOrWhiteSpace(f.WhatsAppPhoneNumberId)) return null;
        var token = Unprotect(f.WhatsAppAccessTokenProtected, tenantId, "WhatsApp token");
        return token is null ? null : new WhatsAppCredentials(f.WhatsAppPhoneNumberId.Trim(), token);
    }

    public async Task<HashSet<int>> GetWhatsAppEnabledTenantIdsAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        return (await db.TenantFeatures.Where(f => f.WhatsAppEnabled).Select(f => f.TenantId).ToListAsync(ct)).ToHashSet();
    }

    // ---------------- Super Admin writes ----------------

    /// <summary>Turns the AI Assistant on/off for a client. <paramref name="newApiKey"/> replaces the
    /// saved key when given; <paramref name="removeKey"/> deletes it.</summary>
    public async Task SaveAiAsync(int tenantId, bool enabled, string? newApiKey, bool removeKey)
    {
        using var db = dbFactory.CreateDbContext();
        var f = await LoadForUpdateAsync(db, tenantId);
        f.AiEnabled = enabled;
        if (removeKey) { f.AiApiKeyProtected = null; f.AiApiKeyHint = null; }
        if (!string.IsNullOrWhiteSpace(newApiKey))
        {
            var key = newApiKey.Trim();
            f.AiApiKeyProtected = protector.Protect(key);
            f.AiApiKeyHint = Hint(key);
        }
        f.UpdatedAtUtc = DateTime.UtcNow;
        if (enabled) await ResolvePendingAsync(db, tenantId, Features.AiAssistant, "Enabled by the platform administrator.");
        await db.SaveChangesAsync();
    }

    public async Task SaveWhatsAppAsync(int tenantId, bool enabled, string? phoneNumberId, string? displayNumber,
        string? newAccessToken, bool removeToken)
    {
        using var db = dbFactory.CreateDbContext();
        var f = await LoadForUpdateAsync(db, tenantId);
        f.WhatsAppEnabled = enabled;
        f.WhatsAppPhoneNumberId = string.IsNullOrWhiteSpace(phoneNumberId) ? null : phoneNumberId.Trim();
        f.WhatsAppDisplayNumber = string.IsNullOrWhiteSpace(displayNumber) ? null : displayNumber.Trim();
        if (removeToken) { f.WhatsAppAccessTokenProtected = null; f.WhatsAppAccessTokenHint = null; }
        if (!string.IsNullOrWhiteSpace(newAccessToken))
        {
            var token = newAccessToken.Trim();
            f.WhatsAppAccessTokenProtected = protector.Protect(token);
            f.WhatsAppAccessTokenHint = Hint(token);
        }
        f.UpdatedAtUtc = DateTime.UtcNow;
        if (enabled) await ResolvePendingAsync(db, tenantId, Features.WhatsAppAlerts, "Enabled by the platform administrator.");
        await db.SaveChangesAsync();
    }

    // ---------------- Feature requests ----------------

    /// <summary>Client side: ask for a feature. Returns false if one is already pending.</summary>
    public async Task<bool> RequestAsync(int tenantId, int userId, string userName, string feature, string message)
    {
        using var db = dbFactory.CreateDbContext();
        db.TenantId = tenantId;
        if (await db.FeatureRequests.AnyAsync(r => r.Feature == feature && r.Status == FeatureRequestStatus.Pending))
            return false;
        db.FeatureRequests.Add(new FeatureRequest
        {
            Feature = feature,
            RequestedByUserId = userId,
            RequestedByName = Truncate(userName, 80),
            Message = Truncate(message?.Trim() ?? "", 500),
        });
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>A client's own requests, newest first.</summary>
    public async Task<List<FeatureRequest>> GetRequestsAsync(int tenantId)
    {
        using var db = dbFactory.CreateDbContext();
        db.TenantId = tenantId;
        return await db.FeatureRequests.AsNoTracking().OrderByDescending(r => r.CreatedAtUtc).ToListAsync();
    }

    /// <summary>Super Admin: pending requests across all clients, with the client name.</summary>
    public async Task<List<(FeatureRequest Request, string TenantName)>> GetPendingRequestsAsync()
    {
        using var db = dbFactory.CreateDbContext();
        var rows = await db.FeatureRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Status == FeatureRequestStatus.Pending)
            .Join(db.Tenants, r => r.TenantId, t => t.Id, (r, t) => new { r, t.Name })
            .OrderBy(x => x.r.CreatedAtUtc)
            .ToListAsync();
        return rows.Select(x => (x.r, x.Name)).ToList();
    }

    /// <summary>Super Admin: decline a request with a note the client will see.</summary>
    public async Task DeclineAsync(int requestId, string note)
    {
        using var db = dbFactory.CreateDbContext();
        var r = await db.FeatureRequests.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == requestId);
        if (r is null || r.Status != FeatureRequestStatus.Pending) return;
        r.Status = FeatureRequestStatus.Declined;
        r.AdminNote = Truncate(note?.Trim() ?? "", 500);
        r.ResolvedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    // ---------------- Helpers ----------------

    private static async Task<TenantFeature> LoadForUpdateAsync(FarmDbContext db, int tenantId)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId))
            throw new InvalidOperationException($"Tenant {tenantId} not found.");
        var f = await db.TenantFeatures.FirstOrDefaultAsync(x => x.TenantId == tenantId);
        if (f is null)
        {
            f = new TenantFeature { TenantId = tenantId };
            db.TenantFeatures.Add(f);
        }
        return f;
    }

    private static async Task ResolvePendingAsync(FarmDbContext db, int tenantId, string feature, string note)
    {
        var pending = await db.FeatureRequests.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.Feature == feature && r.Status == FeatureRequestStatus.Pending)
            .ToListAsync();
        foreach (var r in pending)
        {
            r.Status = FeatureRequestStatus.Approved;
            r.AdminNote = note;
            r.ResolvedAtUtc = DateTime.UtcNow;
        }
    }

    private string? Unprotect(string? value, int tenantId, string what)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return protector.Unprotect(value);
        }
        catch (CryptographicException ex)
        {
            // Happens if the data-protection key ring was lost/replaced: the saved secret can't be
            // read any more and the Super Admin has to enter it again.
            log.LogError(ex, "Could not decrypt the {What} for tenant {TenantId}; it must be re-entered.", what, tenantId);
            return null;
        }
    }

    private static string Hint(string secret) => secret.Length <= 4 ? "****" : secret[^4..];

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
