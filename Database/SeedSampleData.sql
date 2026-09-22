/* ============================================================
   LEGACY — pre-multi-tenant sample data. Left here for reference only.
   ============================================================
   Written against the pre-multi-tenant schema (no TenantId column
   on any table) — running this against the current database will
   fail or insert rows with no tenant, which the app's tenant query
   filters will then never show anyone. Create a tenant from the
   Super Admin area instead, which seeds its houses/admin the same
   way this script used to for the whole (single-tenant) install.
   ============================================================
   AMR Poultry Farms — sample data seeder (ORIGINAL, STALE)
   ============================================================
   Adds realistic demo data so the app isn't empty:
     - 2 integrators (Suguna Foods, Venky's India Ltd) if missing
     - for every house that currently has ZERO batches:
         • 1 ACTIVE batch (mid-cycle, placed recently) with daily
           records up to yesterday, 3 feed deliveries, 2 health
           events
         • 1 CLOSED batch (a finished 42-day cycle) with full daily
           records, feed deliveries, health events, a lifting and
           a settlement
     - a few general farm expenses

   Safe to re-run: houses that already have at least one batch are
   skipped entirely, so this never duplicates data. Run this AFTER
   CreateAmrPoultryFarmDatabase.sql (or after the app's first run,
   which seeds the 3 default houses + admin user on its own).

   Usage: open in SSMS / Azure Data Studio, check the USE below
   points at your database, and hit Execute.
   ============================================================ */

USE [AmrPoultryFarm];
GO

SET NOCOUNT ON;

-- ---------------- Integrators ----------------------------------------
IF NOT EXISTS (SELECT 1 FROM dbo.Integrators WHERE Name = N'Suguna Foods')
    INSERT INTO dbo.Integrators (Name, BagWeightKg, Notes, SortOrder) VALUES (N'Suguna Foods', 50, N'', 1);

IF NOT EXISTS (SELECT 1 FROM dbo.Integrators WHERE Name = N'Venky''s India Ltd')
    INSERT INTO dbo.Integrators (Name, BagWeightKg, Notes, SortOrder) VALUES (N'Venky''s India Ltd', 50, N'', 2);

DECLARE @Integrator1 INT = (SELECT TOP 1 Id FROM dbo.Integrators WHERE Name = N'Suguna Foods');
DECLARE @Integrator2 INT = (SELECT TOP 1 Id FROM dbo.Integrators WHERE Name = N'Venky''s India Ltd');

-- ---------------- Make sure there's at least one house ---------------
IF NOT EXISTS (SELECT 1 FROM dbo.Houses)
BEGIN
    INSERT INTO dbo.Houses (Name, Code, CapacityBirds, Notes, SortOrder) VALUES
        (N'House 1', NULL, 12500, N'', 1),
        (N'House 2', NULL, 12500, N'', 2),
        (N'House 3', NULL, 12500, N'', 3);
END

-- ---------------- Plan + insert 2 batches per un-seeded house --------
DECLARE @NewBatches TABLE (BatchId INT, HouseId INT, Kind CHAR(1), PlacementDate DATE, EndDate DATE, ChicksPlaced INT);

;WITH Eligible AS (
    SELECT h.Id AS HouseId, ROW_NUMBER() OVER (ORDER BY h.SortOrder, h.Id) AS Seq
    FROM dbo.Houses h
    WHERE NOT EXISTS (SELECT 1 FROM dbo.Batches b WHERE b.HouseId = h.Id)
),
Plan_ AS (
    -- Active batch: placed 14 to 14+6*(N-1) days ago, still running (no ClosedDate)
    SELECT HouseId, Seq, 'A' AS Kind,
           CAST(DATEADD(DAY, -(14 + (Seq - 1) * 6), GETDATE()) AS DATE) AS PlacementDate,
           11500 + Seq * 250 AS ChicksPlaced,
           CASE WHEN Seq % 2 = 0 THEN @Integrator2 ELSE @Integrator1 END AS IntegratorId,
           CASE WHEN Seq % 2 = 0 THEN N'Ross 308' ELSE N'Cobb 430Y' END AS Breed
    FROM Eligible
    UNION ALL
    -- Closed batch: a finished 42-day cycle from further back
    SELECT HouseId, Seq, 'C',
           CAST(DATEADD(DAY, -(120 + (Seq - 1) * 5), GETDATE()) AS DATE),
           11800 + Seq * 200,
           CASE WHEN Seq % 2 = 0 THEN @Integrator1 ELSE @Integrator2 END,
           CASE WHEN Seq % 2 = 0 THEN N'Cobb 430Y' ELSE N'Ross 308' END
    FROM Eligible
)
INSERT INTO dbo.Batches (BatchCode, HouseId, IntegratorId, Breed, PlacementDate, ChicksPlaced, ChickCostPerBird, TargetWeightKg, Status, ClosedDate)
OUTPUT inserted.Id, inserted.HouseId,
       CASE WHEN inserted.Status = 0 THEN 'A' ELSE 'C' END,
       CAST(inserted.PlacementDate AS DATE),
       CAST(COALESCE(inserted.ClosedDate, DATEADD(DAY, -1, GETDATE())) AS DATE),
       inserted.ChicksPlaced
