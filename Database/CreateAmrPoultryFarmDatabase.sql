/* ============================================================
   LEGACY — pre-multi-tenant schema. Left here for reference only.
   ============================================================
   The app is now multi-tenant (see Migrations/) — every table below
   is missing the TenantId column the real schema now has, and
   FarmService.InitializeAsync() (the seeding step this script
   mirrors) no longer exists; seeding now happens per-tenant via
   Services/PlatformAdminService.CreateTenantAsync when a Super Admin
   creates a client. Don't run this against a real deployment — use
   `dotnet ef database update` (SQL Server) or EnsureCreatedAsync()
   (SQLite dev) instead, both wired up in Program.cs.
   ============================================================
   AMR Poultry Farms — SQL Server schema + seed data (ORIGINAL, STALE)
   ============================================================
   Creates every table the app's EF Core model mapped BEFORE
   multi-tenancy (same shape EnsureCreatedAsync() used to build on
   first run), with the same foreign keys, unique indexes, and
   default constraints as Data/FarmDbContext.cs + Models/FarmModels.cs
   had at the time — plus the same first-run seed data
   FarmService.InitializeAsync() used to insert:
     - 3 houses (House 1 / House 2 / House 3)
     - the built-in "Admin" role holding every permission code
     - one login: username "admin", password "admin"

   Usage:
     sqlcmd -S YOUR_SERVER -i CreateAmrPoultryFarmDatabase.sql
   or open it in SQL Server Management Studio / Azure Data Studio
   and hit Execute. Safe to re-run — every CREATE is guarded with
   an IF NOT EXISTS check, and the seed inserts only fire if the
   tables are empty.

   IMPORTANT: change the admin password immediately after first
   login (Settings → Users → admin → Reset Password). The hash
   below is for the literal password "admin", PBKDF2-SHA256,
   100,000 iterations — same algorithm as Services/PasswordHasher.cs.
   ============================================================ */

IF DB_ID(N'AmrPoultryFarm') IS NULL
BEGIN
    PRINT 'Creating database AmrPoultryFarm...';
    CREATE DATABASE [AmrPoultryFarm];
END
GO

USE [AmrPoultryFarm];
GO

-- ============================================================
-- Lookup tables
-- ============================================================

