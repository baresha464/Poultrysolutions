using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace AmrPoultryFarmWeb.Services;

/// <summary>Dev-only helper behind `dotnet run -- seed-demo`: provisions a tenant the normal way
/// (via PlatformAdminService, so it gets the same houses/Admin role/admin login every real client
/// gets — nothing here bypasses tenant isolation) and fills it with a few realistic batches so the
/// UI isn't empty on a fresh database. Never invoked from the running app's request pipeline.</summary>
public class DemoDataSeeder
{
    private readonly IDbContextFactory<FarmDbContext> dbFactory;
    private readonly PlatformAdminService platformAdmin;

    public DemoDataSeeder(IDbContextFactory<FarmDbContext> dbFactory, PlatformAdminService platformAdmin)
    {
        this.dbFactory = dbFactory;
        this.platformAdmin = platformAdmin;
    }

    public async Task<TenantProvisionResult> SeedAsync(string? tenantName = null)
    {
        var name = string.IsNullOrWhiteSpace(tenantName) ? "Demo Farm" : tenantName;
        using (var check = dbFactory.CreateDbContext())
        {
            // Never collide with an existing tenant's name (and never reprint a password we no
            // longer have) — just provision a fresh, distinctly-named demo tenant instead.
            if (await check.Tenants.AnyAsync(t => t.Name == name))
                name = $"{name} {DateTime.UtcNow:yyyy-MM-dd HHmmss}";
        }

        var provision = await platformAdmin.CreateTenantAsync(name, "admin", null);

        using var db = dbFactory.CreateDbContext();
        db.TenantId = provision.Tenant.Id;

        var houses = await db.Houses.OrderBy(h => h.SortOrder).ToListAsync();

        var integrator = new Integrator { Name = "Skylark Foods", BagWeightKg = 50m, SortOrder = 1 };
        db.Integrators.Add(integrator);
        await db.SaveChangesAsync();

        var rng = new Random(12345);

        // One active batch well into its cycle, one fully closed with a lifting/settlement, and
        // one just placed — covers the shapes the UI needs to render (in-progress KPIs, a
        // finished settlement, and a near-empty daily-record list).
        await SeedBatchAsync(db, rng, houses[0], integrator, placedDaysAgo: 20, closeIt: false, batchCode: "DEMO-2026-01");
        await SeedBatchAsync(db, rng, houses[1], integrator, placedDaysAgo: 45, closeIt: true, batchCode: "DEMO-2026-02");
        await SeedBatchAsync(db, rng, houses[2], integrator, placedDaysAgo: 4, closeIt: false, batchCode: "DEMO-2026-03");

        return provision;
    }

    private static async Task SeedBatchAsync(FarmDbContext db, Random rng, House house, Integrator integrator,
        int placedDaysAgo, bool closeIt, string batchCode)
    {
        var placement = DateTime.Today.AddDays(-placedDaysAgo);
        const int chicksPlaced = 5000;
        var cycleLength = closeIt ? 38 : placedDaysAgo;

        var batch = new Batch
        {
            BatchCode = batchCode,
            HouseId = house.Id,
            IntegratorId = integrator.Id,
            Breed = "Cobb 430Y",
            PlacementDate = placement,
            ChicksPlaced = chicksPlaced,
            ChickCostPerBird = 35m,
            TargetWeightKg = 2.3m,
            Status = closeIt ? BatchStatus.Closed : BatchStatus.Active,
            ClosedDate = closeIt ? placement.AddDays(cycleLength) : null,
        };
        db.Batches.Add(batch);
        await db.SaveChangesAsync(); // need batch.Id before adding its children below

        db.FeedDeliveries.AddRange(
            new FeedDelivery { BatchId = batch.Id, Date = placement, FeedType = "Pre-starter", Bags = 20, BagWeightKg = 50m, DcNumber = "DC-001" },
            new FeedDelivery { BatchId = batch.Id, Date = placement.AddDays(7), FeedType = "Starter", Bags = 60, BagWeightKg = 50m, DcNumber = "DC-002" });
        if (cycleLength > 20)
            db.FeedDeliveries.Add(new FeedDelivery { BatchId = batch.Id, Date = placement.AddDays(20), FeedType = "Finisher", Bags = 100, BagWeightKg = 50m, DcNumber = "DC-003" });

        db.HealthEvents.AddRange(
            new HealthEvent { BatchId = batch.Id, Date = placement.AddDays(1), Type = HealthEventType.Vaccination, Name = "ND Lasota", Dose = "1 drop/bird", Route = "Eye drop", Cost = 800 },
            new HealthEvent { BatchId = batch.Id, Date = placement.AddDays(10), Type = HealthEventType.Vaccination, Name = "IBD", Dose = "1 ml/ltr", Route = "Drinking water", Cost = 600 });

        db.Expenses.AddRange(
            new Expense { BatchId = batch.Id, Date = placement, Category = "Litter", Description = "Rice husk bedding", Amount = 4500 },
            new Expense { BatchId = batch.Id, Date = placement.AddDays(5), Category = "Electricity", Description = "Brooding power", Amount = 3200 });

        var alive = chicksPlaced;
        decimal bodyWeightGm = 42; // approx. day-old chick weight
        var daysToSeed = Math.Min(cycleLength, DateTime.Today.Subtract(placement).Days);
        for (var day = 1; day <= daysToSeed; day++)
        {
            var mortality = rng.Next(0, 4);
            var culls = day % 7 == 0 ? rng.Next(0, 2) : 0;
            alive -= mortality + culls;
            bodyWeightGm += 55 + rng.Next(-5, 10);
            var feedBags = Math.Round(2m + day * 0.35m + (decimal)rng.NextDouble(), 1);

            db.DailyRecords.Add(new DailyRecord
            {
                BatchId = batch.Id,
                Date = placement.AddDays(day),
                Mortality = mortality,
                Culls = culls,
                FeedConsumedBags = feedBags,
                FeedConsumedKg = feedBags * integrator.BagWeightKg,
                AvgBodyWeightGm = Math.Round(bodyWeightGm, 0),
                WaterConsumedLtr = Math.Round(alive * 0.2m, 1),
                MinTempC = 24 + rng.Next(0, 3),
                MaxTempC = 30 + rng.Next(0, 4),
                HumidityPct = 55 + rng.Next(0, 15),
            });
        }

        if (closeIt)
        {
            var avgWeightKg = bodyWeightGm / 1000m;
            var liftedWeight = Math.Round(alive * avgWeightKg, 1);
            db.Liftings.Add(new Lifting
            {
                BatchId = batch.Id,
                Date = batch.ClosedDate!.Value,
                BirdsLifted = alive,
                TotalWeightKg = liftedWeight,
                VehicleNumber = "AP-16-TZ-4521",
                DcNumber = "LIFT-001",
            });
            db.Settlements.Add(new Settlement
            {
                BatchId = batch.Id,
                Date = batch.ClosedDate!.Value,
                GrowingChargePerKg = 8.5m,
                PerformanceIncentive = 1500m,
                Deductions = 300m,
                AmountReceived = Math.Round(liftedWeight * 8.5m + 1500m - 300m, 0),
            });
        }

        await db.SaveChangesAsync();
    }
}
