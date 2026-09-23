using System.ComponentModel.DataAnnotations;
using AmrPoultryFarmWeb.Data;

namespace AmrPoultryFarmWeb.Models;

/// <summary>A user's WhatsApp alert settings (one per user). Nothing is sent unless the user has
/// entered a number and explicitly opted in — WhatsApp's business policy requires consent.</summary>
public class NotificationPreference : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>International format, digits only, e.g. 919876543210.</summary>
    [MaxLength(20)]
    public string WhatsAppNumber { get; set; } = "";

    public bool WhatsAppOptIn { get; set; }
    public DateTime? OptInAtUtc { get; set; }

    public bool DailySummary { get; set; } = true;
    /// <summary>Local (farm) hour of day the daily summary goes out, 0-23.</summary>
    public int SummaryHour { get; set; } = 19;

    public bool MortalityAlerts { get; set; } = true;
    public bool HeatAlerts { get; set; } = true;
    public bool FeedAlerts { get; set; } = true;
    public bool VaccinationAlerts { get; set; } = true;
    public bool MissingEntryAlerts { get; set; } = true;
}

/// <summary>Every WhatsApp message the platform sent (or tried to), for history, troubleshooting and
/// de-duplication: an alert with the same <see cref="DedupKey"/> is never sent to a user twice.</summary>
public class NotificationLog : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int UserId { get; set; }
    public int? BatchId { get; set; }

    [MaxLength(30)]
    public string Kind { get; set; } = "";

    [MaxLength(120)]
    public string DedupKey { get; set; } = "";

    [MaxLength(20)]
    public string ToNumber { get; set; } = "";

    [MaxLength(600)]
    public string Summary { get; set; } = "";

    /// <summary>Sent, Failed or Simulated (test mode — logged but not delivered).</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "";

    [MaxLength(120)]
    public string? ProviderMessageId { get; set; }

    [MaxLength(500)]
    public string? Error { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