IF OBJECT_ID(N'dbo.Houses', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Houses (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Houses PRIMARY KEY,
        Name            NVARCHAR(50)   NOT NULL,
        Code            NVARCHAR(20)   NULL,
        CapacityBirds   INT            NOT NULL CONSTRAINT DF_Houses_CapacityBirds DEFAULT (0),
        Notes           NVARCHAR(300)  NOT NULL CONSTRAINT DF_Houses_Notes DEFAULT (N''),
        SortOrder       INT            NOT NULL CONSTRAINT DF_Houses_SortOrder DEFAULT (0)
    );
END
GO

IF OBJECT_ID(N'dbo.Integrators', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Integrators (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Integrators PRIMARY KEY,
        Name            NVARCHAR(80)   NOT NULL,
        BagWeightKg     DECIMAL(18,2)  NOT NULL CONSTRAINT DF_Integrators_BagWeightKg DEFAULT (50),
        Notes           NVARCHAR(300)  NOT NULL CONSTRAINT DF_Integrators_Notes DEFAULT (N''),
        SortOrder       INT            NOT NULL CONSTRAINT DF_Integrators_SortOrder DEFAULT (0)
    );
END
GO

-- ============================================================
-- Batches + child records
-- ============================================================

IF OBJECT_ID(N'dbo.Batches', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Batches (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Batches PRIMARY KEY,
        BatchCode           NVARCHAR(30)   NOT NULL,
        HouseId             INT            NOT NULL,
        IntegratorId        INT            NOT NULL,
        BranchCode          NVARCHAR(40)   NOT NULL CONSTRAINT DF_Batches_BranchCode DEFAULT (N''),
        Breed               NVARCHAR(40)   NOT NULL CONSTRAINT DF_Batches_Breed DEFAULT (N'Cobb 430Y'),
        PlacementDate       DATETIME2      NOT NULL,
        ChicksPlaced        INT            NOT NULL CONSTRAINT DF_Batches_ChicksPlaced DEFAULT (0),
        ChickCostPerBird    DECIMAL(18,2)  NOT NULL CONSTRAINT DF_Batches_ChickCostPerBird DEFAULT (0),
        TargetWeightKg      DECIMAL(18,2)  NOT NULL CONSTRAINT DF_Batches_TargetWeightKg DEFAULT (2.3),
        Status              INT            NOT NULL CONSTRAINT DF_Batches_Status DEFAULT (0),  -- 0=Active, 1=Closed
        ClosedDate          DATETIME2      NULL,
        Notes               NVARCHAR(300)  NOT NULL CONSTRAINT DF_Batches_Notes DEFAULT (N''),
        CONSTRAINT FK_Batches_Houses FOREIGN KEY (HouseId) REFERENCES dbo.Houses (Id) ON DELETE NO ACTION,
        CONSTRAINT FK_Batches_Integrators FOREIGN KEY (IntegratorId) REFERENCES dbo.Integrators (Id) ON DELETE NO ACTION
    );
    CREATE INDEX IX_Batches_HouseId ON dbo.Batches (HouseId);
    CREATE INDEX IX_Batches_IntegratorId ON dbo.Batches (IntegratorId);
    CREATE INDEX IX_Batches_Status ON dbo.Batches (Status);
    CREATE INDEX IX_Batches_PlacementDate ON dbo.Batches (PlacementDate);
END
GO

IF OBJECT_ID(N'dbo.DailyRecords', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DailyRecords (
        Id                  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DailyRecords PRIMARY KEY,
        BatchId             INT            NOT NULL,
        Date                DATETIME2      NOT NULL,
        Mortality           INT            NOT NULL CONSTRAINT DF_DailyRecords_Mortality DEFAULT (0),
        Culls               INT            NOT NULL CONSTRAINT DF_DailyRecords_Culls DEFAULT (0),
        FeedConsumedBags    DECIMAL(18,2)  NOT NULL CONSTRAINT DF_DailyRecords_FeedConsumedBags DEFAULT (0),
        FeedConsumedKg      DECIMAL(18,2)  NOT NULL CONSTRAINT DF_DailyRecords_FeedConsumedKg DEFAULT (0),
        AvgBodyWeightGm     DECIMAL(18,2)  NOT NULL CONSTRAINT DF_DailyRecords_AvgBodyWeightGm DEFAULT (0),
        WaterConsumedLtr    DECIMAL(18,2)  NOT NULL CONSTRAINT DF_DailyRecords_WaterConsumedLtr DEFAULT (0),
        MinTempC            DECIMAL(18,2)  NOT NULL CONSTRAINT DF_DailyRecords_MinTempC DEFAULT (0),
        MaxTempC            DECIMAL(18,2)  NOT NULL CONSTRAINT DF_DailyRecords_MaxTempC DEFAULT (0),
        HumidityPct         DECIMAL(18,2)  NOT NULL CONSTRAINT DF_DailyRecords_HumidityPct DEFAULT (0),
        Remarks             NVARCHAR(300)  NOT NULL CONSTRAINT DF_DailyRecords_Remarks DEFAULT (N''),
        CONSTRAINT FK_DailyRecords_Batches FOREIGN KEY (BatchId) REFERENCES dbo.Batches (Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_DailyRecords_BatchId_Date ON dbo.DailyRecords (BatchId, Date);
END
GO

IF OBJECT_ID(N'dbo.FeedDeliveries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FeedDeliveries (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FeedDeliveries PRIMARY KEY,
        BatchId         INT            NOT NULL,
        Date            DATETIME2      NOT NULL,
        FeedType        NVARCHAR(30)   NOT NULL CONSTRAINT DF_FeedDeliveries_FeedType DEFAULT (N'Starter'),
        Bags            INT            NOT NULL CONSTRAINT DF_FeedDeliveries_Bags DEFAULT (0),
        BagWeightKg     DECIMAL(18,2)  NOT NULL CONSTRAINT DF_FeedDeliveries_BagWeightKg DEFAULT (50),
        DcNumber        NVARCHAR(40)   NOT NULL CONSTRAINT DF_FeedDeliveries_DcNumber DEFAULT (N''),
        Remarks         NVARCHAR(200)  NOT NULL CONSTRAINT DF_FeedDeliveries_Remarks DEFAULT (N''),
        CONSTRAINT FK_FeedDeliveries_Batches FOREIGN KEY (BatchId) REFERENCES dbo.Batches (Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_FeedDeliveries_BatchId ON dbo.FeedDeliveries (BatchId);
END
GO

IF OBJECT_ID(N'dbo.HealthEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HealthEvents (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_HealthEvents PRIMARY KEY,
        BatchId         INT            NOT NULL,
        Date            DATETIME2      NOT NULL,
        Type            INT            NOT NULL CONSTRAINT DF_HealthEvents_Type DEFAULT (0), -- 0=Vaccination,1=Medication,2=Supplement,3=Disinfection
        Name            NVARCHAR(80)   NOT NULL CONSTRAINT DF_HealthEvents_Name DEFAULT (N''),
        Dose            NVARCHAR(40)   NOT NULL CONSTRAINT DF_HealthEvents_Dose DEFAULT (N''),
        Route           NVARCHAR(30)   NOT NULL CONSTRAINT DF_HealthEvents_Route DEFAULT (N'Drinking water'),
        Cost            DECIMAL(18,2)  NOT NULL CONSTRAINT DF_HealthEvents_Cost DEFAULT (0),
        Remarks         NVARCHAR(200)  NOT NULL CONSTRAINT DF_HealthEvents_Remarks DEFAULT (N''),
        CONSTRAINT FK_HealthEvents_Batches FOREIGN KEY (BatchId) REFERENCES dbo.Batches (Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_HealthEvents_BatchId ON dbo.HealthEvents (BatchId);
END
GO

IF OBJECT_ID(N'dbo.Liftings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Liftings (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Liftings PRIMARY KEY,
        BatchId         INT            NOT NULL,
        Date            DATETIME2      NOT NULL,
        BirdsLifted     INT            NOT NULL CONSTRAINT DF_Liftings_BirdsLifted DEFAULT (0),
        TotalWeightKg   DECIMAL(18,2)  NOT NULL CONSTRAINT DF_Liftings_TotalWeightKg DEFAULT (0),
        VehicleNumber   NVARCHAR(40)   NOT NULL CONSTRAINT DF_Liftings_VehicleNumber DEFAULT (N''),
        DcNumber        NVARCHAR(40)   NOT NULL CONSTRAINT DF_Liftings_DcNumber DEFAULT (N''),
        Remarks         NVARCHAR(200)  NOT NULL CONSTRAINT DF_Liftings_Remarks DEFAULT (N''),
        CONSTRAINT FK_Liftings_Batches FOREIGN KEY (BatchId) REFERENCES dbo.Batches (Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_Liftings_BatchId ON dbo.Liftings (BatchId);
END
GO

IF OBJECT_ID(N'dbo.Settlements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Settlements (
        Id                      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Settlements PRIMARY KEY,
        BatchId                 INT            NOT NULL,
        Date                    DATETIME2      NOT NULL,
        GrowingChargePerKg      DECIMAL(18,2)  NOT NULL CONSTRAINT DF_Settlements_GrowingChargePerKg DEFAULT (0),
        PerformanceIncentive    DECIMAL(18,2)  NOT NULL CONSTRAINT DF_Settlements_PerformanceIncentive DEFAULT (0),
        Deductions              DECIMAL(18,2)  NOT NULL CONSTRAINT DF_Settlements_Deductions DEFAULT (0),
        AmountReceived          DECIMAL(18,2)  NOT NULL CONSTRAINT DF_Settlements_AmountReceived DEFAULT (0),
        Remarks                 NVARCHAR(300)  NOT NULL CONSTRAINT DF_Settlements_Remarks DEFAULT (N''),
        CONSTRAINT FK_Settlements_Batches FOREIGN KEY (BatchId) REFERENCES dbo.Batches (Id) ON DELETE CASCADE,
        CONSTRAINT UQ_Settlements_BatchId UNIQUE (BatchId)   -- one settlement per batch
    );
END
GO

IF OBJECT_ID(N'dbo.Expenses', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Expenses (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Expenses PRIMARY KEY,
        BatchId         INT            NULL,              -- NULL = general farm expense, not tied to a batch
        Date            DATETIME2      NOT NULL,
        Category        NVARCHAR(40)   NOT NULL CONSTRAINT DF_Expenses_Category DEFAULT (N'Electricity'),
        Description     NVARCHAR(120)  NOT NULL CONSTRAINT DF_Expenses_Description DEFAULT (N''),
        Amount          DECIMAL(18,2)  NOT NULL CONSTRAINT DF_Expenses_Amount DEFAULT (0),
        CONSTRAINT FK_Expenses_Batches FOREIGN KEY (BatchId) REFERENCES dbo.Batches (Id) ON DELETE SET NULL
    );
    CREATE INDEX IX_Expenses_BatchId ON dbo.Expenses (BatchId);
    CREATE INDEX IX_Expenses_Date ON dbo.Expenses (Date);
END
GO

-- ============================================================
-- Login / roles / permissions
-- ============================================================

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Users (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
        Username        NVARCHAR(40)    NOT NULL,
        PasswordHash    NVARCHAR(MAX)   NOT NULL,
        PasswordSalt    NVARCHAR(MAX)   NOT NULL,
        DisplayName     NVARCHAR(80)    NOT NULL CONSTRAINT DF_Users_DisplayName DEFAULT (N''),
        IsActive        BIT             NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT (1),
        CONSTRAINT UQ_Users_Username UNIQUE (Username)
    );
END
GO

IF OBJECT_ID(N'dbo.Roles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Roles (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
        Name            NVARCHAR(50)    NOT NULL,
        IsSystemRole    BIT             NOT NULL CONSTRAINT DF_Roles_IsSystemRole DEFAULT (0),
        SortOrder       INT             NOT NULL CONSTRAINT DF_Roles_SortOrder DEFAULT (0)
    );
END
GO

IF OBJECT_ID(N'dbo.UserRoles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserRoles (
        Id      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_UserRoles PRIMARY KEY,
        UserId  INT NOT NULL,
        RoleId  INT NOT NULL,
        CONSTRAINT FK_UserRoles_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (Id) ON DELETE CASCADE,
        CONSTRAINT FK_UserRoles_Roles FOREIGN KEY (RoleId) REFERENCES dbo.Roles (Id) ON DELETE NO ACTION,
        CONSTRAINT UQ_UserRoles_UserId_RoleId UNIQUE (UserId, RoleId)
    );
END
GO

IF OBJECT_ID(N'dbo.RolePermissions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RolePermissions (
        Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RolePermissions PRIMARY KEY,
        RoleId          INT NOT NULL,
        PermissionCode  NVARCHAR(60) NOT NULL,
        CONSTRAINT FK_RolePermissions_Roles FOREIGN KEY (RoleId) REFERENCES dbo.Roles (Id) ON DELETE CASCADE,
        CONSTRAINT UQ_RolePermissions_RoleId_PermissionCode UNIQUE (RoleId, PermissionCode)
    );
END
GO

IF OBJECT_ID(N'dbo.UserHouses', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserHouses (
        Id      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_UserHouses PRIMARY KEY,
        UserId  INT NOT NULL,
        HouseId INT NOT NULL,
        CONSTRAINT FK_UserHouses_Users FOREIGN KEY (UserId) REFERENCES dbo.Users (Id) ON DELETE CASCADE,
        CONSTRAINT FK_UserHouses_Houses FOREIGN KEY (HouseId) REFERENCES dbo.Houses (Id) ON DELETE CASCADE,
        CONSTRAINT UQ_UserHouses_UserId_HouseId UNIQUE (UserId, HouseId)
    );
END
GO

-- ============================================================
-- Seed data (mirrors FarmService.InitializeAsync's first-run seed)
-- ============================================================

-- 3 default houses
IF NOT EXISTS (SELECT 1 FROM dbo.Houses)
BEGIN
    INSERT INTO dbo.Houses (Name, Code, CapacityBirds, Notes, SortOrder) VALUES
        (N'House 1', NULL, 0, N'', 1),
        (N'House 2', NULL, 0, N'', 2),
        (N'House 3', NULL, 0, N'', 3);
END
GO

-- Built-in Admin role + every permission code + the admin user, only on an empty install
IF NOT EXISTS (SELECT 1 FROM dbo.Roles)
BEGIN
    DECLARE @RoleId INT;
    INSERT INTO dbo.Roles (Name, IsSystemRole, SortOrder) VALUES (N'Admin', 1, 1);
    SET @RoleId = SCOPE_IDENTITY();

    INSERT INTO dbo.RolePermissions (RoleId, PermissionCode) VALUES
        (@RoleId, N'Houses.View'),       (@RoleId, N'Houses.Add'),       (@RoleId, N'Houses.Edit'),       (@RoleId, N'Houses.Delete'),
        (@RoleId, N'Batches.View'),      (@RoleId, N'Batches.Add'),      (@RoleId, N'Batches.Edit'),      (@RoleId, N'Batches.Delete'),
        (@RoleId, N'DailyRecords.View'), (@RoleId, N'DailyRecords.Add'), (@RoleId, N'DailyRecords.Edit'), (@RoleId, N'DailyRecords.Delete'),
        (@RoleId, N'Feed.View'),         (@RoleId, N'Feed.Add'),         (@RoleId, N'Feed.Edit'),         (@RoleId, N'Feed.Delete'),
        (@RoleId, N'Health.View'),       (@RoleId, N'Health.Add'),       (@RoleId, N'Health.Edit'),       (@RoleId, N'Health.Delete'),
        (@RoleId, N'Lifting.View'),      (@RoleId, N'Lifting.Add'),      (@RoleId, N'Lifting.Edit'),      (@RoleId, N'Lifting.Delete'),
        (@RoleId, N'Settlement.View'),   (@RoleId, N'Settlement.Add'),   (@RoleId, N'Settlement.Edit'),
        (@RoleId, N'Expenses.View'),     (@RoleId, N'Expenses.Add'),     (@RoleId, N'Expenses.Edit'),     (@RoleId, N'Expenses.Delete'),
        (@RoleId, N'Reports.View'),
        (@RoleId, N'Integrators.View'),  (@RoleId, N'Integrators.Add'),  (@RoleId, N'Integrators.Edit'),  (@RoleId, N'Integrators.Delete'),
        (@RoleId, N'Users.View'),        (@RoleId, N'Users.Add'),        (@RoleId, N'Users.Edit'),        (@RoleId, N'Users.Delete'),
        (@RoleId, N'Roles.View'),        (@RoleId, N'Roles.Add'),        (@RoleId, N'Roles.Edit'),        (@RoleId, N'Roles.Delete');

    DECLARE @UserId INT;
    -- Username: admin / Password: admin  (PBKDF2-SHA256, 100,000 iterations — Services/PasswordHasher.cs)
    -- CHANGE THIS PASSWORD after first login.
    INSERT INTO dbo.Users (Username, PasswordHash, PasswordSalt, DisplayName, IsActive) VALUES
        (N'admin', N'ri5JrsCLPvcFRq9A16cta7fG4LL4H/JaYSI20B3e+Tc=', N'GdB6w/OcRGAkYg5XlVmJkg==', N'Administrator', 1);
    SET @UserId = SCOPE_IDENTITY();

    INSERT INTO dbo.UserRoles (UserId, RoleId) VALUES (@UserId, @RoleId);
END
GO

PRINT 'AmrPoultryFarm schema is ready.';
