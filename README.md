# AMR Poultry Farms — Web

ASP.NET Core Blazor Web App (.NET 8, Interactive Server render mode) for tracking broiler
batches, daily logs, feed, health events, lifting/harvest, settlement and expenses across
multiple houses — with role-based logins for your team.

This is a from-scratch web rebuild of the original `AmrPoultryFarm` .NET MAUI Blazor Hybrid
app: the same business logic and data model (batches, daily records, FCR/EEF performance
calculations, permissions system) now run as a normal ASP.NET Core site with a modern UI, so
anyone on the network can open it in a browser instead of installing a mobile app, and multiple
people can use it — including at the same time — against one shared database.

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
   the app creates the schema automatically on first run (`EnsureCreatedAsync`) and seeds the
   default houses + an `admin` user (see below).
3. For a SQL-authenticated login instead of Windows/trusted auth:
   `Server=YOUR_SERVER;Database=AmrPoultryFarm;User Id=...;Password=...;TrustServerCertificate=True;`
4. Don't commit a real password into `appsettings.json` — use `dotnet user-secrets` locally or
   an environment variable (`ConnectionStrings__Farm`) in deployment.

**Prefer to create the database yourself instead of letting the app auto-create it on first
run?** `Database/CreateAmrPoultryFarmDatabase.sql` has the full schema — every table, foreign
key, index — plus the same seed data (3 houses, the Admin role with every permission, and the
`admin`/`admin` login) the app would otherwise create automatically. Run it with:
```bash
sqlcmd -S YOUR_SERVER -i Database/CreateAmrPoultryFarmDatabase.sql
```
or open it in SQL Server Management Studio / Azure Data Studio and execute it directly. It's
idempotent — safe to run again later (every `CREATE TABLE` is guarded, and the seed inserts
only fire on an empty install).

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
the schema and seeds:
- Three houses: **House 1**, **House 2**, **House 3**
- One admin login: **username `admin`, password `admin`** — holding every permission.

**Change the admin password immediately** (Settings → Users → admin → Reset Password) before
using this anywhere besides your own machine.

## Project layout

```
AmrPoultryFarmWeb/
  Program.cs                  — DB provider selection, cookie auth, login/logout endpoints, seeding
  Data/FarmDbContext.cs       — EF Core model (SQL Server or SQLite via config)
  Models/FarmModels.cs        — Batch, DailyRecord, FeedDelivery, HealthEvent, Lifting, Settlement, Expense, House, Integrator, User, Role...
  Models/Permissions.cs       — permission-code catalog used by the role editor
  Services/
    FarmService.cs            — CRUD + FCR/EEF/livability performance calculations
    AuthService.cs            — login validation, permission/session snapshot, user & role CRUD
    PasswordHasher.cs         — PBKDF2 hashing
  Components/
    App.razor, Routes.razor   — host page / router (with auth-aware routing)
    RedirectToLogin.razor     — sends unauthenticated visitors to /Account/Login
    Account/Login.razor       — sign-in screen
    Layout/MainLayout.razor   — header + bottom nav shell
    Layout/EmptyLayout.razor  — bare layout used by the login/error pages
    Pages/                    — Home, Batches, BatchDetail, forms for every record type,
                                 Houses, Integrators, Users, Roles, Expenses, Reports, Settings
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
- **EF Core Migrations**: the app currently uses `EnsureCreatedAsync()` for simplicity (same as
  the original). If you need to evolve the schema after going live on SQL Server without losing
  data, switch to proper EF Core Migrations (`dotnet ef migrations add ...`) rather than
  `EnsureCreated`.
