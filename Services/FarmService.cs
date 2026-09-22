using System.Data.Common;
using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AmrPoultryFarmWeb.Services;

public class FarmService
{
    private readonly AuthService authService;
    private readonly IDbContextFactory<FarmDbContext> dbFactory;

    public FarmService(AuthService authService, IDbContextFactory<FarmDbContext> dbFactory)
    {
        this.authService = authService;
        this.dbFactory = dbFactory;
    }

    // A fresh, short-lived DbContext per operation, created via the DI factory (was `new()`
    // against a MAUI-only FileSystem path in the original app), pinned to the caller's current
    // tenant so every ITenantScoped query/write on it is automatically scoped (see
    // FarmDbContext.TenantId). Startup schema/seeding and cross-tenant Super Admin actions don't
    // go through this service at all — see Program.cs and PlatformAdminService.
    private FarmDbContext NewContext()
    {
        var db = dbFactory.CreateDbContext();
        db.TenantId = authService.CurrentTenantId;
        return db;
    }

    // House-level data scoping: the system Admin role always sees everything; every other user
    // sees only houses explicitly assigned to them (empty assignment = sees nothing, not everything).
    private bool CanAccessHouse(int houseId) =>
        authService.IsSystemAdmin || authService.CurrentAssignedHouseIds.Contains(houseId);

