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

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// ---------------- First-run schema + seed (houses, Admin role/user) ----------------
using (var scope = app.Services.CreateScope())
{
    var farmService = scope.ServiceProvider.GetRequiredService<FarmService>();
    await farmService.InitializeAsync();
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

    var user = await authService.ValidateCredentialsAsync(username, password);
    if (user is null)
    {
        var back = string.IsNullOrWhiteSpace(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return Results.Redirect($"/Account/Login?error=1{back}");
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true });

    var target = string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') ? "/" : returnUrl;
    return Results.Redirect(target);
}).DisableAntiforgery();

app.MapPost("/Account/Logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/Account/Login");
}).DisableAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
