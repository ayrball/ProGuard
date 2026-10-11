using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProGuard.Core.Data;
using ProGuard.Core.Models;

namespace ProGuard.Tests;

public class SchemaUpgraderTests : IDisposable
{
    /// <summary>The real model pointed at a database file of the test's choosing.</summary>
    private sealed class FileDbContext : ProGuardDbContext
    {
        private readonly string _path;
        public FileDbContext(string path) => _path = path;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlite($"Data Source={_path}");
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ProGuard.Tests", "schema-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SchemaUpgraderTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "proguard.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void AddsMissingColumns_WithDefaults_AndKeepsExistingRows()
    {
        // A ProcessRules table from before most of the rule columns existed, and a Settings table that only
        // knew about ProBalanceEnabled (stored as false, so we can tell it was not overwritten).
        Exec("""
            CREATE TABLE "ProcessRules" ("Id" INTEGER NOT NULL CONSTRAINT "PK_ProcessRules" PRIMARY KEY AUTOINCREMENT, "ProcessName" TEXT NOT NULL);
            INSERT INTO "ProcessRules" ("ProcessName") VALUES ('game.exe');
            CREATE TABLE "Settings" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY AUTOINCREMENT, "ProBalanceEnabled" INTEGER NOT NULL);
            INSERT INTO "Settings" ("Id", "ProBalanceEnabled") VALUES (1, 0);
            """);

        using (var db = new FileDbContext(_path)) db.EnsureSchema();

        var ruleColumns = Columns("ProcessRules");
        Assert.Equal(
            new[] { "Id", "ProcessName", "PriorityClass", "AffinityMask", "CpuLimitPercent", "MaxInstances",
                    "PowerProfileGuid", "ExcludeFromProBalance", "DisallowExecution" }.OrderBy(n => n),
            ruleColumns.Keys.OrderBy(n => n));
        Assert.Equal(("INTEGER", true, null), ruleColumns["PriorityClass"]);
        Assert.Equal(("TEXT", true, null), ruleColumns["PowerProfileGuid"]);
        Assert.Equal(("INTEGER", false, "0"), ruleColumns["ExcludeFromProBalance"]);

        var settingsColumns = Columns("Settings");
        // Non-null columns get the value a new AppSettings would have, which is what a fresh database's seed row gets.
        Assert.Equal(("INTEGER", false, "5"), settingsColumns["ProBalanceSensitivity"]);
        Assert.Equal(("INTEGER", false, "1"), settingsColumns["StartWithWindows"]);
        Assert.Equal(("TEXT", false, "'System'"), settingsColumns["Theme"]);
        Assert.Equal(("INTEGER", false, "0"), settingsColumns["SmartTrimEnabled"]);
        Assert.Equal(("TEXT", true, null), settingsColumns["IdlePowerPlanGuid"]);

        using (var db = new FileDbContext(_path))
        {
            var rule = db.ProcessRules.Single();
            Assert.Equal("game.exe", rule.ProcessName);
            Assert.Null(rule.PriorityClass);
            Assert.False(rule.ExcludeFromProBalance);

            var settings = db.Settings.Single();
            Assert.Equal(1, settings.Id);
            Assert.False(settings.ProBalanceEnabled); // existing value untouched
            Assert.Equal(5, settings.ProBalanceSensitivity);
            Assert.Equal(25.0, settings.ProBalanceCpuThreshold);
            Assert.Equal(300, settings.SmartTrimIntervalSeconds);
            Assert.Equal(1024, settings.SmartTrimMemoryThresholdMB);
            Assert.True(settings.MinimizeToTray);
            Assert.Equal("System", settings.Theme);
            Assert.Null(settings.IdlePowerPlanGuid);

            // And the upgraded tables work for new rows.
            db.ProcessRules.Add(new ProcessRule { ProcessName = "new.exe", PriorityClass = 64, AffinityMask = 3 });
            db.SaveChanges();
            Assert.Equal(64, db.ProcessRules.Single(r => r.ProcessName == "new.exe").PriorityClass);
        }
    }