    // EnsureCreated only builds a fresh schema for a brand-new database file — it never
    // reconciles an existing one. Older installs may still have pre-refactor columns (free-text
    // "HouseName"/"IntegratorName" on Batches, no lookup tables). This performs that one-time,
    // idempotent upgrade in place, without losing data.
    private static async Task UpgradeSchemaAsync(FarmDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        await UpgradeReferenceColumnAsync(conn, "Houses", """
            CREATE TABLE "Houses" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Houses" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Code" TEXT NULL,
                "CapacityBirds" INTEGER NOT NULL DEFAULT 0,
                "Notes" TEXT NOT NULL DEFAULT '',
                "SortOrder" INTEGER NOT NULL DEFAULT 0
            );
            """, idColumn: "HouseId", legacyTextColumn: "HouseName");

        await UpgradeReferenceColumnAsync(conn, "Integrators", """
            CREATE TABLE "Integrators" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Integrators" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Notes" TEXT NOT NULL DEFAULT '',
                "SortOrder" INTEGER NOT NULL DEFAULT 0
            );
            """, idColumn: "IntegratorId", legacyTextColumn: "IntegratorName");

        using var idxHouse = conn.CreateCommand();
        idxHouse.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_Batches_HouseId\" ON \"Batches\" (\"HouseId\");";
        await idxHouse.ExecuteNonQueryAsync();

        using var idxIntegrator = conn.CreateCommand();
        idxIntegrator.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_Batches_IntegratorId\" ON \"Batches\" (\"IntegratorId\");";
        await idxIntegrator.ExecuteNonQueryAsync();

        // Login/roles are brand-new tables — no legacy column to migrate away from, just create
        // them if this is an existing db that predates this feature (EnsureCreated already
        // handles brand-new installs via the EF model).
        await EnsureTableAsync(conn, "Users", """
            CREATE TABLE "Users" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT,
                "Username" TEXT NOT NULL,
                "PasswordHash" TEXT NOT NULL,
                "PasswordSalt" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL DEFAULT '',
                "IsActive" INTEGER NOT NULL DEFAULT 1
            );
            """);
        using (var idxUser = conn.CreateCommand())
        {
            idxUser.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Users_Username\" ON \"Users\" (\"Username\");";
            await idxUser.ExecuteNonQueryAsync();
        }

        await EnsureTableAsync(conn, "Roles", """
            CREATE TABLE "Roles" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Roles" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "IsSystemRole" INTEGER NOT NULL DEFAULT 0,
                "SortOrder" INTEGER NOT NULL DEFAULT 0
            );
            """);

        await EnsureTableAsync(conn, "UserRoles", """
            CREATE TABLE "UserRoles" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UserRoles" PRIMARY KEY AUTOINCREMENT,
                "UserId" INTEGER NOT NULL,
                "RoleId" INTEGER NOT NULL
            );
            """);
        using (var idxUserRole = conn.CreateCommand())
        {
            idxUserRole.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UserRoles_UserId_RoleId\" ON \"UserRoles\" (\"UserId\", \"RoleId\");";
            await idxUserRole.ExecuteNonQueryAsync();
        }

        await EnsureTableAsync(conn, "RolePermissions", """
            CREATE TABLE "RolePermissions" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_RolePermissions" PRIMARY KEY AUTOINCREMENT,
                "RoleId" INTEGER NOT NULL,
                "PermissionCode" TEXT NOT NULL
            );
            """);
        using (var idxRolePerm = conn.CreateCommand())
        {
            idxRolePerm.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RolePermissions_RoleId_PermissionCode\" ON \"RolePermissions\" (\"RoleId\", \"PermissionCode\");";
            await idxRolePerm.ExecuteNonQueryAsync();
        }

        await EnsureTableAsync(conn, "UserHouses", """
            CREATE TABLE "UserHouses" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UserHouses" PRIMARY KEY AUTOINCREMENT,
                "UserId" INTEGER NOT NULL,
                "HouseId" INTEGER NOT NULL
            );
            """);
        using (var idxUserHouse = conn.CreateCommand())
        {
            idxUserHouse.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UserHouses_UserId_HouseId\" ON \"UserHouses\" (\"UserId\", \"HouseId\");";
            await idxUserHouse.ExecuteNonQueryAsync();
        }

        await EnsureColumnAsync(conn, "Batches", "BranchCode", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(conn, "Integrators", "BagWeightKg", "TEXT NOT NULL DEFAULT '50'");
        await EnsureColumnAsync(conn, "DailyRecords", "FeedConsumedBags", "TEXT NOT NULL DEFAULT '0'");
    }

    private static async Task EnsureTableAsync(DbConnection conn, string table, string createTableSql)
    {
        if (await TableExistsAsync(conn, table)) return;
        using var create = conn.CreateCommand();
        create.CommandText = createTableSql;
        await create.ExecuteNonQueryAsync();
    }

    // For a plain additive column with no legacy predecessor to migrate away from — just add it
    // with a default so existing rows stay valid.
    private static async Task EnsureColumnAsync(DbConnection conn, string table, string column, string columnDefSql)
    {
        if (await ColumnExistsAsync(conn, table, column)) return;
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {columnDefSql};";
        await alter.ExecuteNonQueryAsync();
    }

    // Ensures a lookup table (Houses/Integrators) exists, adds the FK id column to Batches if
    // missing, and — if a legacy free-text column is still present — backfills lookup rows from
    // its distinct values and drops it (it's NOT NULL at the SQLite level with no default, so
    // leaving it behind breaks every insert that no longer populates it).
    private static async Task UpgradeReferenceColumnAsync(DbConnection conn, string refTable,
        string createTableSql, string idColumn, string legacyTextColumn)
    {
        if (!await TableExistsAsync(conn, refTable))
        {
            using var create = conn.CreateCommand();
            create.CommandText = createTableSql;
            await create.ExecuteNonQueryAsync();
        }

        var idColumnIsNew = !await ColumnExistsAsync(conn, "Batches", idColumn);
        if (idColumnIsNew)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"Batches\" ADD COLUMN \"{idColumn}\" INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync();
        }

        if (await ColumnExistsAsync(conn, "Batches", legacyTextColumn))
        {
            if (idColumnIsNew)
                await BackfillReferenceIdsFromLegacyTextAsync(conn, refTable, legacyTextColumn, idColumn);

            using var drop = conn.CreateCommand();
            drop.CommandText = $"ALTER TABLE \"Batches\" DROP COLUMN \"{legacyTextColumn}\";";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task BackfillReferenceIdsFromLegacyTextAsync(DbConnection conn, string refTable,
        string legacyTextColumn, string idColumn)
    {
        var names = new List<string>();
        using (var select = conn.CreateCommand())
        {
            select.CommandText = $"SELECT DISTINCT \"{legacyTextColumn}\" FROM \"Batches\" WHERE \"{legacyTextColumn}\" IS NOT NULL AND TRIM(\"{legacyTextColumn}\") <> '';";
            using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        }

        int order = 0;
        foreach (var name in names)
        {
            order++;
            int refId;
            using (var find = conn.CreateCommand())
            {
                find.CommandText = $"SELECT \"Id\" FROM \"{refTable}\" WHERE \"Name\" = $name;";
                find.Parameters.Add(new SqliteParameter("$name", name));
                var existing = await find.ExecuteScalarAsync();
                if (existing is not null)
                {
                    refId = Convert.ToInt32(existing);
                }
                else
                {
                    using var insert = conn.CreateCommand();
                    insert.CommandText = $"INSERT INTO \"{refTable}\" (\"Name\", \"Notes\", \"SortOrder\") VALUES ($name, '', $order); SELECT last_insert_rowid();";
                    insert.Parameters.Add(new SqliteParameter("$name", name));
                    insert.Parameters.Add(new SqliteParameter("$order", order));
                    refId = Convert.ToInt32(await insert.ExecuteScalarAsync());
                }
            }

            using var update = conn.CreateCommand();
            update.CommandText = $"UPDATE \"Batches\" SET \"{idColumn}\" = $rid WHERE \"{legacyTextColumn}\" = $name;";
            update.Parameters.Add(new SqliteParameter("$rid", refId));
            update.Parameters.Add(new SqliteParameter("$name", name));
            await update.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(DbConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$t;";
        cmd.Parameters.Add(new SqliteParameter("$t", table));
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> ColumnExistsAsync(DbConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = await cmd.ExecuteReaderAsync();
        var nameOrdinal = -1;
        while (await reader.ReadAsync())
        {
            if (nameOrdinal < 0) nameOrdinal = reader.GetOrdinal("name");
            if (string.Equals(reader.GetString(nameOrdinal), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---------------- Houses ----------------
    public async Task<List<House>> GetHousesAsync()
    {
        using var db = NewContext();
        var houses = await db.Houses.OrderBy(h => h.SortOrder).ThenBy(h => h.Name).ToListAsync();
        return authService.IsSystemAdmin
            ? houses
            : houses.Where(h => authService.CurrentAssignedHouseIds.Contains(h.Id)).ToList();
    }

    public async Task<House?> GetHouseAsync(int id)
    {
        if (!CanAccessHouse(id)) return null;
        using var db = NewContext();
        return await db.Houses.FirstOrDefaultAsync(h => h.Id == id);
    }

    public async Task<int> SaveHouseAsync(House house)
    {
        using var db = NewContext();
        if (house.Id == 0) db.Houses.Add(house);
        else db.Houses.Update(house);
        await db.SaveChangesAsync();
        return house.Id;
    }

    /// <summary>Returns false (and does not delete) if the house still has batches attached.</summary>
    public async Task<bool> DeleteHouseAsync(int houseId)
    {
        using var db = NewContext();
        if (await db.Batches.AnyAsync(b => b.HouseId == houseId)) return false;
        var house = await db.Houses.FindAsync(houseId);
        if (house is null) return false;
        db.Houses.Remove(house);
        await db.SaveChangesAsync();
        return true;
    }

    // ---------------- Integrators ----------------
    public async Task<List<Integrator>> GetIntegratorsAsync()
    {
        using var db = NewContext();
        return await db.Integrators.OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync();
    }

    public async Task<Integrator?> GetIntegratorAsync(int id)
    {
        using var db = NewContext();
        return await db.Integrators.FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<int> SaveIntegratorAsync(Integrator integrator)
    {
        using var db = NewContext();
        if (integrator.Id == 0) db.Integrators.Add(integrator);
        else db.Integrators.Update(integrator);
        await db.SaveChangesAsync();
        return integrator.Id;
    }

    /// <summary>Returns false (and does not delete) if the integrator still has batches attached.</summary>
    public async Task<bool> DeleteIntegratorAsync(int integratorId)
    {
        using var db = NewContext();
        if (await db.Batches.AnyAsync(b => b.IntegratorId == integratorId)) return false;
        var integrator = await db.Integrators.FindAsync(integratorId);
        if (integrator is null) return false;
        db.Integrators.Remove(integrator);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Unscoped (deliberately ignores house scoping) — used for delete-safety checks, which
    /// must consider every batch system-wide, not just the ones the current viewer can see.</summary>
    public async Task<int> CountBatchesForIntegratorAsync(int integratorId)
    {
        using var db = NewContext();
        return await db.Batches.CountAsync(b => b.IntegratorId == integratorId);
    }

    // ---------------- Batches ----------------
    public async Task<List<int>> GetBatchYearsAsync()
    {
        using var db = NewContext();
        var q = db.Batches.AsQueryable();
        if (!authService.IsSystemAdmin)
        {
            var allowed = authService.CurrentAssignedHouseIds;
            q = q.Where(b => allowed.Contains(b.HouseId));
        }
        return await q.Select(b => b.PlacementDate.Year).Distinct()
            .OrderByDescending(y => y).ToListAsync();
    }

    public async Task<List<Batch>> GetBatchesAsync(bool activeOnly = false, int? houseId = null,
        int? year = null, BatchStatus? status = null, string? search = null)
    {
        using var db = NewContext();
        var q = db.Batches.Include(b => b.House).Include(b => b.Integrator).AsQueryable();
        if (activeOnly) q = q.Where(b => b.Status == BatchStatus.Active);
        if (status.HasValue) q = q.Where(b => b.Status == status);
        if (houseId.HasValue) q = q.Where(b => b.HouseId == houseId);
        if (year.HasValue) q = q.Where(b => b.PlacementDate.Year == year);
        if (!authService.IsSystemAdmin)
        {
            var allowed = authService.CurrentAssignedHouseIds;
            q = q.Where(b => allowed.Contains(b.HouseId));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(b => EF.Functions.Like(b.BatchCode, $"%{s}%")
                           || (b.Integrator != null && EF.Functions.Like(b.Integrator.Name, $"%{s}%")));
        }
        return await q.OrderByDescending(b => b.PlacementDate).ToListAsync();
    }

    public async Task<Batch?> GetBatchAsync(int id)
    {
        using var db = NewContext();
        var batch = await db.Batches.Include(b => b.House).Include(b => b.Integrator).FirstOrDefaultAsync(b => b.Id == id);
        if (batch is not null && !CanAccessHouse(batch.HouseId)) return null;
        return batch;
    }

    public async Task<int> SaveBatchAsync(Batch batch)
    {
        using var db = NewContext();
        if (batch.Id == 0) db.Batches.Add(batch);
        else db.Batches.Update(batch);
        await db.SaveChangesAsync();
        return batch.Id;
    }

    public async Task CloseBatchAsync(int batchId)
    {
        using var db = NewContext();
        var batch = await db.Batches.FindAsync(batchId);
        if (batch is null) return;
        batch.Status = BatchStatus.Closed;
        batch.ClosedDate = DateTime.Today;
        await db.SaveChangesAsync();
    }

    public async Task DeleteBatchAsync(int batchId)
    {
        using var db = NewContext();
        var batch = await db.Batches.FindAsync(batchId);
        if (batch is null) return;
        db.Batches.Remove(batch);
        await db.SaveChangesAsync();
    }

    // ---------------- Daily records ----------------
    public async Task<List<DailyRecord>> GetDailyRecordsAsync(int batchId)
    {
        using var db = NewContext();
        return await db.DailyRecords.Where(d => d.BatchId == batchId)
            .OrderByDescending(d => d.Date).ToListAsync();
    }

    public async Task<int> SaveDailyRecordAsync(DailyRecord record)
    {
        using var db = NewContext();
        if (record.Id == 0) db.DailyRecords.Add(record);
        else db.DailyRecords.Update(record);
        await db.SaveChangesAsync();
        return record.Id;
    }

    public async Task DeleteDailyRecordAsync(int id)
    {
        using var db = NewContext();
        var rec = await db.DailyRecords.FindAsync(id);
        if (rec is null) return;
        db.DailyRecords.Remove(rec);
        await db.SaveChangesAsync();
    }

    // ---------------- Feed deliveries ----------------
    public async Task<List<FeedDelivery>> GetFeedDeliveriesAsync(int batchId)
    {
        using var db = NewContext();
        return await db.FeedDeliveries.Where(f => f.BatchId == batchId)
            .OrderByDescending(f => f.Date).ToListAsync();
    }

    public async Task<int> SaveFeedDeliveryAsync(FeedDelivery feed)
    {
        using var db = NewContext();
        if (feed.Id == 0) db.FeedDeliveries.Add(feed);
        else db.FeedDeliveries.Update(feed);
        await db.SaveChangesAsync();
        return feed.Id;
    }

    public async Task DeleteFeedDeliveryAsync(int id)
    {
        using var db = NewContext();
        var f = await db.FeedDeliveries.FindAsync(id);
        if (f is null) return;
        db.FeedDeliveries.Remove(f);
        await db.SaveChangesAsync();
    }

    // ---------------- Health events ----------------
    public async Task<List<HealthEvent>> GetHealthEventsAsync(int batchId)
    {
        using var db = NewContext();
        return await db.HealthEvents.Where(h => h.BatchId == batchId)
            .OrderByDescending(h => h.Date).ToListAsync();
    }

    public async Task<int> SaveHealthEventAsync(HealthEvent evt)
    {
        using var db = NewContext();
        if (evt.Id == 0) db.HealthEvents.Add(evt);
        else db.HealthEvents.Update(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    public async Task DeleteHealthEventAsync(int id)
    {
        using var db = NewContext();
        var h = await db.HealthEvents.FindAsync(id);
        if (h is null) return;
        db.HealthEvents.Remove(h);
        await db.SaveChangesAsync();
    }

    // ---------------- Expenses ----------------
    // General farm expenses (BatchId == null) aren't house-specific and stay visible to anyone
    // with Expenses.View; batch-linked expenses are scoped through the batch's house.
    public async Task<List<Expense>> GetExpensesAsync(int? batchId)
    {
        using var db = NewContext();
        var q = db.Expenses.Include(e => e.Batch).AsQueryable();
        q = batchId.HasValue ? q.Where(e => e.BatchId == batchId) : q;
        if (!authService.IsSystemAdmin)
        {
            var allowed = authService.CurrentAssignedHouseIds;
            q = q.Where(e => e.Batch == null || allowed.Contains(e.Batch.HouseId));
        }
        return await q.OrderByDescending(e => e.Date).ToListAsync();
    }

    public async Task<List<Expense>> GetAllExpensesAsync()
    {
        using var db = NewContext();
        var q = db.Expenses.Include(e => e.Batch).AsQueryable();
        if (!authService.IsSystemAdmin)
        {
            var allowed = authService.CurrentAssignedHouseIds;
            q = q.Where(e => e.Batch == null || allowed.Contains(e.Batch.HouseId));
        }
        return await q.OrderByDescending(e => e.Date).ToListAsync();
    }

    public async Task<int> SaveExpenseAsync(Expense expense)
    {
        using var db = NewContext();
        if (expense.Id == 0) db.Expenses.Add(expense);
        else db.Expenses.Update(expense);
        await db.SaveChangesAsync();
        return expense.Id;
    }

    public async Task DeleteExpenseAsync(int id)
    {
        using var db = NewContext();
        var e = await db.Expenses.FindAsync(id);
        if (e is null) return;
        db.Expenses.Remove(e);
        await db.SaveChangesAsync();
    }

    // ---------------- Liftings ----------------
    public async Task<List<Lifting>> GetLiftingsAsync(int batchId)
    {
        using var db = NewContext();
        return await db.Liftings.Where(l => l.BatchId == batchId)
            .OrderByDescending(l => l.Date).ToListAsync();
    }

    public async Task<int> SaveLiftingAsync(Lifting lifting)
    {
        using var db = NewContext();
        if (lifting.Id == 0) db.Liftings.Add(lifting);
        else db.Liftings.Update(lifting);
        await db.SaveChangesAsync();
        return lifting.Id;
    }

    public async Task DeleteLiftingAsync(int id)
    {
        using var db = NewContext();
        var l = await db.Liftings.FindAsync(id);
        if (l is null) return;
        db.Liftings.Remove(l);
        await db.SaveChangesAsync();
    }

    // ---------------- Settlement ----------------
    public async Task<Settlement?> GetSettlementAsync(int batchId)
    {
        using var db = NewContext();
        return await db.Settlements.FirstOrDefaultAsync(s => s.BatchId == batchId);
    }

    public async Task<int> SaveSettlementAsync(Settlement settlement)
    {
        using var db = NewContext();
        if (settlement.Id == 0) db.Settlements.Add(settlement);
        else db.Settlements.Update(settlement);
        await db.SaveChangesAsync();
        return settlement.Id;
    }

    // ---------------- Performance calculation ----------------
    public async Task<BatchPerformance> GetPerformanceAsync(int batchId)
    {
        using var db = NewContext();
        var batch = await db.Batches.FindAsync(batchId);
        if (batch is null) return new BatchPerformance();

        var daily = await db.DailyRecords.Where(d => d.BatchId == batchId).ToListAsync();
        var feed = await db.FeedDeliveries.Where(f => f.BatchId == batchId).ToListAsync();
        var lifts = await db.Liftings.Where(l => l.BatchId == batchId).ToListAsync();
        var expenses = await db.Expenses.Where(e => e.BatchId == batchId).ToListAsync();

        int mortality = daily.Sum(d => d.Mortality);
        int culls = daily.Sum(d => d.Culls);
        int liftedBirds = lifts.Sum(l => l.BirdsLifted);
        int liveBirds = Math.Max(0, batch.ChicksPlaced - mortality - culls - liftedBirds);

        decimal feedReceived = feed.Sum(f => f.TotalKg);
        decimal feedConsumed = daily.Sum(d => d.FeedConsumedKg);
        decimal feedStock = feedReceived - feedConsumed;

        decimal liftedWeight = lifts.Sum(l => l.TotalWeightKg);
        // last sampled body weight (gm) converted to kg, used to estimate live-bird weight if not yet lifted
        decimal lastAvgWtGm = daily.OrderByDescending(d => d.Date).FirstOrDefault()?.AvgBodyWeightGm ?? 0;
        decimal estimatedLiveWeight = liveBirds * (lastAvgWtGm / 1000m);
        decimal totalLiveWeightKg = liftedWeight + estimatedLiveWeight;

        int ageDays = batch.AgeDays == 0 ? 1 : batch.AgeDays;
        decimal avgBodyWeightKg = lastAvgWtGm > 0 ? lastAvgWtGm / 1000m
            : (liftedBirds > 0 ? lifts.Sum(l => l.AvgWeightKg) / lifts.Count : 0);

        int startedBirds = batch.ChicksPlaced;
        decimal mortalityPct = startedBirds > 0 ? Math.Round((decimal)(mortality + culls) / startedBirds * 100, 2) : 0;
        decimal livabilityPct = 100 - mortalityPct;

        // FCR = total feed consumed (kg) / total live weight produced (kg)
        decimal fcr = totalLiveWeightKg > 0 ? Math.Round(feedConsumed / totalLiveWeightKg, 3) : 0;

        // EEF (European Efficiency Factor) = (Livability% x Avg body wt in kg x 100) / (Age in days x FCR)
        decimal eef = (ageDays > 0 && fcr > 0)
            ? Math.Round((livabilityPct * avgBodyWeightKg * 100) / (ageDays * fcr), 1)
            : 0;

        decimal dailyGainGm = ageDays > 0 ? Math.Round((avgBodyWeightKg * 1000m) / ageDays, 1) : 0;

        return new BatchPerformance
        {
            AgeDays = batch.AgeDays,
            ChicksPlaced = startedBirds,
            TotalMortality = mortality,
            TotalCulls = culls,
            BirdsLifted = liftedBirds,
            LiveBirds = liveBirds,
            MortalityPct = mortalityPct,
            LivabilityPct = livabilityPct,
            FeedReceivedKg = feedReceived,
            FeedConsumedKg = feedConsumed,
            FeedStockKg = feedStock,
            AvgBodyWeightKg = avgBodyWeightKg,
            TotalLiveWeightKg = totalLiveWeightKg,
            Fcr = fcr,
            Eef = eef,
            DailyGainGm = dailyGainGm,
            TotalExpenses = expenses.Sum(e => e.Amount)
        };
    }

    // ---------------- Tenant branding ----------------
    // Tenant itself isn't ITenantScoped (it's the row that defines a tenant, not one that belongs
    // to one), so it carries no query filter — these methods always target the caller's own
    // CurrentTenantId explicitly rather than trusting any id a form might pass in, so a tenant
    // admin can never edit another tenant's branding.
    public async Task<int> SaveTenantBrandingAsync(string name, byte[]? logoBytes, string? logoContentType,
        string primaryColorHex, string accentColorHex)
    {
        if (authService.CurrentTenantId is not { } tenantId)
            throw new InvalidOperationException("No current tenant.");

        using var db = NewContext();
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId);
        tenant.Name = name;
        if (logoBytes is not null)
        {
            tenant.LogoBytes = logoBytes;
            tenant.LogoContentType = logoContentType;
        }
        tenant.PrimaryColorHex = primaryColorHex;
        tenant.AccentColorHex = accentColorHex;
        await db.SaveChangesAsync();
        return tenant.Id;
    }
}
