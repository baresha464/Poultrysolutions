using System.ComponentModel.DataAnnotations;
using AmrPoultryFarmWeb.Data;

namespace AmrPoultryFarmWeb.Models;

/// <summary>Optional paid features a client can have switched on.</summary>
public static class Features
{
    public const string AiAssistant = "AI";
    public const string WhatsAppAlerts = "WhatsApp";

    public static string Label(string feature) => feature switch
    {
        AiAssistant => "AI Assistant",
        WhatsAppAlerts => "WhatsApp Alerts",
        _ => feature,
    };
}

/// <summary>
/// Per-client switches and credentials for the optional features, managed only by the Super Admin.
/// Each client uses its own Anthropic API key and its own WhatsApp Business number/token, so usage
/// and billing stay separate. Secrets are stored encrypted (ASP.NET Data Protection, see
/// TenantFeatureService) and are never sent back to any page — only the last 4 characters are kept
/// in the clear so the Super Admin can tell which key is saved.
///
/// Deliberately not ITenantScoped: it is always read by explicit TenantId (tenant pages, the alert
/// worker, the Super Admin), and a tenant must never be able to query it through the normal filters.
/// </summary>
public class TenantFeature
{
    [Key]
    public int TenantId { get; set; }

    // ---- AI Assistant (Anthropic) ----
    public bool AiEnabled { get; set; }
    public string? AiApiKeyProtected { get; set; }
    [MaxLength(8)]
    public string? AiApiKeyHint { get; set; }

    // ---- WhatsApp alerts (Meta WhatsApp Business Cloud API) ----
    public bool WhatsAppEnabled { get; set; }
    [MaxLength(40)]
    public string? WhatsAppPhoneNumberId { get; set; }
    public string? WhatsAppAccessTokenProtected { get; set; }
    [MaxLength(8)]
    public string? WhatsAppAccessTokenHint { get; set; }
    [MaxLength(20)]
    public string? WhatsAppDisplayNumber { get; set; }   // the business number farmers will see messages from

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public enum FeatureRequestStatus { Pending = 0, Approved = 1, Declined = 2 }

/// <summary>A client asking the Super Admin to switch on a feature.</summary>
public class FeatureRequest : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    [Required, MaxLength(20)]
    public string Feature { get; set; } = "";

    public int RequestedByUserId { get; set; }
    [MaxLength(80)]
    public string RequestedByName { get; set; } = "";

    [MaxLength(500)]
    public string Message { get; set; } = "";

    public FeatureRequestStatus Status { get; set; } = FeatureRequestStatus.Pending;

    [MaxLength(500)]
    public string AdminNote { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }
}

/// <summary>What a client page may know about its features — never the secrets themselves.</summary>
public record FeatureStatus(bool AiEnabled, bool AiReady, bool WhatsAppEnabled, bool WhatsAppLive)
{
    public static readonly FeatureStatus None = new(false, false, false, false);
}
