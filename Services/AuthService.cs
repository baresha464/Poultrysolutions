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

    private FarmDbContext NewContext() => dbFactory.CreateDbContext();

    public User? CurrentUser { get; private set; }
    public HashSet<string> CurrentPermissions { get; private set; } = new();
    public HashSet<int> CurrentAssignedHouseIds { get; private set; } = new();
    public bool IsSystemAdmin { get; private set; }
    public bool IsAuthenticated => CurrentUser is not null;

    /// <summary>Raised after the session snapshot changes so subscribed components can re-render.</summary>
    public event Action? StateChanged;

    // ---------------- Session (claims-backed) ----------------

    /// <summary>Verifies credentials for the login endpoint. Does not touch the auth cookie itself —
    /// the endpoint calls HttpContext.SignInAsync with the returned user's id.</summary>
    public async Task<User?> ValidateCredentialsAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        using var db = NewContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsActive);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt))
            return null;
        return user;
    }

    /// <summary>Populates the session snapshot from the signed-in cookie principal. Call once per
    /// circuit (MainLayout does this via the cascading AuthenticationState).</summary>
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

        using var db = NewContext();
        var user = await db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r!.RolePermissions)
            .Include(u => u.UserHouses)
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);

        if (user is null)
        {
            Clear();
            return;
        }

        ApplySession(user);
    }

    public bool HasPermission(string code) => CurrentPermissions.Contains(code);

    private void ApplySession(User user)
    {
        CurrentUser = user;
        CurrentPermissions = user.UserRoles
            .Where(ur => ur.Role is not null)
            .SelectMany(ur => ur.Role!.RolePermissions)
            .Select(rp => rp.PermissionCode)
            .ToHashSet();
        CurrentAssignedHouseIds = user.UserHouses.Select(uh => uh.HouseId).ToHashSet();
        IsSystemAdmin = user.UserRoles.Any(ur => ur.Role is { IsSystemRole: true });
        StateChanged?.Invoke();
    }

    private void Clear()
    {
        CurrentUser = null;
        CurrentPermissions = new();
        CurrentAssignedHouseIds = new();
        IsSystemAdmin = false;
        StateChanged?.Invoke();
    }

    // ---------------- Users ----------------
    public async Task<List<User>> GetUsersAsync()
    {
        using var db = NewContext();
        return await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .OrderBy(u => u.Username).ToListAsync();
    }

    public async Task<User?> GetUserAsync(int id)
    {
        using var db = NewContext();
        return await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .Include(u => u.UserHouses)
            .FirstOrDefaultAsync(u => u.Id == id);
    }

    /// <summary>Creates or updates a user. Pass a non-empty <paramref name="newPassword"/> to set/reset it.</summary>
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
            db.Users.Add(user);
        }
        else
        {
            var existing = await db.Users.Include(u => u.UserRoles).Include(u => u.UserHouses)
                .FirstAsync(u => u.Id == user.Id);
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
        var user = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
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
