using AmrPoultryFarmWeb.Data;
using AmrPoultryFarmWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace AmrPoultryFarmWeb.Services;

/// <summary>Dev-only helper behind `dotnet run -- seed-demo`: provisions a tenant the normal way
/// (via PlatformAdminService, so it gets the same houses/Admin role/admin login every real client
/// gets — nothing here bypasses tenant isolation) and fills it with a realistic farm history, so
/// the dashboards and PDF reports have something meaningful to compare. Never invoked from the
/// running app's request pipeline.
///
/// Each house gets two completed batches plus one batch currently in the house. Completed batches
/// are dealt a random mix of outcomes — a best-in-class flock, average and below-average flocks,
/// and at least one failure (disease outbreak, heat stress or poor brooding) — each simulated day by
/// day against a broiler growth/FCR curve, with multi-day liftings, integrator settlements
/// (incentives/deductions), vaccination programmes and realistic running costs.</summary>
public class DemoDataSeeder
{
    private readonly IDbContextFactory<FarmDbContext> dbFactory;
    private readonly PlatformAdminService platformAdmin;

    public DemoDataSeeder(IDbContextFactory<FarmDbContext> dbFactory, PlatformAdminService platformAdmin)
    {
        this.dbFactory = dbFactory;
        this.platformAdmin = platformAdmin;
    }

    // ---------------- Outcome profiles ----------------
    private enum Scenario { None, Disease, HeatStress, ColdBrooding }

    /// <param name="GrowthEarly">Weight vs standard in week 1-2 (1.0 = on standard).</param>
    /// <param name="GrowthLate">Weight vs standard at the end of the cycle.</param>
    /// <param name="FcrFactor">Feed eaten per kg gained vs standard (lower is better).</param>
    /// <param name="DailyMort">Background daily mortality after week 1, as a fraction of live birds.</param>
    /// <param name="FirstWeekMort">Total first-week loss, as a fraction of chicks placed.</param>
    private record Profile(string Label, double GrowthEarly, double GrowthLate, double FcrFactor,
        double DailyMort, double FirstWeekMort, Scenario Scenario, double Stocking, bool MissesBooster);

    private static readonly Profile Best = new("Best", 1.05, 1.05, 0.94, 0.00025, 0.005, Scenario.None, 0.98, false);
    private static readonly Profile Average = new("Average", 0.99, 0.99, 1.00, 0.00045, 0.009, Scenario.None, 1.00, false);
    private static readonly Profile BelowAverage = new("Below average", 0.95, 0.95, 1.05, 0.0007, 0.013, Scenario.None, 1.03, true);
    private static readonly Profile DiseaseOutbreak = new("Failure - disease", 1.00, 0.86, 1.12, 0.0006, 0.010, Scenario.Disease, 1.00, true);
    private static readonly Profile HeatStress = new("Failure - heat stress", 0.99, 0.88, 1.09, 0.0006, 0.009, Scenario.HeatStress, 1.06, false);
    private static readonly Profile PoorBrooding = new("Failure - poor brooding", 0.80, 0.92, 1.08, 0.0006, 0.032, Scenario.ColdBrooding, 1.00, false);

    // Broiler standard (as-hatched), aligned with FlockAdvisor's benchmark: (day, weight g, cumulative FCR).
    private static readonly (int Day, double Weight, double Fcr)[] Standard =
    {
        (0, 42, 0.00), (7, 180, 0.92), (14, 440, 1.12), (21, 790, 1.32), (28, 1260, 1.48),
        (35, 1800, 1.63), (42, 2300, 1.78), (49, 2750, 1.93),
    };

    private static (double Weight, double Fcr) StdAt(double day)
    {
        for (int i = 1; i < Standard.Length; i++)
        {
            if (day > Standard[i].Day) continue;
            var lo = Standard[i - 1];
            var hi = Standard[i];
            var t = (day - lo.Day) / (hi.Day - lo.Day);
            return (lo.Weight + (hi.Weight - lo.Weight) * t, lo.Fcr + (hi.Fcr - lo.Fcr) * t);
        }
        return (Standard[^1].Weight, Standard[^1].Fcr);
    }

