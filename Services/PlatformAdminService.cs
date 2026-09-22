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

    /// <summary>Creates a new client: the Tenant row, its default 3-house layout, an Admin role
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

        db.Houses.AddRange(
            new House { Name = "House 1", SortOrder = 1 },
            new House { Name = "House 2", SortOrder = 2 },
            new House { Name = "House 3", SortOrder = 3 });

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
