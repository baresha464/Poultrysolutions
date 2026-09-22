using System.Security.Claims;
using AmrPoultryFarmWeb.Components;
using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---------------- Database ----------------
// Provider is configurable: SQL Server for real deployments (Nokia/enterprise environment —
// the default), SQLite for zero-install local dev. Set in appsettings or env vars:
//   Database:Provider          = "SqlServer" | "Sqlite"   (default: SqlServer)
//   ConnectionStrings:Farm     = the connection string for whichever provider is selected
var databaseProvider = builder.Configuration["Database:Provider"] ?? "SqlServer";

if (string.Equals(databaseProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
    Directory.CreateDirectory(dataDir);
    var defaultSqliteConnectionString = $"Data Source={Path.Combine(dataDir, "amr_poultry_farm.db3")}";
    var sqliteConnectionString = builder.Configuration.GetConnectionString("Farm") ?? defaultSqliteConnectionString;

    builder.Services.AddDbContextFactory<FarmDbContext>(options => options.UseSqlite(sqliteConnectionString));
}
else
{
    // e.g. "Server=localhost;Database=AmrPoultryFarm;Trusted_Connection=True;TrustServerCertificate=True;"
    // or, for SQL auth: "Server=YOUR_SERVER;Database=AmrPoultryFarm;User Id=...;Password=...;TrustServerCertificate=True;"
    var sqlServerConnectionString = builder.Configuration.GetConnectionString("Farm");
    if (string.IsNullOrWhiteSpace(sqlServerConnectionString))
    {
        throw new InvalidOperationException(
            "ConnectionStrings:Farm is required when Database:Provider is SqlServer. " +
            "Set it in appsettings.json, an environment variable (ConnectionStrings__Farm), " +
            "or user-secrets, or switch Database:Provider to \"Sqlite\" for local dev.");
    }

    builder.Services.AddDbContextFactory<FarmDbContext>(options => options.UseSqlServer(sqlServerConnectionString));
}

// ---------------- Auth ----------------
// Cookie auth replaces the MAUI app's SecureStorage-backed session. The cookie only carries
// the user id; AuthService re-reads roles/permissions/houses from the DB once per circuit so
// permission changes take effect on next login/refresh without re-issuing tokens.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.Name = "AmrPoultryFarm.Auth";
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// ---------------- App services ----------------
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<FarmService>();
builder.Services.AddScoped<PlatformAdminService>();
builder.Services.AddScoped<DemoDataSeeder>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// ---------------- Schema + Super Admin bootstrap ----------------
// No more global "seed houses/admin if empty" — houses/roles/an admin user now only get created
// per-tenant, when a Super Admin creates that client (PlatformAdminService.CreateTenantAsync).
// Startup's only job is: the schema exists, and exactly one platform Super Admin account exists.
// Skipped under `dotnet ef` (set EF_DESIGN_TIME=1 when running migration commands) — everything
// between Build() and Run() executes during design-time tooling too, and this block needs a real
// reachable database, which design-time generation shouldn't depend on.
if (Environment.GetEnvironmentVariable("EF_DESIGN_TIME") != "1")
using (var scope = app.Services.CreateScope())
{
    using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FarmDbContext>>().CreateDbContext();
    if (string.Equals(databaseProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        // SQLite stays EnsureCreated — it's throwaway local dev, not worth a second migration
        // history alongside SQL Server's real one.
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        await db.Database.MigrateAsync();
    }

    var platformAdmin = scope.ServiceProvider.GetRequiredService<PlatformAdminService>();
    var seeded = await platformAdmin.EnsureSuperAdminSeededAsync();
    if (seeded is { } credentials)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine(" Super Admin account created — this is the only time the");
        Console.WriteLine(" password is shown. Sign in and change it, then create your");
        Console.WriteLine(" first client from the Super Admin area.");
        Console.WriteLine($"   Username: {credentials.Username}");
        Console.WriteLine($"   Password: {credentials.Password}");
        Console.WriteLine("============================================================");
    }
}

// ---------------- Demo data seeding (dev only) ----------------
// `dotnet run -- seed-demo [Tenant Name]` provisions a brand-new tenant the same way the Super
// Admin UI does (houses/Admin role/admin login) and fills it with a few realistic batches, then
// exits without starting Kestrel. Doesn't touch existing tenants — safe to run any time.
if (args.Contains("seed-demo"))
{
    using var scope = app.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
    var tenantNameArg = args.SkipWhile(a => a != "seed-demo").Skip(1).FirstOrDefault();
    var result = await seeder.SeedAsync(tenantNameArg);

    Console.WriteLine("============================================================");
    Console.WriteLine(" Demo data seeded.");
    Console.WriteLine($"   Tenant:   {result.Tenant.Name}");
    Console.WriteLine($"   Username: {result.AdminUsername}");
    Console.WriteLine($"   Password: {result.AdminPassword}");
    Console.WriteLine(" Sign in at /Account/Login with these credentials.");
    Console.WriteLine("============================================================");
    return;
}

// `dotnet run -- reset-creds` sets known, easy-to-remember credentials for local testing:
// sadmin/sadmin for the platform Super Admin, admin/admin for the seeded tenant admin. Dev
// convenience only — never use these on anything reachable off your own machine.
if (args.Contains("reset-creds"))
{
    using var scope = app.Services.CreateScope();
    using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FarmDbContext>>().CreateDbContext();

    var superAdmin = await db.Users.FirstOrDefaultAsync(u => u.IsSuperAdmin);
    if (superAdmin is not null)
    {
        var (hash, salt) = PasswordHasher.Hash("sadmin");
        superAdmin.Username = "sadmin";
        superAdmin.PasswordHash = hash;
        superAdmin.PasswordSalt = salt;
    }

    var tenantAdmin = await db.Users.FirstOrDefaultAsync(u => u.Username == "admin" && !u.IsSuperAdmin);
    if (tenantAdmin is not null)
    {
        var (hash, salt) = PasswordHasher.Hash("admin");
        tenantAdmin.PasswordHash = hash;
        tenantAdmin.PasswordSalt = salt;
    }

    await db.SaveChangesAsync();

    Console.WriteLine("============================================================");
    Console.WriteLine($" Super Admin reset: {(superAdmin is not null ? "sadmin / sadmin" : "(none found)")}");
    Console.WriteLine($" Tenant admin reset: {(tenantAdmin is not null ? "admin / admin" : "(none found — username 'admin' not present)")}");
    Console.WriteLine("============================================================");
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ---------------- Login / logout endpoints ----------------
// Plain POST endpoints (not Blazor circuit calls) because only these can issue/clear the auth
// cookie via HttpContext.SignInAsync/SignOutAsync mid-request.
app.MapPost("/Account/LoginSubmit", async (HttpContext http, AuthService authService, string? returnUrl) =>
{
    var form = await http.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();

    string BackTo(int error) =>
        $"/Account/Login?error={error}" + (string.IsNullOrWhiteSpace(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}");

    var user = await authService.ValidateCredentialsAsync(username, password);
    if (user is null)
    {
        return Results.Redirect(BackTo(1));
    }

    // Checked before issuing the cookie (not left for the next circuit's LoadFromPrincipalAsync
    // to discover) so a deactivated tenant's user gets a clear message right at sign-in.
    if (!await authService.CanSignInAsync(user))
    {
        return Results.Redirect(BackTo(2));
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true });

    var defaultTarget = user.IsSuperAdmin ? "/superadmin" : "/";
    var target = string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') ? defaultTarget : returnUrl;
    return Results.Redirect(target);
}).DisableAntiforgery();

app.MapPost("/Account/Logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/Account/Login");
}).DisableAntiforgery();

// Tenant logo, referenced as <img src="/tenant-logo/{id}"> from MainLayout — a plain GET endpoint
// (not a data-URI embedded in the page) so the logo bytes aren't re-sent on every circuit render.
// No auth required: it's just an image keyed by a numeric id, equivalent to any static asset.
app.MapGet("/tenant-logo/{tenantId:int}", async (int tenantId, HttpContext http, IDbContextFactory<FarmDbContext> dbFactory) =>
{
    using var db = dbFactory.CreateDbContext();
    var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
    if (tenant?.LogoBytes is null || tenant.LogoContentType is null)
        return Results.Redirect("/images/logo-mark.png");

    http.Response.Headers.CacheControl = "public, max-age=3600";
    return Results.File(tenant.LogoBytes, tenant.LogoContentType);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