    [Fact]
    public void CreatesMissingTables_IncludingSeedRows()
    {
        Exec("""
            CREATE TABLE "ProcessRules" ("Id" INTEGER NOT NULL CONSTRAINT "PK_ProcessRules" PRIMARY KEY AUTOINCREMENT, "ProcessName" TEXT NOT NULL);
            INSERT INTO "ProcessRules" ("ProcessName") VALUES ('game.exe');
            """);

        using (var db = new FileDbContext(_path)) db.EnsureSchema();

        var tables = Names("SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.Contains("WatchdogRules", tables);
        Assert.Contains("PowerProfiles", tables);
        Assert.Contains("ProBalanceActions", tables);
        Assert.Contains("Settings", tables);

        using var check = new FileDbContext(_path);
        Assert.Equal("game.exe", check.ProcessRules.Single().ProcessName);
        // The Settings seed row a fresh database gets is inserted into a newly created Settings table too.
        var settings = check.Settings.Single();
        Assert.Equal(1, settings.Id);
        Assert.True(settings.ProBalanceEnabled);

        check.WatchdogRules.Add(new WatchdogRule { TargetProcessName = "leak.exe", ThresholdValue = 10 });
        check.SaveChanges();
        Assert.Single(check.WatchdogRules);
    }

    [Fact]
    public void CreatesFreshDatabase_AndIsIdempotent()
    {
        using (var db = new FileDbContext(_path)) db.EnsureSchema();
        var tables = Names("SELECT name FROM sqlite_master WHERE type = 'table'");
        var columns = Columns("Settings");

        using (var db = new FileDbContext(_path))
        {
            db.EnsureSchema();
            db.EnsureSchema();
            Assert.Single(db.Settings);
        }

        Assert.Equal(tables, Names("SELECT name FROM sqlite_master WHERE type = 'table'"));
        Assert.Equal(columns, Columns("Settings"));
    }

    [Fact]
    public void SecondCallOnUpgradedDatabase_ChangesNothing()
    {
        Exec("""
            CREATE TABLE "ProcessRules" ("Id" INTEGER NOT NULL CONSTRAINT "PK_ProcessRules" PRIMARY KEY AUTOINCREMENT, "ProcessName" TEXT NOT NULL);
            INSERT INTO "ProcessRules" ("ProcessName") VALUES ('game.exe');
            """);

        using (var db = new FileDbContext(_path)) db.EnsureSchema();
        var schema = Names("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL");

        // A different context type gets its own cache entry, so this really re-reads the schema.
        using (var db = new OtherFileDbContext(_path)) db.EnsureSchema();

        Assert.Equal(schema, Names("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL"));
        using var check = new FileDbContext(_path);
        Assert.Single(check.ProcessRules);
        Assert.Single(check.Settings);
    }

    [Fact]
    public void RecreatesDatabase_WhenTheFileWasDeletedAfterACheck()
    {
        using (var db = new FileDbContext(_path)) db.EnsureSchema();
        SqliteConnection.ClearAllPools();
        File.Delete(_path);

        using var again = new FileDbContext(_path);
        again.EnsureSchema();
        Assert.Single(again.Settings);
    }

    [Fact]
    public async Task ConcurrentCallers_AllSucceed_AndUpgradeOnce()
    {
        Exec("""
            CREATE TABLE "ProcessRules" ("Id" INTEGER NOT NULL CONSTRAINT "PK_ProcessRules" PRIMARY KEY AUTOINCREMENT, "ProcessName" TEXT NOT NULL);
            INSERT INTO "ProcessRules" ("ProcessName") VALUES ('game.exe');
            CREATE TABLE "Settings" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY AUTOINCREMENT, "ProBalanceEnabled" INTEGER NOT NULL);
            INSERT INTO "Settings" ("Id", "ProBalanceEnabled") VALUES (1, 0);
            """);

        // Two context types so the per-process cache can't let callers skip the work: like the engine and the
        // UI, each of them has to look at the database itself.
        const int callers = 8;
        using var start = new Barrier(callers);
        var tasks = Enumerable.Range(0, callers).Select(i => Task.Run(() =>
        {
            using DbContext db = i % 2 == 0 ? new FileDbContext(_path) : new OtherFileDbContext(_path);
            start.SignalAndWait();
            db.EnsureSchema();
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Contains("DisallowExecution", Columns("ProcessRules").Keys);
        Assert.Contains("Theme", Columns("Settings").Keys);
        using var check = new FileDbContext(_path);
        Assert.Single(check.ProcessRules);
        Assert.False(check.Settings.Single().ProBalanceEnabled);
        Assert.Empty(check.WatchdogRules);
    }

    [Fact]
    public async Task ConcurrentCallers_OnMissingDatabase_AllSucceed()
    {
        const int callers = 8;
        using var start = new Barrier(callers);
        var tasks = Enumerable.Range(0, callers).Select(i => Task.Run(() =>
        {
            using DbContext db = i % 2 == 0 ? new FileDbContext(_path) : new OtherFileDbContext(_path);
            start.SignalAndWait();
            db.EnsureSchema();
        })).ToArray();
        await Task.WhenAll(tasks);

        using var check = new FileDbContext(_path);
        Assert.Single(check.Settings);
    }

    /// <summary>Same as <see cref="FileDbContext"/>, but a different type, so it has its own schema-check cache entry.</summary>
    private sealed class OtherFileDbContext : ProGuardDbContext
    {
        private readonly string _path;
        public OtherFileDbContext(string path) => _path = path;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlite($"Data Source={_path}");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        return connection;
    }

    private void Exec(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private List<string> Names(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>name → (declared type, nullable, default SQL) from PRAGMA table_info.</summary>
    private Dictionary<string, (string Type, bool Nullable, string? Default)> Columns(string table)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('{table}')";
        using var reader = command.ExecuteReader();
        var columns = new Dictionary<string, (string, bool, string?)>();
        while (reader.Read())
            columns[reader.GetString(0)] = (reader.GetString(1), reader.GetInt64(2) == 0,
                reader.IsDBNull(3) ? null : reader.GetString(3));
        return columns;
    }
}
