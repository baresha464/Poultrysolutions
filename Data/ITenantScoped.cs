namespace AmrPoultryFarmWeb.Data;

/// <summary>Marker for every entity that belongs to exactly one tenant. FarmDbContext uses this to
/// apply a global query filter to each one and to auto-stamp TenantId on insert, so FarmService's
/// CRUD methods don't need to touch TenantId themselves.</summary>
public interface ITenantScoped
{
    int TenantId { get; set; }
}
