using System.Security.Claims;
using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace AmrPoultryFarmWeb.Services;

/// <summary>
/// Same public surface as the original MAUI AuthService (CurrentUser/HasPermission/IsSystemAdmin/
/// CurrentAssignedHouseIds), so every page's permission checks are unchanged. What differs is how
/// a session gets in: the ASP.NET Core auth cookie (issued by the /Account/Login endpoint in
/// Program.cs) carries only the user id, and <see cref="LoadFromPrincipalAsync"/> — called once
/// per circuit from MainLayout — re-reads roles/permissions/houses fresh from the DB, replacing
/// the MAUI app's SecureStorage-backed session restore.
/// </summary>
public class AuthService
{
    private readonly IDbContextFactory<FarmDbContext> dbFactory;

    public AuthService(IDbContextFactory<FarmDbContext> dbFactory)
    {
        this.dbFactory = dbFactory;
    }

    // Tenant-scoped: every call this makes is filtered to CurrentTenantId (null while logged out
    // or for the Super Admin, whose own farm-data reads must fail-closed to empty — see
    // FarmDbContext.TenantId). Login and the initial principal lookup use their own unscoped
    // context instead, since the tenant isn't known yet at that point.
    private FarmDbContext NewContext()
    {
        var db = dbFactory.CreateDbContext();
        db.TenantId = CurrentTenantId;
        return db;
    }

    public User? CurrentUser { get; private set; }
    public HashSet<string> CurrentPermissions { get; private set; } = new();
    public HashSet<int> CurrentAssignedHouseIds { get; private set; } = new();
    public bool IsSystemAdmin { get; private set; }
    public bool IsAuthenticated => CurrentUser is not null;

    /// <summary>Tenant the current session belongs to; null while logged out or for the Super Admin
    /// (who isn't tied to any tenant).</summary>
    public int? CurrentTenantId { get; private set; }

    /// <summary>The tenant row itself — name/logo/colors — for MainLayout's branding.</summary>
    public Tenant? CurrentTenant { get; private set; }

    /// <summary>Platform-level account: can manage clients, never sees any tenant's farm data.</summary>
    public bool IsSuperAdmin { get; private set; }

    /// <summary>Raised after the session snapshot changes so subscribed components can re-render.</summary>
    public event Action? StateChanged;

    // ---------------- Session (claims-backed) ----------------