    private static double TargetTempC(int day) => day switch { <= 7 => 33, <= 14 => 30, <= 21 => 27, <= 28 => 24, _ => 22 };

    public async Task<TenantProvisionResult> SeedAsync(string? tenantName = null)
    {
        var name = string.IsNullOrWhiteSpace(tenantName) ? "Demo Farm" : tenantName;
        var username = "admin";
        using (var check = dbFactory.CreateDbContext())
        {
            // Never collide with an existing tenant's name (and never reprint a password we no
            // longer have) — just provision a fresh, distinctly-named demo tenant instead.
            if (await check.Tenants.AnyAsync(t => t.Name == name))
                name = $"{name} {DateTime.UtcNow:yyyy-MM-dd HHmmss}";

            // Usernames are unique across the whole install, so a second demo tenant can't reuse "admin".
            for (int n = 2; await check.Users.AnyAsync(u => u.Username == username); n++)
                username = $"demo{n}";
        }

        var provision = await platformAdmin.CreateTenantAsync(name, username, null);

        // New clients start with one house; the demo farm has three, added the same way the
        // Super Admin adds houses when a client asks for more.
        await platformAdmin.AddHouseAsync(provision.Tenant.Id, "House 2", null, 0);
        await platformAdmin.AddHouseAsync(provision.Tenant.Id, "House 3", null, 0);

        using var db = dbFactory.CreateDbContext();
        db.TenantId = provision.Tenant.Id;

        // A different random history on every run; outcome mix is still guaranteed below.
        var rng = new Random();

        var houses = await db.Houses.OrderBy(h => h.SortOrder).ToListAsync();
        var capacities = new[] { 5000, 6000, 4500, 5500, 5000 };
        for (int i = 0; i < houses.Count; i++)
        {
            houses[i].Code = $"H{i + 1}";
            houses[i].CapacityBirds = capacities[i % capacities.Length];
            houses[i].Notes = i switch
            {
                0 => "Open-sided shed, curtains + 6 fans",
                1 => "Open-sided shed, foggers installed",
                _ => "Older shed, natural ventilation",
            };
        }

        // Integrators are platform-level: reuse the demo ones if an earlier demo already created
        // them, then enable them for this client — the same as the Super Admin would.
        var integrators = new[]
        {
            new Integrator { Name = "Skylark Foods", BagWeightKg = 50m, ChickCostPerBird = 38.50m, GrowingChargePerKg = 8.00m,
                             StandardFcr = 1.72m, FcrIncentivePerKg = 0.04m, MortalityAllowancePct = 5m, MortalityDeductionPerBird = 30m,
                             PaymentDays = 15, Notes = "Demo integrator" },
            new Integrator { Name = "Godavari Integrations", BagWeightKg = 50m, ChickCostPerBird = 40.00m, GrowingChargePerKg = 7.60m,
                             StandardFcr = 1.75m, FcrIncentivePerKg = 0.04m, MortalityAllowancePct = 5m, MortalityDeductionPerBird = 30m,
                             PaymentDays = 21, Notes = "Demo integrator" },
        };
        for (int i = 0; i < integrators.Length; i++)
        {
            var existing = await db.Integrators.FirstOrDefaultAsync(x => x.Name == integrators[i].Name);
            if (existing is null) await platformAdmin.SaveIntegratorAsync(integrators[i]);
            else integrators[i] = existing;
        }
        var integratorIds = integrators.Select(i => i.Id).ToList();
        await platformAdmin.SetTenantIntegratorsAsync(provision.Tenant.Id, integratorIds);
        // Re-load into this context so batches attach to tracked rows.
        integrators = await db.Integrators.Where(i => integratorIds.Contains(i.Id))
            .OrderBy(i => i.Name == "Skylark Foods" ? 0 : 1).ToArrayAsync();

        // Deal outcomes for the completed batches: always at least one best and one failure, the
        // rest a random mix — then shuffle so which house/cycle gets which is different every run.
        var failures = new[] { DiseaseOutbreak, HeatStress, PoorBrooding };
        var pool = new List<Profile> { Best, Average, Average, BelowAverage, failures[rng.Next(failures.Length)] };
        var extras = new[] { Best, Average, BelowAverage, failures[rng.Next(failures.Length)] };
        while (pool.Count < houses.Count * 2) pool.Add(extras[rng.Next(extras.Length)]);
        pool = pool.OrderBy(_ => rng.Next()).ToList();

        // Houses are staggered like a real farm: the batch in each house now is at a different age.
        var currentAges = new[] { 31, 17, 5, 24, 10 };
        var plans = new List<(House House, DateTime Placement, Profile Profile, bool Completed)>();
        for (int h = 0; h < houses.Count; h++)
        {
            var currentPlacement = DateTime.Today.AddDays(-currentAges[h % currentAges.Length]);
            var activeProfile = new[] { Best, Average, Average, BelowAverage }[rng.Next(4)];
            plans.Add((houses[h], currentPlacement, activeProfile, false));

            // ~41-day cycle + 14-20 days of clean-out/downtime between flocks.
            var prev = currentPlacement;
            for (int k = 0; k < 2; k++)
            {
                prev = prev.AddDays(-(41 + rng.Next(14, 21)));
                plans.Add((houses[h], prev, pool[h * 2 + k], true));
            }
        }

        int seq = 0;
        foreach (var plan in plans.OrderBy(p => p.Placement))
        {
            seq++;
            var integrator = plan.House.SortOrder == 3 ? integrators[1] : integrators[rng.Next(10) < 8 ? 0 : 1];
            var code = $"AMR-{plan.Placement:yy}-{seq:00}";
            SeedBatch(db, rng, plan.House, integrator, plan.Placement, plan.Profile, plan.Completed, code);
            await db.SaveChangesAsync();
        }

        SeedGeneralExpenses(db, rng, plans.Min(p => p.Placement));
        await db.SaveChangesAsync();

        return provision;
    }

