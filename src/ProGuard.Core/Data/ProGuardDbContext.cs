using Microsoft.EntityFrameworkCore;
using ProGuard.Core.Models;

namespace ProGuard.Core.Data;

public class ProGuardDbContext : DbContext
{
    public DbSet<ProcessRule> ProcessRules { get; set; } = null!;
    public DbSet<WatchdogRule> WatchdogRules { get; set; } = null!;
    public DbSet<PowerProfile> PowerProfiles { get; set; } = null!;
    public DbSet<ProBalanceAction> ProBalanceActions { get; set; } = null!;
    public DbSet<AppSettings> Settings { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite("Data Source=proguard.db");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSettings>().HasData(
            new AppSettings { Id = 1 }
        );
    }
}