INTO @NewBatches (BatchId, HouseId, Kind, PlacementDate, EndDate, ChicksPlaced)
SELECT
    CONCAT('AMR-', RIGHT('000' + CAST(HouseId AS VARCHAR(10)), 3), '-', Kind),
    HouseId, IntegratorId, Breed, PlacementDate, ChicksPlaced, 45.50, 2.30,
    CASE WHEN Kind = 'A' THEN 0 ELSE 1 END,
    CASE WHEN Kind = 'C' THEN DATEADD(DAY, 42, PlacementDate) ELSE NULL END
FROM Plan_;

-- ---------------- Daily records (placement day .. EndDate) -----------
;WITH Numbers AS (
    SELECT TOP (400) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects
),
Days AS (
    SELECT nb.BatchId, nb.ChicksPlaced, n.n AS DayIdx,
           DATEADD(DAY, n.n, nb.PlacementDate) AS RecDate
    FROM @NewBatches nb
    CROSS JOIN Numbers n
    WHERE DATEADD(DAY, n.n, nb.PlacementDate) <= nb.EndDate
),
Raw AS (
    SELECT *,
           CASE WHEN DayIdx <= 3 THEN ABS(CHECKSUM(NEWID())) % 4
                ELSE ABS(CHECKSUM(NEWID())) % 3 END AS MortToday
    FROM Days
),
Cum AS (
    SELECT *,
           SUM(MortToday) OVER (PARTITION BY BatchId ORDER BY DayIdx ROWS UNBOUNDED PRECEDING) AS CumMort
    FROM Raw
)
INSERT INTO dbo.DailyRecords (BatchId, Date, Mortality, FeedConsumedBags, FeedConsumedKg, AvgBodyWeightGm, WaterConsumedLtr, MinTempC, MaxTempC, HumidityPct)
SELECT
    c.BatchId,
    CAST(c.RecDate AS DATETIME2),
    c.MortToday,
    fb.FeedBags,
    CAST(fb.FeedBags * 50.0 AS DECIMAL(18,2)),
    CAST(40 + c.DayIdx * 54.0 AS DECIMAL(18,2)),
    CAST(fb.FeedBags * 50.0 * 1.8 AS DECIMAL(18,2)),
    CAST(ROUND(29 - c.DayIdx * 0.26, 1) AS DECIMAL(18,2)),
    CAST(ROUND(33 - c.DayIdx * 0.26, 1) AS DECIMAL(18,2)),
    CAST(55 + (ABS(CHECKSUM(NEWID())) % 16) AS DECIMAL(18,2))
FROM Cum c
CROSS APPLY (SELECT CAST(ROUND((c.ChicksPlaced - c.CumMort) * (15 + c.DayIdx * 2.2) / 1000.0 / 50.0, 1) AS DECIMAL(18,2)) AS FeedBags) fb;

-- ---------------- Feed deliveries -------------------------------------
INSERT INTO dbo.FeedDeliveries (BatchId, Date, FeedType, Bags, BagWeightKg, DcNumber)
SELECT BatchId, CAST(PlacementDate AS DATETIME2), N'Pre-Starter', CAST(ROUND(ChicksPlaced / 1000.0, 0) AS INT), 50, CONCAT('DC-', BatchId, '-1')
FROM @NewBatches;

INSERT INTO dbo.FeedDeliveries (BatchId, Date, FeedType, Bags, BagWeightKg, DcNumber)
SELECT BatchId, CAST(DATEADD(DAY, 7, PlacementDate) AS DATETIME2), N'Starter', CAST(ROUND(ChicksPlaced / 1000.0, 0) * 3 AS INT), 50, CONCAT('DC-', BatchId, '-2')
FROM @NewBatches
WHERE DATEADD(DAY, 7, PlacementDate) <= EndDate;

