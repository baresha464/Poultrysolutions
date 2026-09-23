using AmrPoultryFarmWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace AmrPoultryFarmWeb.Data;

public class FarmDbContext : DbContext
{
    // Connection string now comes from DI (Program.cs -> AddDbContextFactory), configured from
    // appsettings/ConnectionStrings:Farm — replaces the MAUI app's FileSystem.AppDataDirectory
    // path, which doesn't exist outside a MAUI host.
    public FarmDbContext(DbContextOptions<FarmDbContext> options) : base(options) { }

    // Set by the caller (FarmService/AuthService's NewContext(), or a platform-admin routine)
    // right after CreateDbContext(). Deliberately a plain mutable property, not DI-injected —
    // IDbContextFactory-created contexts don't reliably support scoped-service constructor
    // injection. Every ITenantScoped query filter below closes over this property, and
    // SaveChanges uses it to auto-stamp new rows. Left null = every tenant-scoped table reads as
    // empty (fail-closed), which is what makes it impossible for the Super Admin's own context
    // (TenantId always null) to ever see tenant farm data.
    public int? TenantId { get; set; }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<House> Houses => Set<House>();
    public DbSet<Integrator> Integrators => Set<Integrator>();
    public DbSet<TenantIntegrator> TenantIntegrators => Set<TenantIntegrator>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<DailyRecord> DailyRecords => Set<DailyRecord>();
    public DbSet<FeedDelivery> FeedDeliveries => Set<FeedDelivery>();
    public DbSet<HealthEvent> HealthEvents => Set<HealthEvent>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<Lifting> Liftings => Set<Lifting>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserHouse> UserHouses => Set<UserHouse>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<TenantFeature> TenantFeatures => Set<TenantFeature>();
    public DbSet<FeatureRequest> FeatureRequests => Set<FeatureRequest>();
    public DbSet<DemoRequest> DemoRequests => Set<DemoRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Fail-closed tenant isolation: every ITenantScoped table only ever returns rows for the
        // TenantId currently set on this context instance, and returns nothing at all if it's
        // unset. User is deliberately excluded — login needs to look up a username before any
        // tenant is known (see User's doc comment) — the handful of AuthService methods that read
        // tenant users filter TenantId by hand instead.
        modelBuilder.Entity<House>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        // Integrator itself is platform-level (no filter); what a tenant may use is this join.
        modelBuilder.Entity<TenantIntegrator>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<Batch>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<DailyRecord>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<FeedDelivery>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<HealthEvent>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<Expense>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<Lifting>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<Settlement>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<Role>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<RolePermission>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<UserRole>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<UserHouse>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);

        modelBuilder.Entity<House>()
            .HasMany(h => h.Batches).WithOne(b => b.House!)
            .HasForeignKey(b => b.HouseId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Integrator>()
            .HasMany(i => i.Batches).WithOne(b => b.Integrator!)
            .HasForeignKey(b => b.IntegratorId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TenantIntegrator>()
            .HasOne(ti => ti.Integrator).WithMany(i => i.Tenants)
            .HasForeignKey(ti => ti.IntegratorId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TenantIntegrator>().HasIndex(ti => new { ti.TenantId, ti.IntegratorId }).IsUnique();

        modelBuilder.Entity<House>().HasIndex(h => h.TenantId);
        modelBuilder.Entity<Batch>().HasIndex(b => b.TenantId);
        modelBuilder.Entity<Role>().HasIndex(r => r.TenantId);
        modelBuilder.Entity<User>().HasIndex(u => u.TenantId);

        modelBuilder.Entity<Batch>().HasIndex(b => b.HouseId);
        modelBuilder.Entity<Batch>().HasIndex(b => b.IntegratorId);
        modelBuilder.Entity<Batch>().HasIndex(b => b.Status);
        modelBuilder.Entity<Batch>().HasIndex(b => b.PlacementDate);
        modelBuilder.Entity<DailyRecord>().HasIndex(d => new { d.BatchId, d.Date });
        modelBuilder.Entity<FeedDelivery>().HasIndex(f => f.BatchId);
        modelBuilder.Entity<HealthEvent>().HasIndex(h => h.BatchId);
        modelBuilder.Entity<Lifting>().HasIndex(l => l.BatchId);
        modelBuilder.Entity<Expense>().HasIndex(e => e.BatchId);
        modelBuilder.Entity<Expense>().HasIndex(e => e.Date);

        modelBuilder.Entity<Batch>()
            .HasMany(b => b.DailyRecords).WithOne(d => d.Batch!)
            .HasForeignKey(d => d.BatchId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Batch>()
            .HasMany(b => b.FeedDeliveries).WithOne(f => f.Batch!)
            .HasForeignKey(f => f.BatchId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Batch>()
            .HasMany(b => b.HealthEvents).WithOne(h => h.Batch!)
            .HasForeignKey(h => h.BatchId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Batch>()
            .HasMany(b => b.Liftings).WithOne(l => l.Batch!)
            .HasForeignKey(l => l.BatchId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Batch>()
            .HasOne(b => b.Settlement).WithOne(s => s.Batch!)
            .HasForeignKey<Settlement>(s => s.BatchId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Expense>()
            .HasOne(e => e.Batch).WithMany(b => b.Expenses)
            .HasForeignKey(e => e.BatchId).OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<User>().HasIndex(u => u.Username).IsUnique();
        modelBuilder.Entity<User>().Property(u => u.PreferredLanguage).HasDefaultValue("en");

        modelBuilder.Entity<UserRole>()
            .HasOne(ur => ur.User).WithMany(u => u.UserRoles)
            .HasForeignKey(ur => ur.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserRole>()
            .HasOne(ur => ur.Role).WithMany(r => r.UserRoles)
            .HasForeignKey(ur => ur.RoleId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<UserRole>().HasIndex(ur => new { ur.UserId, ur.RoleId }).IsUnique();

        modelBuilder.Entity<RolePermission>()
            .HasOne(rp => rp.Role).WithMany(r => r.RolePermissions)
            .HasForeignKey(rp => rp.RoleId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RolePermission>().HasIndex(rp => new { rp.RoleId, rp.PermissionCode }).IsUnique();

        modelBuilder.Entity<UserHouse>()
            .HasOne(uh => uh.User).WithMany(u => u.UserHouses)
            .HasForeignKey(uh => uh.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserHouse>()
            .HasOne(uh => uh.House).WithMany()
            .HasForeignKey(uh => uh.HouseId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserHouse>().HasIndex(uh => new { uh.UserId, uh.HouseId }).IsUnique();

        // ---- WhatsApp notifications ----
        modelBuilder.Entity<NotificationPreference>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<NotificationLog>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<NotificationPreference>()
            .HasOne(p => p.User).WithMany()
            .HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<NotificationPreference>().HasIndex(p => p.UserId).IsUnique();
        modelBuilder.Entity<NotificationLog>()
            .HasOne<User>().WithMany()
            .HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
        // One row per (user, alert) is what makes "never send the same alert twice" hold even if
        // two worker ticks overlap.
        modelBuilder.Entity<NotificationLog>().HasIndex(l => new { l.UserId, l.DedupKey }).IsUnique();
        modelBuilder.Entity<NotificationLog>().HasIndex(l => new { l.TenantId, l.CreatedAtUtc });

        // ---- Optional features (Super Admin-managed) ----
        modelBuilder.Entity<TenantFeature>().Property(f => f.TenantId).ValueGeneratedNever();
        modelBuilder.Entity<TenantFeature>()
            .HasOne<Tenant>().WithOne()
            .HasForeignKey<TenantFeature>(f => f.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<FeatureRequest>().HasQueryFilter(e => TenantId != null && e.TenantId == TenantId);
        modelBuilder.Entity<FeatureRequest>().HasIndex(r => new { r.Status, r.CreatedAtUtc });
        modelBuilder.Entity<FeatureRequest>().HasIndex(r => new { r.TenantId, r.Feature });

        // Public landing-page leads (platform-level, Super Admin only).
        modelBuilder.Entity<DemoRequest>().HasIndex(r => new { r.Status, r.CreatedAtUtc });
    }

    // Stamps TenantId onto every newly-added ITenantScoped entity from this context's own
    // TenantId, so FarmService's CRUD methods never have to set it themselves. Entities that
    // already carry an explicit TenantId (tenant-provisioning, which inserts rows for a
    // newly-created tenant before this context's own TenantId is switched over) are left alone.
    private void StampTenantId()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added && entry.Entity is ITenantScoped scoped && scoped.TenantId == 0)
            {
                scoped.TenantId = TenantId ?? throw new InvalidOperationException(
                    $"Cannot insert a {entry.Entity.GetType().Name} row: FarmDbContext.TenantId is not set.");
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenantId();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTenantId();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