    /// <summary>Verifies credentials for the login endpoint. Does not touch the auth cookie itself —
    /// the endpoint calls HttpContext.SignInAsync with the returned user's id. Usernames are unique
    /// across the whole install (not per-tenant) precisely so this lookup — which happens before any
    /// tenant is known — is unambiguous.</summary>
    public async Task<User?> ValidateCredentialsAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        using var db = dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsActive);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt))
            return null;
        return user;
    }

    /// <summary>True if this user is safe to sign in: a Super Admin always is (no tenant to be
    /// deactivated); a tenant user needs their tenant to still exist and be active.</summary>
    public async Task<bool> CanSignInAsync(User user)
    {
        if (user.IsSuperAdmin) return true;
        if (user.TenantId is null) return false;
        using var db = dbFactory.CreateDbContext();
        return await db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.IsActive);
    }

    /// <summary>Populates the session snapshot from the signed-in cookie principal. Call once per
    /// circuit (MainLayout does this via the cascading AuthenticationState).
    /// Two-phase read because the tenant isn't known until the User row is read: phase 1 looks the
    /// user up on an unscoped context (User has no query filter, by design); phase 2 — only for a
    /// tenant user — switches that same context's TenantId over and re-queries with the
    /// role/permission/house includes, which now resolve correctly scoped.</summary>
    public async Task LoadFromPrincipalAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            Clear();
            return;
        }

        var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idClaim, out var userId))
        {
            Clear();
            return;
        }

        using var db = dbFactory.CreateDbContext();
        var basic = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);
        if (basic is null)
        {
            Clear();
            return;
        }

        if (basic.IsSuperAdmin)
        {
            ApplySuperAdminSession(basic);
            return;
        }

        if (basic.TenantId is null)
        {
            Clear();
            return;
        }

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == basic.TenantId);
        if (tenant is null || !tenant.IsActive)
        {
            Clear();
            return;
        }

        db.TenantId = tenant.Id;
        var user = await db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r!.RolePermissions)
            .Include(u => u.UserHouses)
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);

        if (user is null)
        {
            Clear();
            return;
        }

        ApplySession(user, tenant);
    }

    public bool HasPermission(string code) => CurrentPermissions.Contains(code);

    /// <summary>Saves the signed-in user's UI/AI language ("en" or "te").</summary>
    public async Task SetPreferredLanguageAsync(string language)
    {
        if (CurrentUser is null) return;
        using var db = dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == CurrentUser.Id);
        if (user is null) return;
        user.PreferredLanguage = AppLanguages.Normalize(language);
        await db.SaveChangesAsync();
        CurrentUser.PreferredLanguage = user.PreferredLanguage;
    }

    private void ApplySession(User user, Tenant tenant)
    {
        CurrentUser = user;
        CurrentTenantId = tenant.Id;
        CurrentTenant = tenant;
        IsSuperAdmin = false;
        CurrentPermissions = user.UserRoles
            .Where(ur => ur.Role is not null)
            .SelectMany(ur => ur.Role!.RolePermissions)
            .Select(rp => rp.PermissionCode)
            .ToHashSet();
        CurrentAssignedHouseIds = user.UserHouses.Select(uh => uh.HouseId).ToHashSet();
        IsSystemAdmin = user.UserRoles.Any(ur => ur.Role is { IsSystemRole: true });
        StateChanged?.Invoke();
    }

    private void ApplySuperAdminSession(User user)
    {
        CurrentUser = user;
        CurrentTenantId = null;
        CurrentTenant = null;
        IsSuperAdmin = true;
        CurrentPermissions = new();
        CurrentAssignedHouseIds = new();
        IsSystemAdmin = false;
        StateChanged?.Invoke();
    }

    private void Clear()
    {
        CurrentUser = null;
        CurrentTenantId = null;
        CurrentTenant = null;
        IsSuperAdmin = false;
        CurrentPermissions = new();
        CurrentAssignedHouseIds = new();
        IsSystemAdmin = false;
        StateChanged?.Invoke();
    }

    // ---------------- Users ----------------
    // User has no query filter (see its doc comment), so every method here filters TenantId by
    // hand — this is the small, deliberate cost of keeping login able to look up a username before
    // any tenant is known.
    public async Task<List<User>> GetUsersAsync()
    {
        using var db = NewContext();
        return await db.Users.Where(u => u.TenantId == CurrentTenantId)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .OrderBy(u => u.Username).ToListAsync();
    }

    public async Task<User?> GetUserAsync(int id)
    {
        using var db = NewContext();
        return await db.Users.Where(u => u.TenantId == CurrentTenantId)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .Include(u => u.UserHouses)
            .FirstOrDefaultAsync(u => u.Id == id);
    }

    /// <summary>Creates or updates a user in the current tenant. Pass a non-empty
    /// <paramref name="newPassword"/> to set/reset it.</summary>
    public async Task<int> SaveUserAsync(User user, List<int> roleIds, List<int> houseIds, string? newPassword)
    {
        using var db = NewContext();

        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            var (hash, salt) = PasswordHasher.Hash(newPassword);
            user.PasswordHash = hash;
            user.PasswordSalt = salt;
        }

        if (user.Id == 0)
        {
            user.TenantId = CurrentTenantId;
            db.Users.Add(user);
        }
        else
        {
            var existing = await db.Users.Include(u => u.UserRoles).Include(u => u.UserHouses)
                .FirstAsync(u => u.Id == user.Id && u.TenantId == CurrentTenantId);
            existing.Username = user.Username;
            existing.DisplayName = user.DisplayName;
            existing.IsActive = user.IsActive;
            if (!string.IsNullOrWhiteSpace(newPassword))
            {
                existing.PasswordHash = user.PasswordHash;
                existing.PasswordSalt = user.PasswordSalt;
            }
            db.UserRoles.RemoveRange(existing.UserRoles);
            db.UserHouses.RemoveRange(existing.UserHouses);
            user = existing;
        }
        await db.SaveChangesAsync();

        db.UserRoles.AddRange(roleIds.Distinct().Select(rid => new UserRole { UserId = user.Id, RoleId = rid }));
        db.UserHouses.AddRange(houseIds.Distinct().Select(hid => new UserHouse { UserId = user.Id, HouseId = hid }));
        await db.SaveChangesAsync();

        return user.Id;
    }

    /// <summary>Returns false (and does not delete) if this is the last user holding the system Admin role.</summary>
    public async Task<bool> DeleteUserAsync(int userId)
    {
        using var db = NewContext();
        var user = await db.Users.Where(u => u.TenantId == CurrentTenantId)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return false;

        var holdsSystemRole = user.UserRoles.Any(ur => ur.Role is { IsSystemRole: true });
        if (holdsSystemRole)
        {
            var otherAdmins = await db.UserRoles.CountAsync(ur => ur.Role!.IsSystemRole && ur.UserId != userId);
            if (otherAdmins == 0) return false;
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return true;
    }

    // ---------------- Roles ----------------
    public async Task<List<Role>> GetRolesAsync()
    {
        using var db = NewContext();
        return await db.Roles.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync();
    }

    public async Task<Role?> GetRoleAsync(int id)
    {
        using var db = NewContext();
        return await db.Roles.Include(r => r.RolePermissions).FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<int> SaveRoleAsync(Role role, List<string> permissionCodes)
    {
        using var db = NewContext();

        if (role.Id == 0)
        {
            role.RolePermissions = permissionCodes.Distinct()
                .Select(c => new RolePermission { PermissionCode = c }).ToList();
            db.Roles.Add(role);
        }
        else
        {
            var existing = await db.Roles.Include(r => r.RolePermissions).FirstAsync(r => r.Id == role.Id);
            existing.Name = existing.IsSystemRole ? existing.Name : role.Name;
            existing.SortOrder = role.SortOrder;
            db.RolePermissions.RemoveRange(existing.RolePermissions);
            existing.RolePermissions = permissionCodes.Distinct()
                .Select(c => new RolePermission { RoleId = existing.Id, PermissionCode = c }).ToList();
        }
        await db.SaveChangesAsync();
        return role.Id;
    }

    /// <summary>Returns false (and does not delete) for the system Admin role or a role still assigned to users.</summary>
    public async Task<bool> DeleteRoleAsync(int roleId)
    {
        using var db = NewContext();
        var role = await db.Roles.FindAsync(roleId);
        if (role is null || role.IsSystemRole) return false;
        if (await db.UserRoles.AnyAsync(ur => ur.RoleId == roleId)) return false;
        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        return true;
    }
}