INSERT INTO dbo.FeedDeliveries (BatchId, Date, FeedType, Bags, BagWeightKg, DcNumber)
SELECT BatchId, CAST(DATEADD(DAY, 21, PlacementDate) AS DATETIME2), N'Finisher', CAST(ROUND(ChicksPlaced / 1000.0, 0) * 6 AS INT), 50, CONCAT('DC-', BatchId, '-3')
FROM @NewBatches
WHERE DATEADD(DAY, 21, PlacementDate) <= EndDate;

-- ---------------- Health events ---------------------------------------
INSERT INTO dbo.HealthEvents (BatchId, Date, Type, Name, Dose, Route, Cost)
SELECT BatchId, CAST(PlacementDate AS DATETIME2), 0, N'ND-IB (Lasota)', N'1 drop/bird', N'Eye drop', CAST(ChicksPlaced * 0.45 AS DECIMAL(18,2))
FROM @NewBatches;

INSERT INTO dbo.HealthEvents (BatchId, Date, Type, Name, Dose, Route, Cost)
SELECT BatchId, CAST(DATEADD(DAY, 14, PlacementDate) AS DATETIME2), 0, N'Gumboro (IBD)', N'1 ml/ltr', N'Drinking water', CAST(ChicksPlaced * 0.35 AS DECIMAL(18,2))
FROM @NewBatches
WHERE DATEADD(DAY, 14, PlacementDate) <= EndDate;

-- ---------------- Liftings + settlement for closed batches ------------
DECLARE @Liftings TABLE (BatchId INT, BirdsLifted INT, TotalWeightKg DECIMAL(18,2), EndDate DATE);

INSERT INTO @Liftings (BatchId, BirdsLifted, TotalWeightKg, EndDate)
SELECT nb.BatchId,
       nb.ChicksPlaced - ISNULL(dr.TotalMort, 0),
       CAST((nb.ChicksPlaced - ISNULL(dr.TotalMort, 0)) * 2.25 AS DECIMAL(18,2)),
       nb.EndDate
FROM @NewBatches nb
OUTER APPLY (SELECT SUM(Mortality) AS TotalMort FROM dbo.DailyRecords d WHERE d.BatchId = nb.BatchId) dr
WHERE nb.Kind = 'C';

INSERT INTO dbo.Liftings (BatchId, Date, BirdsLifted, TotalWeightKg, VehicleNumber, DcNumber)
SELECT BatchId, CAST(DATEADD(DAY, 1, EndDate) AS DATETIME2), BirdsLifted, TotalWeightKg,
       CONCAT('TS-08-UA-', 4000 + BatchId), CONCAT('LFT-', BatchId)
FROM @Liftings;

INSERT INTO dbo.Settlements (BatchId, Date, GrowingChargePerKg, PerformanceIncentive, Deductions, AmountReceived)
SELECT BatchId, CAST(DATEADD(DAY, 3, EndDate) AS DATETIME2), 8.50,
       CAST(TotalWeightKg * 0.30 AS DECIMAL(18,2)),
       CAST(TotalWeightKg * 0.05 AS DECIMAL(18,2)),
       CAST(TotalWeightKg * 8.50 + TotalWeightKg * 0.30 - TotalWeightKg * 0.05 AS DECIMAL(18,2))
FROM @Liftings;

-- ---------------- A few general farm expenses --------------------------
IF NOT EXISTS (SELECT 1 FROM dbo.Expenses WHERE BatchId IS NULL)
BEGIN
    INSERT INTO dbo.Expenses (BatchId, Date, Category, Description, Amount) VALUES
        (NULL, DATEADD(DAY, -5, CAST(GETDATE() AS DATE)), N'Electricity', N'Monthly power bill — farm', 18500),
        (NULL, DATEADD(DAY, -10, CAST(GETDATE() AS DATE)), N'Labour', N'Field staff wages', 42000),
        (NULL, DATEADD(DAY, -15, CAST(GETDATE() AS DATE)), N'Diesel/Gas', N'Genset diesel refill', 6200);
END

-- ---------------- Summary ----------------------------------------------
DECLARE @HouseCount INT = (SELECT COUNT(DISTINCT HouseId) FROM @NewBatches);
DECLARE @BatchCount INT = (SELECT COUNT(*) FROM @NewBatches);
PRINT CONCAT('Seeded ', @BatchCount, ' batches across ', @HouseCount, ' house(s). Houses that already had batches were left untouched.');
GO
