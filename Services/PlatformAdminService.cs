using System.Security.Cryptography;
using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace AmrPoultryFarmWeb.Services;

/// <summary>Result of provisioning a new client — the admin credentials are returned once so the
/// Super Admin UI can display them; they are never stored or shown again after this.</summary>
public record TenantProvisionResult(Tenant Tenant, string AdminUsername, string AdminPassword);

/// <summary>Platform-level operations: creating/activating clients and bootstrapping the one Super
/// Admin account. Deliberately separate from FarmService — this is tenant-agnostic (or explicitly
/// cross-tenant, for the couple of actions that need it) rather than per-tenant business logic, and
/// every method here either takes an explicit tenantId or touches no tenant data at all, so a
/// cross-tenant read/write is always a deliberate, grep-able call — never an ambient bypass.</summary>
public class PlatformAdminService
{
    private readonly IDbContextFactory<FarmDbContext> dbFactory;

    public PlatformAdminService(IDbContextFactory<FarmDbContext> dbFactory)
    {
        this.dbFactory = dbFactory;
    }

    /// <summary>Seeds the one platform Super Admin account if none exists yet. Returns the generated
    /// password (to be printed once at startup) or null if a Super Admin already exists.</summary>
    public async Task<(string Username, string Password)?> EnsureSuperAdminSeededAsync()
    {
        using var db = dbFactory.CreateDbContext();
        if (await db.Users.AnyAsync(u => u.IsSuperAdmin)) return null;

        var password = GenerateRandomPassword();
        var (hash, salt) = PasswordHasher.Hash(password);
        db.Users.Add(new User
        {
            Username = "superadmin",
            DisplayName = "Platform Super Admin",
            PasswordHash = hash,
            PasswordSalt = salt,
            IsActive = true,
            IsSuperAdmin = true,
            TenantId = null,
        });
        await db.SaveChangesAsync();
        return ("superadmin", password);
    }

    /// <summary>Tenant list for the Super Admin's client list — identity/status only, no farm data.</summary>
    public async Task<List<Tenant>> GetTenantsAsync()
    {
        using var db = dbFactory.CreateDbContext();
        return await db.Tenants.OrderByDescending(t => t.CreatedAtUtc).ToListAsync();
    }

    public async Task<Tenant?> GetTenantAsync(int id)
    {
        using var db = dbFactory.CreateDbContext();
        return await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
    }

    /// <summary>Creates a new client: the Tenant row, a single default house, an Admin role
    /// holding every permission, and that tenant's first admin user. Generates a password if
    /// <paramref name="initialPassword"/> isn't supplied. Mirrors what the old single-tenant
    /// FarmService.InitializeAsync used to seed once globally, now scoped to one new tenant.</summary>
    public async Task<TenantProvisionResult> CreateTenantAsync(string tenantName, string adminUsername, string? initialPassword)
    {
        using var db = dbFactory.CreateDbContext();

        var tenant = new Tenant { Name = tenantName };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        // Everything from here on is inserted for the tenant we just created — switching the
        // context's TenantId now means FarmDbContext's auto-stamp fills it in on each row below.
        db.TenantId = tenant.Id;

        // One house to start; more are added by the Super Admin when the client asks for them
        // (see AddHouseAsync) — tenants can't create houses themselves.
        db.Houses.Add(new House { Name = "House 1", SortOrder = 1 });

        var adminRole = new Role { Name = "Admin", IsSystemRole = true, SortOrder = 1 };
        adminRole.RolePermissions = PermissionCatalog.AllCodes
            .Select(code => new RolePermission { PermissionCode = code })
            .ToList();
        db.Roles.Add(adminRole);

        var password = string.IsNullOrWhiteSpace(initialPassword) ? GenerateRandomPassword() : initialPassword;
        var (hash, salt) = PasswordHasher.Hash(password);
        var adminUser = new User
        {
            Username = adminUsername,
            DisplayName = "Administrator",
            PasswordHash = hash,
            PasswordSalt = salt,
            IsActive = true,
            TenantId = tenant.Id,
        };
        db.Users.Add(adminUser);

        db.UserRoles.Add(new UserRole { User = adminUser, Role = adminRole });

        await db.SaveChangesAsync();

        return new TenantProvisionResult(tenant, adminUsername, password);
    }

