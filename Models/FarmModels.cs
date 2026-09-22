using System.ComponentModel.DataAnnotations;
using AmrPoultryFarmWeb.Data;

namespace AmrPoultryFarmWeb.Models;

public enum BatchStatus { Active = 0, Closed = 1 }

/// <summary>A physical broiler house / shed on the farm. Houses run many batches over their lifetime.</summary>
public class House : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    [Required, MaxLength(50)]
    public string Name { get; set; } = "";

    [MaxLength(20)]
    public string? Code { get; set; }

    public int CapacityBirds { get; set; }

    [MaxLength(300)]
    public string Notes { get; set; } = "";

    public int SortOrder { get; set; }

    public List<Batch> Batches { get; set; } = new();
}

/// <summary>A company/integrator the farm grows birds for. Managed centrally so batches pick from one list.</summary>
public class Integrator : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    [Required, MaxLength(80)]
    public string Name { get; set; } = "";

    public decimal BagWeightKg { get; set; } = 50m;

    [MaxLength(300)]
    public string Notes { get; set; } = "";

    public int SortOrder { get; set; }

    public List<Batch> Batches { get; set; } = new();
}

/// <summary>One growing cycle / flock placed in a house.</summary>
public class Batch : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    [Required, MaxLength(30)]
    public string BatchCode { get; set; } = "";          // e.g. AMR-2026-04

    public int HouseId { get; set; }
    public House? House { get; set; }

    public int IntegratorId { get; set; }
    public Integrator? Integrator { get; set; }

    [MaxLength(40)]
    public string BranchCode { get; set; } = "";

    [MaxLength(40)]
    public string Breed { get; set; } = "Cobb 430Y";

    public DateTime PlacementDate { get; set; } = DateTime.Today;
    public int ChicksPlaced { get; set; }
    public decimal ChickCostPerBird { get; set; }        // as per integrator DC
    public decimal TargetWeightKg { get; set; } = 2.3m;

    public BatchStatus Status { get; set; } = BatchStatus.Active;
    public DateTime? ClosedDate { get; set; }

    [MaxLength(300)]
    public string Notes { get; set; } = "";

    public List<DailyRecord> DailyRecords { get; set; } = new();
    public List<FeedDelivery> FeedDeliveries { get; set; } = new();
    public List<HealthEvent> HealthEvents { get; set; } = new();
    public List<Expense> Expenses { get; set; } = new();
    public List<Lifting> Liftings { get; set; } = new();
    public Settlement? Settlement { get; set; }

    // ---- Derived helpers (computed in app, not mapped) ----
    public int AgeDays => (int)((Status == BatchStatus.Active ? DateTime.Today : ClosedDate ?? DateTime.Today) - PlacementDate.Date).TotalDays;
}

/// <summary>Daily house record — the core of farm data entry.</summary>
public class DailyRecord : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    public DateTime Date { get; set; } = DateTime.Today;

    public int Mortality { get; set; }
    public int Culls { get; set; }
    public decimal FeedConsumedBags { get; set; }         // entered by the user
    public decimal FeedConsumedKg { get; set; }           // = bags * the batch's integrator bag weight, computed on save
    public decimal AvgBodyWeightGm { get; set; }         // sample weighing
    public decimal WaterConsumedLtr { get; set; }
    public decimal MinTempC { get; set; }
    public decimal MaxTempC { get; set; }
    public decimal HumidityPct { get; set; }

    [MaxLength(300)]
    public string Remarks { get; set; } = "";
}

/// <summary>Feed received from the integrator (Pre-starter/Starter/Finisher).</summary>
public class FeedDelivery : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    public DateTime Date { get; set; } = DateTime.Today;

    [MaxLength(30)]
    public string FeedType { get; set; } = "Starter";    // Pre-starter / Starter / Finisher

    public int Bags { get; set; }
    public decimal BagWeightKg { get; set; } = 50m;
    public decimal TotalKg => Bags * BagWeightKg;

    [MaxLength(40)]
    public string DcNumber { get; set; } = "";           // delivery challan no.

    [MaxLength(200)]
    public string Remarks { get; set; } = "";
}

public enum HealthEventType { Vaccination = 0, Medication = 1, Supplement = 2, Disinfection = 3 }

/// <summary>Vaccination / medication / supplement given to the flock.</summary>
public class HealthEvent : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    public DateTime Date { get; set; } = DateTime.Today;
    public HealthEventType Type { get; set; } = HealthEventType.Vaccination;

    [MaxLength(80)]
    public string Name { get; set; } = "";               // e.g. ND Lasota, IBD, Vitamin C

    [MaxLength(40)]
    public string Dose { get; set; } = "";               // e.g. 1 drop/bird, 1 ml/ltr

    [MaxLength(30)]
    public string Route { get; set; } = "Drinking water"; // Eye drop / Water / Spray / Injection

    public decimal Cost { get; set; }

    [MaxLength(200)]
    public string Remarks { get; set; } = "";
}

