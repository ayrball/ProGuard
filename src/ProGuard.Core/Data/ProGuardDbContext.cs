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
    public DbSet<ConfigProfile> ConfigProfiles { get; set; } = null!;

    /// <summary>
    /// Environment variable that overrides <see cref="DatabasePath"/> (used by tests and portable installs).
    /// </summary>
    public const string DatabasePathVariable = "PROGUARD_DB_PATH";

    /// <summary>
    /// Absolute path of the database shared by the engine and the UI. It must not be relative: the two
    /// processes run from different directories and would otherwise each get their own copy.
    /// </summary>
    public static string DatabasePath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(DatabasePathVariable);
            if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ProGuard", "proguard.db");
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var path = DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        optionsBuilder.UseSqlite($"Data Source={path}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSettings>().HasData(
            new AppSettings { Id = 1 }
        );
    }
}