    // ---------------- Houses (Super Admin only) ----------------
    // House count is a platform decision (it's what a client is provisioned/billed for), so creating
    // houses lives here rather than in FarmService. Tenants can still rename/edit/delete their own.

    public async Task<List<House>> GetTenantHousesAsync(int tenantId)
    {
        using var db = dbFactory.CreateDbContext();
        db.TenantId = tenantId;
        return await db.Houses.OrderBy(h => h.SortOrder).ThenBy(h => h.Name).ToListAsync();
    }

    public async Task<House> AddHouseAsync(int tenantId, string name, string? code, int capacityBirds)
    {
        using var db = dbFactory.CreateDbContext();
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId))
            throw new InvalidOperationException($"Tenant {tenantId} not found.");

        db.TenantId = tenantId;
        var nextOrder = (await db.Houses.MaxAsync(h => (int?)h.SortOrder) ?? 0) + 1;
        var house = new House
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"House {nextOrder}" : name.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            CapacityBirds = Math.Max(0, capacityBirds),
            SortOrder = nextOrder,
        };
        db.Houses.Add(house);
        await db.SaveChangesAsync();
        return house;
    }

    // ---------------- Integrators (Super Admin only) ----------------
    // Integrators and their contract terms (chick cost, bag weight, growing charge, FCR/mortality
    // rules...) are platform data. Clients only see the ones enabled for them (TenantIntegrators).

    public async Task<List<Integrator>> GetIntegratorsAsync()
    {
        using var db = dbFactory.CreateDbContext();
        return await db.Integrators.OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync();
    }

    public async Task<Integrator?> GetIntegratorAsync(int id)
    {
        using var db = dbFactory.CreateDbContext();
        return await db.Integrators.FirstOrDefaultAsync(i => i.Id == id);
    }

    /// <summary>Tenant ids an integrator is enabled for. Reads the join across tenants on purpose.</summary>
    public async Task<List<int>> GetIntegratorTenantIdsAsync(int integratorId)
    {
        using var db = dbFactory.CreateDbContext();
        return await db.TenantIntegrators.IgnoreQueryFilters()
            .Where(t => t.IntegratorId == integratorId).Select(t => t.TenantId).ToListAsync();
    }

    public async Task<int> SaveIntegratorAsync(Integrator integrator)
    {
        using var db = dbFactory.CreateDbContext();
        integrator.Name = integrator.Name.Trim();
        if (await db.Integrators.AnyAsync(i => i.Id != integrator.Id && i.Name == integrator.Name))
            throw new InvalidOperationException($"An integrator named \"{integrator.Name}\" already exists.");
        integrator.Batches = new();
        integrator.Tenants = new();
        if (integrator.Id == 0)
        {
            integrator.SortOrder = (await db.Integrators.MaxAsync(i => (int?)i.SortOrder) ?? 0) + 1;
            db.Integrators.Add(integrator);
        }
        else db.Integrators.Update(integrator);
        await db.SaveChangesAsync();
        return integrator.Id;
    }

    /// <summary>Number of batches (across all clients) that use this integrator — a count only.</summary>
    public async Task<int> CountBatchesForIntegratorAsync(int integratorId)
    {
        using var db = dbFactory.CreateDbContext();
        return await db.Batches.IgnoreQueryFilters().CountAsync(b => b.IntegratorId == integratorId);
    }

    /// <summary>Returns false (and does not delete) if any client has batches with this integrator —
    /// deactivate it instead.</summary>
    public async Task<bool> DeleteIntegratorAsync(int integratorId)
    {
        using var db = dbFactory.CreateDbContext();
        if (await db.Batches.IgnoreQueryFilters().AnyAsync(b => b.IntegratorId == integratorId)) return false;
        var integrator = await db.Integrators.FindAsync(integratorId);
        if (integrator is null) return false;
        db.TenantIntegrators.RemoveRange(await db.TenantIntegrators.IgnoreQueryFilters()
            .Where(t => t.IntegratorId == integratorId).ToListAsync());
        db.Integrators.Remove(integrator);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Integrator ids enabled for one client.</summary>
    public async Task<List<int>> GetTenantIntegratorIdsAsync(int tenantId)
    {
        using var db = dbFactory.CreateDbContext();
        db.TenantId = tenantId;
        return await db.TenantIntegrators.Select(t => t.IntegratorId).ToListAsync();
    }

    /// <summary>Replaces the set of integrators a client may use. Removing one only hides it from
    /// new batches — existing batches keep their integrator and history.</summary>
    public async Task SetTenantIntegratorsAsync(int tenantId, IEnumerable<int> integratorIds)
    {
        using var db = dbFactory.CreateDbContext();
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId))
            throw new InvalidOperationException($"Tenant {tenantId} not found.");
        db.TenantId = tenantId;

        var wanted = integratorIds.Distinct().ToHashSet();
        var valid = await db.Integrators.Where(i => wanted.Contains(i.Id)).Select(i => i.Id).ToListAsync();
        var current = await db.TenantIntegrators.ToListAsync();
        db.TenantIntegrators.RemoveRange(current.Where(c => !wanted.Contains(c.IntegratorId)));
        foreach (var id in valid.Where(id => current.All(c => c.IntegratorId != id)))
            db.TenantIntegrators.Add(new TenantIntegrator { IntegratorId = id });
        await db.SaveChangesAsync();
    }

    /// <summary>Enables one integrator for many clients at once (from the integrator's own page).</summary>
    public async Task SetIntegratorTenantsAsync(int integratorId, IEnumerable<int> tenantIds)
    {
        using var db = dbFactory.CreateDbContext();
        var wanted = tenantIds.Distinct().ToHashSet();
        var current = await db.TenantIntegrators.IgnoreQueryFilters().Where(t => t.IntegratorId == integratorId).ToListAsync();
        db.TenantIntegrators.RemoveRange(current.Where(c => !wanted.Contains(c.TenantId)));
        var validTenants = await db.Tenants.Where(t => wanted.Contains(t.Id)).Select(t => t.Id).ToListAsync();
        foreach (var tid in validTenants.Where(tid => current.All(c => c.TenantId != tid)))
            db.TenantIntegrators.Add(new TenantIntegrator { TenantId = tid, IntegratorId = integratorId });
        await db.SaveChangesAsync();
    }

    public async Task SetTenantActiveAsync(int tenantId, bool isActive)
    {
        using var db = dbFactory.CreateDbContext();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        if (tenant is null) return;
        tenant.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    /// <summary>Resets the password of the tenant's admin user (whoever holds its system Admin
    /// role). Returns false if the tenant or an admin user in it couldn't be found.</summary>
    public async Task<bool> ResetTenantAdminPasswordAsync(int tenantId, string newPassword)
    {
        using var db = dbFactory.CreateDbContext();
        db.TenantId = tenantId;
        var admin = await db.Users
            .Where(u => u.TenantId == tenantId)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.UserRoles.Any(ur => ur.Role!.IsSystemRole));
        if (admin is null) return false;

        var (hash, salt) = PasswordHasher.Hash(newPassword);
        admin.PasswordHash = hash;
        admin.PasswordSalt = salt;
        await db.SaveChangesAsync();
        return true;
    }

    private static string GenerateRandomPassword(int length = 16)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var result = new char[length];
        for (var i = 0; i < length; i++) result[i] = chars[bytes[i] % chars.Length];
        return new string(result);
    }
}
