using System.ComponentModel.DataAnnotations;

namespace AmrPoultryFarmWeb.Models;

/// <summary>A client/company using this installation. Every tenant-scoped row (houses, batches,
/// users, roles, ...) carries a TenantId back to one of these; row-level isolation is enforced by
/// FarmDbContext's global query filters, not by separate databases.</summary>
public class Tenant
{
    public int Id { get; set; }

    /// <summary>Shown as the app name throughout the tenant's UI (sidebar, topbar, page titles).</summary>
    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    public byte[]? LogoBytes { get; set; }

    [MaxLength(100)]
    public string? LogoContentType { get; set; }

    [Required, MaxLength(20)]
    public string PrimaryColorHex { get; set; } = "#0284c7";

    [Required, MaxLength(20)]
    public string AccentColorHex { get; set; } = "#e8a33d";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
