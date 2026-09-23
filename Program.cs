using System.Security.Claims;
using AmrPoultryFarmWeb.Components;
using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Models;
using AmrPoultryFarmWeb.Services;
using AmrPoultryFarmWeb.Services.Reports;
using AmrPoultryFarmWeb.Services.Notifications;
using System.Globalization;
using AmrPoultryFarmWeb;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
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
        // Signed-out visitors opening the site root see the public landing page, not the login form.
        options.Events.OnRedirectToLogin = ctx =>
        {
            var target = ctx.Request.Path == "/" ? "/welcome" : ctx.RedirectUri;
            ctx.Response.Redirect(target);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// ---------------- App services ----------------
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<FarmService>();
builder.Services.AddScoped<PlatformAdminService>();
builder.Services.AddScoped<DemoDataSeeder>();
builder.Services.AddScoped<ReportService>();

// ---------------- Per-client features (AI Assistant, WhatsApp alerts) ----------------
// The Super Admin switches features on per client and enters that client's own Anthropic key /
// WhatsApp credentials; they are encrypted with Data Protection before being stored. The key ring
// must persist (and be shared between servers in a web farm), or saved secrets can't be decrypted
// and have to be re-entered: DataProtection:KeysPath (default App_Data/keys).
var keysPath = builder.Configuration["DataProtection:KeysPath"]
               ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .SetApplicationName(AppBrand.Name)
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
builder.Services.AddSingleton<TenantFeatureService>();
builder.Services.AddSingleton<DemoRequestService>();

// Claude-powered photo health check + farm assistant, using the signed-in client's own API key.
builder.Services.AddScoped<AmrPoultryFarmWeb.Services.Ai.FarmAiService>();

// ---------------- WhatsApp alerts ----------------
// Section "WhatsApp" (see docs/whatsapp-alerts.md) holds platform-wide settings only; each client
// sends from its own WhatsApp Business number. WhatsApp:Provider = "Log" puts the whole platform in
// test mode (messages recorded as Simulated, nothing leaves the server).
builder.Services.Configure<WhatsAppOptions>(builder.Configuration.GetSection("WhatsApp"));
var whatsApp = builder.Configuration.GetSection("WhatsApp").Get<WhatsAppOptions>() ?? new WhatsAppOptions();
builder.Services.AddHttpClient(nameof(WhatsAppSender), c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<IWhatsAppSender, WhatsAppSender>();
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddScoped<NotificationService>();
if (whatsApp.AlertsEnabled)
    builder.Services.AddHostedService<WhatsAppAlertWorker>();

// QuestPDF Community licence: free for individuals, non-profits and businesses under USD 1M annual
// gross revenue — see https://www.questpdf.com/license/ (a paid licence is needed above that).
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
foreach (var fontFile in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fonts"), "*.ttf"))
{
    using var fontStream = File.OpenRead(fontFile);
    QuestPDF.Drawing.FontManager.RegisterFontFromStream(fontStream);
}

// ---------------- Language (English / Telugu) ----------------
// Formatting culture is always en-IN (₹1,20,000, dot decimals); only the UI language varies, and
// it's carried in the standard culture cookie — set at sign-in from the user's saved preference
// and by /culture/set. Blazor Server picks it up when the circuit connects.
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.DefaultRequestCulture = new RequestCulture(AppLanguages.FormatCulture, AppLanguages.UiCultures[0]);
    o.SupportedCultures = new List<CultureInfo> { AppLanguages.FormatCulture };
    o.SupportedUICultures = AppLanguages.UiCultures.ToList();
    o.RequestCultureProviders = new List<IRequestCultureProvider> { new CookieRequestCultureProvider() };
});

// Emit Telugu as real characters, not &#xC2C; entities (the default encoder only passes Latin) —
// roughly 6x smaller pages for Telugu users on mobile data.
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(o =>
    o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(
        System.Text.Unicode.UnicodeRanges.BasicLatin, System.Text.Unicode.UnicodeRanges.Latin1Supplement,
        System.Text.Unicode.UnicodeRanges.GeneralPunctuation, System.Text.Unicode.UnicodeRanges.CurrencySymbols,
        System.Text.Unicode.UnicodeRanges.Telugu));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// BroilIQ mark for the "Powered by BroilIQ" PDF footer.
var platformMarkPath = Path.Combine(app.Environment.WebRootPath, AppBrand.MarkUrl);
if (File.Exists(platformMarkPath))
    AmrPoultryFarmWeb.Services.Reports.Pdf.PdfKit.PlatformMarkSvg = File.ReadAllText(platformMarkPath);

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

        // EnsureCreated never alters an existing file — bring older dev databases up to date.
        await SqliteSchemaUpgrader.UpgradeAsync(db);
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

app.UseRequestLocalization();

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

    // Apply the user's saved language on this device too. Choosing Telugu on the login page itself
    // is honoured (and saved) for a user still on the English default — so a first-time Telugu user
    // doesn't get switched back to English the moment they sign in.
    var language = user.PreferredLanguage;
    if (AppLanguages.Normalize(form["lang"]) == AppLanguages.Telugu && language != AppLanguages.Telugu)
    {
        await authService.LoadFromPrincipalAsync(new ClaimsPrincipal(identity));
        await authService.SetPreferredLanguageAsync(AppLanguages.Telugu);
        language = AppLanguages.Telugu;
    }
    AppendCultureCookie(http, language);

    var defaultTarget = user.IsSuperAdmin ? "/superadmin" : "/";
    var target = string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') ? defaultTarget : returnUrl;
    return Results.Redirect(target);
}).DisableAntiforgery();

