using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace AmrPoultryFarmWeb.Data;

/// <summary>
/// Local-dev SQLite databases are built with EnsureCreated, which never changes an existing file.
/// This brings an older dev database up to the current model: creates any tables (and their
/// indexes) that are missing, straight from EF's own create script, and adds the few columns
/// introduced on existing tables since. SQL Server doesn't use this — it has real migrations.
/// </summary>
public static class SqliteSchemaUpgrader
{
    // (table, column, SQLite definition) added to tables that already existed in older dev databases.
    private static readonly (string Table, string Column, string Definition)[] AddedColumns =
    {
        ("Users", "PreferredLanguage", "TEXT NOT NULL DEFAULT 'en'"),
        ("Integrators", "IsActive", "INTEGER NOT NULL DEFAULT 1"),
        ("Integrators", "ChickCostPerBird", "TEXT NOT NULL DEFAULT '0.0'"),
        ("Integrators", "DefaultBreed", "TEXT NOT NULL DEFAULT 'Cobb 430Y'"),
        ("Integrators", "TargetWeightKg", "TEXT NOT NULL DEFAULT '2.3'"),
        ("Integrators", "GrowingChargePerKg", "TEXT NOT NULL DEFAULT '8.0'"),
        ("Integrators", "StandardFcr", "TEXT NOT NULL DEFAULT '1.7'"),
        ("Integrators", "FcrIncentivePerKg", "TEXT NOT NULL DEFAULT '0.0'"),
        ("Integrators", "MortalityAllowancePct", "TEXT NOT NULL DEFAULT '5.0'"),
        ("Integrators", "MortalityDeductionPerBird", "TEXT NOT NULL DEFAULT '0.0'"),
        ("Integrators", "PaymentDays", "INTEGER NOT NULL DEFAULT 15"),
    };

    // Same data move as the SQL Server MoveIntegratorsToPlatform migration: per-client integrators
    // become one platform list (same-name ones merged), and each client keeps access to its own.
    private static readonly string[] MoveIntegratorsToPlatform =
    {
        """
        INSERT OR IGNORE INTO "TenantIntegrators" ("TenantId", "IntegratorId")
        SELECT DISTINCT i."TenantId",
               (SELECT MIN(k."Id") FROM "Integrators" k WHERE LOWER(TRIM(k."Name")) = LOWER(TRIM(i."Name")))
        FROM "Integrators" i;
        """,
        """
        UPDATE "Batches" SET "IntegratorId" =
            (SELECT MIN(k."Id") FROM "Integrators" k JOIN "Integrators" i ON LOWER(TRIM(k."Name")) = LOWER(TRIM(i."Name"))
             WHERE i."Id" = "Batches"."IntegratorId")
        WHERE "IntegratorId" IN (SELECT "Id" FROM "Integrators");
        """,
        """
        DELETE FROM "Integrators" WHERE "Id" NOT IN (SELECT MIN("Id") FROM "Integrators" GROUP BY LOWER(TRIM("Name")));
        """,
        """DROP INDEX IF EXISTS "IX_Integrators_TenantId";""",
        """ALTER TABLE "Integrators" DROP COLUMN "TenantId";""",
    };

    public static async Task UpgradeAsync(FarmDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) existing.Add(reader.GetString(0));
            }

            // Missing tables + their indexes, from the model's own DDL.
            var statements = db.Database.GenerateCreateScript()
                .Split(";", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sql in statements)
            {
                var table = Regex.Match(sql, "^CREATE TABLE \"([^\"]+)\"");
                if (table.Success && !existing.Contains(table.Groups[1].Value))
                {
                    await ExecAsync(conn, sql);
                    created.Add(table.Groups[1].Value);
                }
            }
            foreach (var sql in statements)
            {
                var index = Regex.Match(sql, "^CREATE (UNIQUE )?INDEX \"[^\"]+\" ON \"([^\"]+)\"");
                if (index.Success && created.Contains(index.Groups[2].Value))
                    await ExecAsync(conn, sql);
            }

            foreach (var (table, column, definition) in AddedColumns)
            {
                if (!await ColumnExistsAsync(conn, table, column))
                    await ExecAsync(conn, $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};");
            }

            if (await ColumnExistsAsync(conn, "Integrators", "TenantId"))
            {
                using var tx = await conn.BeginTransactionAsync();
                foreach (var sql in MoveIntegratorsToPlatform)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private static async Task<bool> ColumnExistsAsync(DbConnection conn, string table, string column)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        return Convert.ToInt32(await check.ExecuteScalarAsync()) > 0;
    }

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