/// <summary>Farm-side running expense (electricity, litter, labour, diesel, brooding...).</summary>
public class Expense : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int? BatchId { get; set; }                    // null = general farm expense
    public Batch? Batch { get; set; }

    public DateTime Date { get; set; } = DateTime.Today;

    [MaxLength(40)]
    public string Category { get; set; } = "Electricity"; // Litter / Labour / Diesel / Gas / Repairs / Transport / Other

    [MaxLength(120)]
    public string Description { get; set; } = "";

    public decimal Amount { get; set; }
}

/// <summary>Bird lifting / harvest by the integrator.</summary>
public class Lifting : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    public DateTime Date { get; set; } = DateTime.Today;
    public int BirdsLifted { get; set; }
    public decimal TotalWeightKg { get; set; }

    [MaxLength(40)]
    public string VehicleNumber { get; set; } = "";

    [MaxLength(40)]
    public string DcNumber { get; set; } = "";

    public decimal AvgWeightKg => BirdsLifted > 0 ? Math.Round(TotalWeightKg / BirdsLifted, 3) : 0;

    [MaxLength(200)]
    public string Remarks { get; set; } = "";
}

/// <summary>Final settlement figures from the integrator when the batch closes.</summary>
public class Settlement : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    public DateTime Date { get; set; } = DateTime.Today;

    public decimal GrowingChargePerKg { get; set; }      // company rate
    public decimal PerformanceIncentive { get; set; }    // FCR / EEF bonus
    public decimal Deductions { get; set; }              // mortality above norm, etc.
    public decimal AmountReceived { get; set; }

    [MaxLength(300)]
    public string Remarks { get; set; } = "";
}

/// <summary>A person who can log into the app. Deliberately NOT ITenantScoped/query-filtered — login
/// must be able to look up a username before any tenant is known. TenantId is null only for the one
/// platform Super Admin account; every tenant user has a real TenantId.</summary>
public class User
{
    public int Id { get; set; }

    public int? TenantId { get; set; }

    /// <summary>Platform-level account (manages clients only, never sees tenant farm data). Not tied
    /// to any tenant, so TenantId is null for this user.</summary>
    public bool IsSuperAdmin { get; set; }

    [Required, MaxLength(40)]
    public string Username { get; set; } = "";

    // Not [Required]: these are computed server-side by AuthService.SaveUserAsync from the
    // form's separate newPassword field, so they're still empty when DataAnnotationsValidator
    // runs against the bound model on submit.
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";

    [MaxLength(80)]
    public string DisplayName { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public List<UserRole> UserRoles { get; set; } = new();
    public List<UserHouse> UserHouses { get; set; } = new();
}

/// <summary>A named set of permissions. Admins can create any number of these.</summary>
public class Role : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    [Required, MaxLength(50)]
    public string Name { get; set; } = "";

    // Protects the seeded "Admin" role from being deleted/renamed away, which would
    // otherwise be able to lock every user out of the app with no way back in.
    public bool IsSystemRole { get; set; }

    public int SortOrder { get; set; }

    public List<UserRole> UserRoles { get; set; } = new();
    public List<RolePermission> RolePermissions { get; set; } = new();
}

/// <summary>Join: which roles a user holds. Effective permissions are the union across all of them.</summary>
public class UserRole : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public int RoleId { get; set; }
    public Role? Role { get; set; }
}

/// <summary>Join: which permission codes a role grants. Codes come from the code-defined <see cref="Permissions"/> catalog.</summary>
public class RolePermission : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int RoleId { get; set; }
    public Role? Role { get; set; }

    [Required, MaxLength(60)]
    public string PermissionCode { get; set; } = "";
}

/// <summary>Join: which houses a user is scoped to. Empty set + non-system-admin = sees nothing.</summary>
public class UserHouse : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public int HouseId { get; set; }
    public House? House { get; set; }
}

/// <summary>Computed live performance snapshot for a batch.</summary>
public class BatchPerformance
{
    public int AgeDays { get; set; }
    public int ChicksPlaced { get; set; }
    public int TotalMortality { get; set; }
    public int TotalCulls { get; set; }
    public int BirdsLifted { get; set; }
    public int LiveBirds { get; set; }
    public decimal MortalityPct { get; set; }
    public decimal LivabilityPct { get; set; }
    public decimal FeedReceivedKg { get; set; }
    public decimal FeedConsumedKg { get; set; }
    public decimal FeedStockKg { get; set; }
    public decimal AvgBodyWeightKg { get; set; }
    public decimal TotalLiveWeightKg { get; set; }
    public decimal Fcr { get; set; }
    public decimal Eef { get; set; }                     // European Efficiency Factor
    public decimal DailyGainGm { get; set; }
    public decimal TotalExpenses { get; set; }
}