app.MapPost("/Account/Logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/welcome");
}).DisableAntiforgery();

// Language switch (EN | తె): sets the culture cookie, saves the choice on the signed-in user so it
// follows them to other devices, then reloads the page so the Blazor circuit restarts in that language.
app.MapGet("/culture/set", async (string lang, string? returnUrl, HttpContext http, AuthService auth) =>
{
    var code = AppLanguages.Normalize(lang);
    AppendCultureCookie(http, code);
    if (http.User.Identity?.IsAuthenticated == true)
    {
        await auth.LoadFromPrincipalAsync(http.User);
        await auth.SetPreferredLanguageAsync(code);
    }
    var target = string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//") ? "/" : returnUrl;
    return Results.LocalRedirect(target);
});

static void AppendCultureCookie(HttpContext http, string? language)
{
    var ui = AppLanguages.UiCultureFor(language ?? AppLanguages.English);
    http.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(AppLanguages.FormatCulture, ui)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax, HttpOnly = true });
}

// Tenant logo, referenced as <img src="/tenant-logo/{id}"> from MainLayout — a plain GET endpoint
// (not a data-URI embedded in the page) so the logo bytes aren't re-sent on every circuit render.
// No auth required: it's just an image keyed by a numeric id, equivalent to any static asset.
app.MapGet("/tenant-logo/{tenantId:int}", async (int tenantId, HttpContext http, IDbContextFactory<FarmDbContext> dbFactory) =>
{
    using var db = dbFactory.CreateDbContext();
    var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
    if (tenant?.LogoBytes is null || tenant.LogoContentType is null)
        return Results.Redirect("/" + AppBrand.MarkUrl);

    http.Response.Headers.CacheControl = "public, max-age=3600";
    return Results.File(tenant.LogoBytes, tenant.LogoContentType);
});

// ---------------- PDF report downloads ----------------
// Plain GET endpoints so a normal <a href> triggers a browser download. AuthService is per-request
// here (not per-circuit), so each call loads the session from the auth cookie first — tenant and
// house scoping then apply exactly as they do on screen.
var reports = app.MapGroup("/reports/pdf").RequireAuthorization();

static async Task<IResult?> DenyUnlessAsync(HttpContext http, AuthService auth, params string[] permissions)
{
    await auth.LoadFromPrincipalAsync(http.User);
    if (auth.CurrentTenantId is null) return Results.Forbid();
    return permissions.All(auth.HasPermission) ? null : Results.Forbid();
}

static string FileSafe(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));

reports.MapGet("/batch/{id:int}", async (int id, HttpContext http, AuthService auth, ReportService svc) =>
{
    if (await DenyUnlessAsync(http, auth, Permissions.Batches.View) is { } denied) return denied;
    if (await svc.BatchCloseoutPdfAsync(id) is not { } report) return Results.NotFound();
    return Results.File(report.Pdf, "application/pdf", $"Batch-Report-{FileSafe(report.BatchCode)}-{DateTime.Today:yyyyMMdd}.pdf");
});

reports.MapGet("/comparison", async (int? houseId, int? year, HttpContext http, AuthService auth, ReportService svc) =>
{
    if (await DenyUnlessAsync(http, auth, Permissions.Reports.View) is { } denied) return denied;
    var pdf = await svc.BatchComparisonPdfAsync(houseId, year);
    return Results.File(pdf, "application/pdf", $"Batch-Comparison-{year?.ToString() ?? "All"}-{DateTime.Today:yyyyMMdd}.pdf");
});

reports.MapGet("/financial", async (int? houseId, int? year, HttpContext http, AuthService auth, ReportService svc) =>
{
    if (await DenyUnlessAsync(http, auth, Permissions.Reports.View, Permissions.Expenses.View) is { } denied) return denied;
    var pdf = await svc.FinancialPdfAsync(houseId, year);
    return Results.File(pdf, "application/pdf", $"Financial-Report-{year?.ToString() ?? "All"}-{DateTime.Today:yyyyMMdd}.pdf");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
