# AMR Poultry Farms — Web

ASP.NET Core Blazor Web App (.NET 8, Interactive Server render mode) for tracking broiler
batches, daily logs, feed, health events, lifting/harvest, settlement and expenses across
multiple houses — with role-based logins for your team.

This is a from-scratch web rebuild of the original `AmrPoultryFarm` .NET MAUI Blazor Hybrid
app: the same business logic and data model (batches, daily records, FCR/EEF performance
calculations, permissions system) now run as a normal ASP.NET Core site with a modern UI, so
anyone on the network can open it in a browser instead of installing a mobile app, and multiple
people can use it — including at the same time — against one shared database.

**Multi-tenant**: this is a single installation shared by multiple clients ("tenants"), each with
its own houses, batches, users and data — fully isolated from every other tenant, in one shared
database. A platform **Super Admin** creates and manages clients (`/superadmin`) but never sees
any client's farm data. Each client's own Admin can rename the app, upload a logo and pick brand
colors for their tenant under **Settings → Branding**. See [Multi-tenancy](#multi-tenancy) below.

## What changed vs. the original MAUI app

- **Database**: SQL Server by default (was: a local SQLite file on-device only). SQLite is
  still available for zero-setup local development — see [Database setup](#database-setup).
- **Auth**: ASP.NET Core cookie authentication (was: MAUI `SecureStorage`). Same username/
  password/roles/permissions model and the same `AuthService.HasPermission(...)` calls
  throughout every page — only *how* the session gets established changed.
- **UI**: reskinned with the modern design system from your Stitch mockups (forest-green
  palette, IBM Plex Sans + Sora, Material Symbols icons) — every page kept its original layout/
  markup and business logic; only the stylesheet (and the header/nav/login screen) changed.
- Everything else — models, permission catalog, FCR/EEF formulas, CRUD services — is carried
  over unchanged.

## Database setup

Set the provider in `appsettings.json` (or an environment variable, which is easiest for a
deployment — Docker, IIS, Azure App Service, etc. all support overriding config with env vars):

```jsonc
{
  "Database": { "Provider": "SqlServer" },   // or "Sqlite"
  "ConnectionStrings": {
    "Farm": "Server=localhost;Database=AmrPoultryFarm;Trusted_Connection=True;TrustServerCertificate=True;"
  }
}
```

**SQL Server** (default, recommended for real use — Nokia network deployment, multiple
concurrent users):
1. Have a SQL Server instance reachable from wherever this app runs (local SQL Server
   Express/Developer edition, or a shared server).
2. Set `ConnectionStrings:Farm` to point at it. The database itself doesn't need to exist yet —
   the app applies EF Core migrations automatically on first run (`Database.MigrateAsync()`,
   from `Migrations/`) and seeds the one platform Super Admin account (see
   [Multi-tenancy](#multi-tenancy) below — there's no more global houses/admin seed).
3. For a SQL-authenticated login instead of Windows/trusted auth:
   `Server=YOUR_SERVER;Database=AmrPoultryFarm;User Id=...;Password=...;TrustServerCertificate=True;`
4. Don't commit a real password into `appsettings.json` — use `dotnet user-secrets` locally or
   an environment variable (`ConnectionStrings__Farm`) in deployment.

**Evolving the schema**: after changing anything under `Models/` or `Data/FarmDbContext.cs`, add
a new migration and it'll apply automatically next run:
```bash
dotnet ef migrations add <DescriptiveName> --context FarmDbContext -o Migrations
```
`Database/CreateAmrPoultryFarmDatabase.sql` and `Database/SeedSampleData.sql` are kept only as
historical reference to the pre-multi-tenant schema — they're stale (no `TenantId` columns) and
shouldn't be run against a real database anymore; use migrations instead.

**SQLite** (local dev only — zero setup, a single `.db3` file under `App_Data/`):
```jsonc
{ "Database": { "Provider": "Sqlite" } }
```
`appsettings.Development.json` already defaults to this, so plain `dotnet run` in Development
works with no SQL Server install at all.

## Running it

```bash
cd AmrPoultryFarmWeb
dotnet restore
dotnet run
```

Then open the URL it prints (typically `https://localhost:5001` or similar). First run creates
the schema and prints a one-time **Super Admin** login to the console — copy it down, it's not
shown again:
```
Username: superadmin
Password: <randomly generated>
```
Sign in with it, then create your first client from **Clients → +** — that's what seeds a
tenant's default 3 houses, its Admin role, and its first admin login. See
[Multi-tenancy](#multi-tenancy) below.

## Multi-tenancy

This install is shared by multiple clients ("tenants"), isolated from each other in one database:

- **Super Admin** (`/superadmin`, the account seeded on first run above) creates/deactivates
  clients and can reset a client's admin password — nothing else. It deliberately never sees any
  tenant's houses, batches, or other farm data (`Services/PlatformAdminService.cs`).
- **Tenant Admin** (the user created when a client is provisioned) manages their own houses,
  batches, users, roles — the same feature set this app always had — plus **Settings →
  Branding**, where they can rename the app, upload a logo and pick brand colors for their own
  tenant only.
- Every login goes through the same shared `/Account/Login` page; which tenant (or the platform)
  a user lands in is resolved server-side after authenticating, not by subdomain or URL.
- Isolation is enforced by an EF Core global query filter keyed on `FarmDbContext.TenantId`
  (`Data/FarmDbContext.cs`) — fail-closed, so a context with no tenant set reads every
  tenant-scoped table as empty rather than as "everything." New tenant-scoped rows get their
  `TenantId` stamped automatically on save; see the comments in `FarmDbContext` for the details.

## Project layout

```
AmrPoultryFarmWeb/
  Program.cs                  — DB provider selection, cookie auth, login/logout/tenant-logo
                                 endpoints, migration + Super Admin bootstrap
  Migrations/                  — EF Core migrations (SQL Server path)
  Data/FarmDbContext.cs       — EF Core model, tenant query filters + TenantId auto-stamp
  Data/ITenantScoped.cs       — marker interface for every tenant-owned entity
  Models/Tenant.cs            — client row: name, logo, brand colors, active flag
  Models/FarmModels.cs        — Batch, DailyRecord, FeedDelivery, HealthEvent, Lifting, Settlement, Expense, House, Integrator, User, Role...
  Models/Permissions.cs       — permission-code catalog used by the role editor
  Services/
    FarmService.cs            — CRUD + FCR/EEF/livability performance calculations + tenant branding save
    AuthService.cs            — login validation, tenant/permission/session snapshot, user & role CRUD
    PlatformAdminService.cs   — Super Admin bootstrap + client create/activate/password-reset
    PasswordHasher.cs         — PBKDF2 hashing
  Components/
    App.razor, Routes.razor   — host page / router (with auth-aware routing)
    RedirectToLogin.razor     — sends unauthenticated visitors to /Account/Login
    Account/Login.razor       — shared sign-in screen (same login for every tenant + Super Admin)
    Layout/MainLayout.razor   — tenant-branded header + nav shell (name/logo/colors)
    Layout/SuperAdminLayout.razor — platform admin shell, no farm nav
    Layout/EmptyLayout.razor  — bare layout used by the login/error pages
    Pages/                    — Home, Batches, BatchDetail, forms for every record type,
                                 Houses, Integrators, Users, Roles, Expenses, Reports, Settings,
                                 BrandingSettings
    Pages/SuperAdmin/         — client list, create client, client detail
    Shared/                   — BatchRow, Dropdown, PoultryHouseArt, BarChart, LineChart
  wwwroot/
    css/app.css                — full design system (colors, cards, KPIs, forms, charts, nav)
    images/                    — logo, login background
    js/ui.js                   — small dropdown-blur helper
```

## Permissions model

Same as the original app: permission codes are grouped by module (`Houses.View`,
`Batches.Add`, `Users.Edit`, ...) in `Models/Permissions.cs`. Roles hold a subset of codes;
users hold one or more roles plus an explicit list of houses they can see (the built-in
**Admin** role always sees everything, everyone else needs houses assigned or they see
nothing house-related). Manage all of this under **Settings → Roles / Users / Houses**.

## Notes / next steps you may want

- **HTTPS/reverse proxy**: for a real deployment, put this behind IIS or a reverse proxy
  (nginx/Caddy) with a real TLS certificate rather than relying on the Kestrel dev cert.
- **Backups**: with SQL Server, use your normal SQL Server backup/maintenance plan instead of
  copying a file, as the old SQLite version needed.
- **Concurrent editing**: this is now a shared multi-user app — two people editing the same
  batch at once will simply have the later save win (no optimistic-concurrency conflict
  handling was added). Fine for a small farm team; worth revisiting if usage grows.
- **`ConnectionStrings:Farm` in `appsettings.json`**: this repo currently has a real connection
  string (host + credentials) committed in plain text. Rotate that password and move the real
  value to `dotnet user-secrets` or an environment variable before treating this repo as public
  or handing it to anyone else — see the "Don't commit a real password" note above.
