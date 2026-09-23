using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmrPoultryFarmWeb.Migrations
{
    /// <summary>
    /// Integrators move from per-client rows to one platform list managed by the Super Admin, with
    /// their contract terms. Existing data is kept: integrators with the same name across clients are
    /// merged into one (lowest Id wins, batches re-pointed), and each client is given access to the
    /// integrators it was already using.
    /// </summary>
    public partial class MoveIntegratorsToPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantIntegrators",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    IntegratorId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantIntegrators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantIntegrators_Integrators_IntegratorId",
                        column: x => x.IntegratorId,
                        principalTable: "Integrators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Access for each client to the (merged) integrators it already had, then merge duplicates.
            migrationBuilder.Sql("""
                INSERT INTO TenantIntegrators (TenantId, IntegratorId)
                SELECT DISTINCT i.TenantId, k.KeepId
                FROM Integrators i
                JOIN (SELECT MIN(Id) AS KeepId, LOWER(LTRIM(RTRIM(Name))) AS N FROM Integrators GROUP BY LOWER(LTRIM(RTRIM(Name)))) k
                  ON k.N = LOWER(LTRIM(RTRIM(i.Name)));

                UPDATE b SET b.IntegratorId = k.KeepId
                FROM Batches b
                JOIN Integrators i ON i.Id = b.IntegratorId
                JOIN (SELECT MIN(Id) AS KeepId, LOWER(LTRIM(RTRIM(Name))) AS N FROM Integrators GROUP BY LOWER(LTRIM(RTRIM(Name)))) k
                  ON k.N = LOWER(LTRIM(RTRIM(i.Name)))
                WHERE b.IntegratorId <> k.KeepId;

                DELETE FROM Integrators
                WHERE Id NOT IN (SELECT MIN(Id) FROM Integrators GROUP BY LOWER(LTRIM(RTRIM(Name))));
                """);

            migrationBuilder.DropIndex(name: "IX_Integrators_TenantId", table: "Integrators");
            migrationBuilder.DropColumn(name: "TenantId", table: "Integrators");

            migrationBuilder.AddColumn<bool>(name: "IsActive", table: "Integrators", type: "bit", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<decimal>(name: "ChickCostPerBird", table: "Integrators", type: "decimal(18,2)", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<string>(name: "DefaultBreed", table: "Integrators", type: "nvarchar(40)", maxLength: 40, nullable: false, defaultValue: "Cobb 430Y");
            migrationBuilder.AddColumn<decimal>(name: "TargetWeightKg", table: "Integrators", type: "decimal(18,2)", nullable: false, defaultValue: 2.3m);
            migrationBuilder.AddColumn<decimal>(name: "GrowingChargePerKg", table: "Integrators", type: "decimal(18,2)", nullable: false, defaultValue: 8m);
            migrationBuilder.AddColumn<decimal>(name: "StandardFcr", table: "Integrators", type: "decimal(18,2)", nullable: false, defaultValue: 1.7m);
            migrationBuilder.AddColumn<decimal>(name: "FcrIncentivePerKg", table: "Integrators", type: "decimal(18,2)", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>(name: "MortalityAllowancePct", table: "Integrators", type: "decimal(18,2)", nullable: false, defaultValue: 5m);
            migrationBuilder.AddColumn<decimal>(name: "MortalityDeductionPerBird", table: "Integrators", type: "decimal(18,2)", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<int>(name: "PaymentDays", table: "Integrators", type: "int", nullable: false, defaultValue: 15);

            migrationBuilder.CreateIndex(
                name: "IX_TenantIntegrators_IntegratorId",
                table: "TenantIntegrators",
                column: "IntegratorId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantIntegrators_TenantId_IntegratorId",
                table: "TenantIntegrators",
                columns: new[] { "TenantId", "IntegratorId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best effort: each integrator goes back to the first client that had access to it.
            migrationBuilder.AddColumn<int>(name: "TenantId", table: "Integrators", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.Sql("""
                UPDATE i SET i.TenantId = t.TenantId
                FROM Integrators i
                JOIN (SELECT IntegratorId, MIN(TenantId) AS TenantId FROM TenantIntegrators GROUP BY IntegratorId) t
                  ON t.IntegratorId = i.Id;
                """);
            migrationBuilder.CreateIndex(name: "IX_Integrators_TenantId", table: "Integrators", column: "TenantId");

            migrationBuilder.DropTable(name: "TenantIntegrators");

            migrationBuilder.DropColumn(name: "IsActive", table: "Integrators");
            migrationBuilder.DropColumn(name: "ChickCostPerBird", table: "Integrators");
            migrationBuilder.DropColumn(name: "DefaultBreed", table: "Integrators");
            migrationBuilder.DropColumn(name: "TargetWeightKg", table: "Integrators");
            migrationBuilder.DropColumn(name: "GrowingChargePerKg", table: "Integrators");
            migrationBuilder.DropColumn(name: "StandardFcr", table: "Integrators");
            migrationBuilder.DropColumn(name: "FcrIncentivePerKg", table: "Integrators");
            migrationBuilder.DropColumn(name: "MortalityAllowancePct", table: "Integrators");
            migrationBuilder.DropColumn(name: "MortalityDeductionPerBird", table: "Integrators");
            migrationBuilder.DropColumn(name: "PaymentDays", table: "Integrators");
        }
    }
}