    private static void SeedBatch(FarmDbContext db, Random rng, House house, Integrator integrator,
        DateTime placement, Profile profile, bool completed, string batchCode)
    {
        var today = DateTime.Today;
        string season = placement.Month switch { >= 3 and <= 6 => "Summer", >= 7 and <= 9 => "Monsoon", _ => "Winter" };
        int chicks = (int)(Math.Round(house.CapacityBirds * profile.Stocking * (0.98 + rng.NextDouble() * 0.03) / 10) * 10);
        int firstLiftDay = profile.Scenario == Scenario.HeatStress ? 36 : 37 + rng.Next(0, 4);
        int liftDays = 2 + rng.Next(0, 2);
        int lastDay = firstLiftDay + liftDays - 1;

        var batch = new Batch
        {
            BatchCode = batchCode,
            House = house,
            Integrator = integrator,
            BranchCode = integrator.Name == "Skylark Foods" ? "SKY-EG-04" : "GDV-RJY-11",
            Breed = rng.Next(10) < 7 ? "Cobb 430Y" : "Ross 308",
            PlacementDate = placement,
            ChicksPlaced = chicks,
            ChickCostPerBird = integrator.ChickCostPerBird,
            TargetWeightKg = 2.3m,
            Status = completed ? BatchStatus.Closed : BatchStatus.Active,
            ClosedDate = completed ? placement.AddDays(lastDay + 1) : null,
            Notes = completed ? $"{profile.Label} flock ({season} placement)" : "",
        };
        db.Batches.Add(batch);

        // ---------------- Day-by-day simulation ----------------
        int alive = chicks;
        double prevCumFeedPerBird = 0;
        var consumptionByDay = new Dictionary<int, decimal>();
        var liftings = new List<Lifting>();
        int liftSeq = 0;
        double growthNoise = 1.0;

        for (int day = 1; day <= lastDay; day++)
        {
            var date = placement.AddDays(day);
            bool recorded = completed || date <= today;
            if (!recorded) break;

            // Growth factor vs standard: early value blends into the late value over the cycle.
            double t = Math.Clamp((day - 7) / 30.0, 0, 1);
            double growth = profile.GrowthEarly + (profile.GrowthLate - profile.GrowthEarly) * t;
            if (profile.Scenario == Scenario.Disease && day >= 21)
                growth = Math.Min(growth, 1.0 - Math.Min(0.14, (day - 20) * 0.02));
            if (profile.Scenario == Scenario.HeatStress && day >= 27)
                growth = Math.Min(growth, 0.99 - Math.Min(0.11, (day - 26) * 0.012));
            growthNoise = Math.Clamp(growthNoise + (rng.NextDouble() - 0.5) * 0.01, 0.985, 1.015);
            var std = StdAt(day);
            double weightGm = std.Weight * growth * growthNoise;

            // Feed: cumulative intake per bird follows weight x cumulative FCR; daily = difference.
            double fcr = std.Fcr * profile.FcrFactor * (profile.Scenario == Scenario.Disease && day >= 21 ? 1.04 : 1.0);
            double cumFeedPerBird = Math.Max(weightGm * Math.Max(fcr, 0.75) - 42, prevCumFeedPerBird + 8);
            double dailyIntakeGm = cumFeedPerBird - prevCumFeedPerBird;
            prevCumFeedPerBird = cumFeedPerBird;

            // Mortality: heavier in the first days, then background rate, plus scenario events.
            double expected = day <= 7
                ? chicks * profile.FirstWeekMort * (day <= 3 ? 0.2 : 0.1)
                : alive * profile.DailyMort;
            string remarks = "";
            if (profile.Scenario == Scenario.Disease && day is >= 22 and <= 29)
            {
                double peak = 1 - Math.Abs(day - 25) / 4.0;
                expected += alive * 0.012 * peak;
                if (day == 22) remarks = "Birds dull, huddling; white watery droppings - vet called";
                if (day == 23) remarks = "Vet diagnosis: IBD (Gumboro) suspected, treatment started";
                if (day == 25) remarks = "Peak mortality, post-mortem done";
                if (day == 28) remarks = "Mortality reducing after treatment";
            }
            if (profile.Scenario == Scenario.HeatStress && day is >= 30 and <= 34)
            {
                expected += alive * (day is 32 or 33 ? 0.008 : 0.003);
                if (day == 30) remarks = "Heat wave, shed 38-40 °C in afternoon, heavy panting";
                if (day == 32) remarks = "Heat stroke deaths in afternoon, foggers not sufficient";
                if (day == 34) remarks = "Temperature easing, electrolytes continued";
            }
            if (profile.Scenario == Scenario.ColdBrooding && day <= 5)
            {
                if (day == 1) remarks = "Power cut at night, brooder gas ran out, chicks huddling";
                if (day == 3) remarks = "Weak chicks separated; poor crop fill";
            }
            int mortality = Math.Max(0, (int)Math.Round(expected + (rng.NextDouble() - 0.5) * 2 * Math.Sqrt(Math.Max(expected, 0.5))));
            int culls = day % 7 == 0 ? rng.Next(0, 4 + (int)(chicks * profile.DailyMort)) : (rng.Next(12) == 0 ? rng.Next(1, 4) : 0);
            mortality = Math.Min(mortality, alive);
            alive -= mortality;
            culls = Math.Min(culls, alive);
            alive -= culls;

            decimal feedKg = (decimal)(alive * dailyIntakeGm / 1000.0);
            decimal bags = Math.Round(feedKg / integrator.BagWeightKg, 1);
            consumptionByDay[day] = bags * integrator.BagWeightKg;

            // House conditions around the age target, shifted by season and scenario.
            double target = TargetTempC(day);
            double offset = season == "Summer" ? 2.5 : season == "Winter" ? -1.0 : 0.5;
            if (profile.Scenario == Scenario.HeatStress && day >= 27) offset += day is >= 30 and <= 34 ? 9 : 5;
            if (profile.Scenario == Scenario.ColdBrooding && day <= 7) offset -= 5;
            if (profile == Best) offset *= 0.3;
            double avgTemp = target + offset + (rng.NextDouble() - 0.5) * 2;
            double spread = 3 + rng.NextDouble() * 3;
            double humidity = season == "Monsoon" ? 72 + rng.Next(0, 14) : season == "Summer" ? 45 + rng.Next(0, 15) : 58 + rng.Next(0, 14);

            db.DailyRecords.Add(new DailyRecord
            {
                Batch = batch,
                Date = date,
                Mortality = mortality,
                Culls = culls,
                FeedConsumedBags = bags,
                FeedConsumedKg = bags * integrator.BagWeightKg,
                AvgBodyWeightGm = Math.Round((decimal)weightGm, 0),
                WaterConsumedLtr = Math.Round(feedKg * (decimal)(1.8 + (avgTemp > 30 ? 0.6 : 0) + rng.NextDouble() * 0.2), 0),
                MinTempC = Math.Round((decimal)(avgTemp - spread / 2), 1),
                MaxTempC = Math.Round((decimal)(avgTemp + spread / 2), 1),
                HumidityPct = (decimal)humidity,
                Remarks = remarks,
            });

            // Lifting: the integrator picks the flock up over 2-3 consecutive days.
            if (completed && day >= firstLiftDay)
            {
                liftSeq++;
                int birds = day == lastDay ? alive : (int)(alive * (liftDays - (day - firstLiftDay) == 2 ? 0.5 : 0.45));
                if (birds <= 0) continue;
                alive -= birds;
                double shrink = 0.985 - rng.NextDouble() * 0.01;   // weighed at the scale after catching/transport
                liftings.Add(new Lifting
                {
                    Batch = batch,
                    Date = date,
                    BirdsLifted = birds,
                    TotalWeightKg = Math.Round((decimal)(birds * weightGm / 1000.0 * shrink), 1),
                    VehicleNumber = $"AP-{new[] { "05", "16", "37", "39" }[rng.Next(4)]}-{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))}-{rng.Next(1000, 9999)}",
                    DcNumber = $"LD-{placement:yyMM}{liftSeq:00}",
                });
            }
        }
        db.Liftings.AddRange(liftings);

        // ---------------- Feed deliveries (integrator DCs) ----------------
        // Delivered ahead of each feed phase, rounded up to whole bags, with a little slack — the
        // way a real integrator plans them.
        decimal Phase(int from, int to) => consumptionByDay.Where(kv => kv.Key >= from && kv.Key <= to).Sum(kv => kv.Value);
        void Deliver(int day, string type, decimal kg)
        {
            var date = placement.AddDays(day);
            if (kg <= 0 || (!completed && date > today)) return;
            int bagsDelivered = (int)Math.Ceiling(kg * 1.02m / integrator.BagWeightKg);
            db.FeedDeliveries.Add(new FeedDelivery
            {
                Batch = batch, Date = date, FeedType = type, Bags = bagsDelivered, BagWeightKg = integrator.BagWeightKg,
                DcNumber = $"DC-{placement:yyMM}-{day:00}",
            });
        }
        // Active batches: deliveries for phases already started are planned from the simulated need.
        Deliver(0, "Pre-starter", Math.Max(Phase(1, 10), completed ? 0 : chicks * 0.25m));
        Deliver(9, "Starter", Math.Max(Phase(11, 22), completed ? 0 : chicks * 0.95m));
        Deliver(21, "Finisher", Math.Max(Phase(23, 31), completed ? 0 : chicks * 1.3m));
        Deliver(30, "Finisher", Math.Max(Phase(32, lastDay), completed ? 0 : chicks * 1.1m));

        // ---------------- Health programme ----------------
        void Health(int day, HealthEventType type, string name, string dose, string route, decimal cost, string remarks = "")
        {
            var date = placement.AddDays(day);
            if (!completed && date > today) return;
            db.HealthEvents.Add(new HealthEvent { Batch = batch, Date = date, Type = type, Name = name, Dose = dose, Route = route, Cost = cost, Remarks = remarks });
        }
        decimal per1000 = chicks / 1000m;
        Health(0, HealthEventType.Disinfection, "Shed fumigation (formalin + KMnO4)", "Full shed", "Fumigation", 1800);
        Health(1, HealthEventType.Supplement, "Glucose + Vitamin premix", "20 g/ltr", "Drinking water", Math.Round(per1000 * 90));
        Health(6, HealthEventType.Vaccination, "ND + IB (Lasota/H120)", "1 drop/bird", "Eye drop", Math.Round(per1000 * 140));
        Health(13, HealthEventType.Vaccination, "IBD (Gumboro) intermediate", "1 dose/bird", "Drinking water", Math.Round(per1000 * 160));
        if (!profile.MissesBooster)
            Health(21, HealthEventType.Vaccination, "ND Lasota booster", "1 dose/bird", "Drinking water", Math.Round(per1000 * 110));
        Health(15, HealthEventType.Medication, "Anticoccidial (in water)", "1 ml/ltr", "Drinking water", Math.Round(per1000 * 180));
        if (profile.Scenario == Scenario.Disease)
        {
            Health(23, HealthEventType.Medication, "Enrofloxacin + liver tonic", "1 ml/2 ltr", "Drinking water", Math.Round(per1000 * 1300), "Vet prescribed, 5 days");
            Health(24, HealthEventType.Supplement, "Electrolytes + Vitamin AD3E", "1 g/ltr", "Drinking water", Math.Round(per1000 * 250));
        }
        if (profile.Scenario == Scenario.HeatStress || season == "Summer")
            Health(28, HealthEventType.Supplement, "Electrolytes + Vitamin C (heat)", "1 g/ltr", "Drinking water", Math.Round(per1000 * 220));
        if (profile.Scenario == Scenario.ColdBrooding)
            Health(3, HealthEventType.Supplement, "Vitamin + probiotic for weak chicks", "1 g/ltr", "Drinking water", Math.Round(per1000 * 150));

        // ---------------- Farm-side running costs ----------------
        void Expense(int day, string category, string description, decimal amount)
        {
            var date = placement.AddDays(day);
            if (amount <= 0 || (!completed && date > today)) return;
            db.Expenses.Add(new Expense { Batch = batch, Date = date, Category = category, Description = description, Amount = Math.Round(amount, 0) });
        }
        decimal J(decimal baseAmount, double pct = 0.08) => baseAmount * (decimal)(1 + (rng.NextDouble() - 0.5) * 2 * pct);
        Expense(-2, "Litter", "Rice husk bedding", J(chicks * 1.05m));
        Expense(-1, "Other", "Shed washing & lime", J(1400));
        Expense(0, "Gas", "Brooder gas cylinders", J(chicks * (season == "Winter" ? 0.75m : 0.30m) * (profile.Scenario == Scenario.ColdBrooding ? 0.7m : 1m)));
        Expense(12, "Electricity", "Power bill - brooding & lighting", J(chicks * 0.45m * (season == "Winter" ? 1.3m : 1m)));
        Expense(34, "Electricity", "Power bill - fans/foggers", J(chicks * 0.45m * (season == "Summer" ? 1.7m : 1m) * (profile.Scenario == Scenario.HeatStress ? 1.3m : 1m)));
        Expense(20, "Labour", "Farm worker wages (half cycle)", J(5200, 0.03));
        Expense(lastDay, "Labour", "Farm worker wages + lifting helpers", J(5200 + 1800, 0.03));
        Expense(18, "Diesel", "Generator diesel", J(season == "Summer" ? 3200 : 1800, 0.2));
        Expense(lastDay, "Transport", "Local transport & misc", J(900, 0.2));
        if (profile.Scenario == Scenario.HeatStress)
            Expense(31, "Repairs", "Emergency fogger pump + extra fan hire", J(6500));
        else if (rng.Next(3) == 0)
            Expense(rng.Next(5, 30), "Repairs", new[] { "Drinker line leak fixed", "Curtain rope/pulley replaced", "Feeder pan replacement" }[rng.Next(3)], J(1200 + rng.Next(0, 3000)));

        // ---------------- Integrator settlement ----------------
        if (completed)
        {
            decimal liftedKg = liftings.Sum(l => l.TotalWeightKg);
            decimal feedKg = consumptionByDay.Values.Sum();
            decimal fcr = liftedKg > 0 ? feedKg / liftedKg : 0;
            int totalLoss = chicks - liftings.Sum(l => l.BirdsLifted);
            decimal mortPct = (decimal)totalLoss / chicks * 100;

            // Settled on the integrator's own terms.
            decimal rate = integrator.GrowingChargePerKg;
            decimal fcrLimit = integrator.StandardFcr;
            decimal incentive = fcr < fcrLimit ? Math.Round((fcrLimit - fcr) * 100m * integrator.FcrIncentivePerKg * liftedKg, 0) : 0;
            decimal deductions = 0;
            decimal allowed = integrator.MortalityAllowancePct;
            if (mortPct > allowed) deductions += Math.Round((mortPct - allowed) / 100 * chicks * integrator.MortalityDeductionPerBird, 0); // excess mortality
            if (fcr > 1.90m) deductions += Math.Round((fcr - 1.90m) * 3m * liftedKg, 0);            // excess feed
            deductions += Math.Round(liftedKg * 0.02m, 0);                                           // weighbridge / admin

            db.Settlements.Add(new Settlement
            {
                Batch = batch,
                Date = placement.AddDays(lastDay + 8),
                GrowingChargePerKg = rate,
                PerformanceIncentive = incentive,
                Deductions = deductions,
                AmountReceived = Math.Round(liftedKg * rate + incentive - deductions, 0),
                Remarks = $"FCR {fcr:0.000}, mortality {mortPct:0.0}%"
                    + (incentive > 0 ? ", FCR incentive earned" : "")
                    + (mortPct > allowed ? ", excess mortality deducted" : ""),
            });
        }
    }

    /// <summary>Farm overheads not tied to one batch — the costs a real owner pays regardless of flock.</summary>
    private static void SeedGeneralExpenses(FarmDbContext db, Random rng, DateTime from)
    {
        for (var month = new DateTime(from.Year, from.Month, 1); month <= DateTime.Today; month = month.AddMonths(1))
        {
            db.Expenses.Add(new Expense { Date = month.AddDays(4), Category = "Other", Description = "Phone, internet & stationery", Amount = 650 + rng.Next(0, 200) });
            if (month.Month % 3 == 0)
                db.Expenses.Add(new Expense { Date = month.AddDays(10), Category = "Repairs", Description = "Generator servicing", Amount = 3500 + rng.Next(0, 1500) });
        }
        db.Expenses.Add(new Expense { Date = from.AddDays(20), Category = "Other", Description = "Shed insurance (annual)", Amount = 14500 });
        db.Expenses.Add(new Expense { Date = from.AddDays(75), Category = "Repairs", Description = "Roof sheet repair after storm", Amount = 11800 });
    }
}
