using AmrPoultryFarmWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace AmrPoultryFarmWeb.Data;

public class FarmDbContext : DbContext
{
    // Connection string now comes from DI (Program.cs -> AddDbContextFactory), configured from
    // appsettings/ConnectionStrings:Farm — replaces the MAUI app's FileSystem.AppDataDirectory
    // path, which doesn't exist outside a MAUI host.
    public FarmDbContext(DbContextOptions<FarmDbContext> options) : base(options) { }

    public DbSet<House> Houses => Set<House>();
    public DbSet<Integrator> Integrators => Set<Integrator>();
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<House>()
            .HasMany(h => h.Batches).WithOne(b => b.House!)
            .HasForeignKey(b => b.HouseId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Integrator>()
            .HasMany(i => i.Batches).WithOne(b => b.Integrator!)
            .HasForeignKey(b => b.IntegratorId).OnDelete(DeleteBehavior.Restrict);

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
    }
}
