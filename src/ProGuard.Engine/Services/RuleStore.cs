using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// SQLite-backed rule store with in-memory caching for fast lookups.
/// Process rules are keyed by <see cref="ProcessExtensions.NormalizeName"/> so that a rule saved as
/// "chrome.exe" is found for the running process "chrome".
/// </summary>
public class RuleStore : IRuleStore
{
    private readonly ILogger<RuleStore> _logger;
    private readonly IServiceProvider _serviceProvider;
    // Replaced wholesale on reload so rules deleted by the UI disappear from the cache too.
    private volatile ConcurrentDictionary<string, ProcessRule> _processRules = new(StringComparer.Ordinal);

    // Other processes (the UI when the engine isn't reachable, admin tools) may write the database file directly; we
    // notice by watching its files. In WAL mode a commit only touches "<db>-wal" until the next checkpoint, so the
    // main file's write time alone misses most changes.
    private string? _dbFilePath;
    private DatabaseStamp _lastLoadedStamp;
    private (int, int, int) _lastLoadedCounts = (-1, -1, -1);

    internal readonly record struct DatabaseStamp(DateTime DbWriteTimeUtc, DateTime WalWriteTimeUtc, long WalLength);

    // The watchdog timer enumerates these while IPC/UI threads mutate them, so every access goes through _sync
    // and readers get snapshots.
    private readonly object _sync = new();
    private readonly List<WatchdogRule> _watchdogRules = new();
    private readonly List<PowerProfile> _powerProfiles = new();
    private AppSettings _settings = new();

    public RuleStore(ILogger<RuleStore> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        LoadRules();
    }

    /// <summary>
    /// Reloads every cache from the database if the database file has been written since the last load,
    /// e.g. because the UI added or removed a rule. Returns true when a reload happened.
    /// </summary>
    public bool ReloadIfChanged()
    {
        if (string.IsNullOrEmpty(_dbFilePath) || !File.Exists(_dbFilePath)) return false;

        if (Stamp(_dbFilePath) == _lastLoadedStamp) return false;

        // The engine's own writes (e.g. the ProBalance log) land here too, so this is routine.
        _logger.LogDebug("Database changed on disk, reloading rules");
        LoadRules();
        return true;
    }

    /// <summary>What changes when anyone commits to the database at <paramref name="dbPath"/>, in either journal mode.</summary>
    internal static DatabaseStamp Stamp(string dbPath)
    {
        var wal = new FileInfo(dbPath + "-wal");
        return new DatabaseStamp(File.GetLastWriteTimeUtc(dbPath),
            wal.Exists ? wal.LastWriteTimeUtc : default, wal.Exists ? wal.Length : -1);
    }

    private void LoadRules()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
            db.EnsureSchema();

            // Take the timestamp before reading so a write that lands mid-load still triggers another reload.
            _dbFilePath = db.Database.GetDbConnection().DataSource;
            if (!string.IsNullOrEmpty(_dbFilePath) && File.Exists(_dbFilePath))
                _lastLoadedStamp = Stamp(_dbFilePath);

            var processRules = new ConcurrentDictionary<string, ProcessRule>(StringComparer.Ordinal);
            foreach (var rule in db.ProcessRules.ToList())
            {
                processRules[ProcessExtensions.NormalizeName(rule.ProcessName)] = rule;
            }
            _processRules = processRules;

            lock (_sync)
            {
                _watchdogRules.Clear();
                _watchdogRules.AddRange(db.WatchdogRules.ToList());

                _powerProfiles.Clear();
                _powerProfiles.AddRange(db.PowerProfiles.ToList());

                _settings = db.Settings.FirstOrDefault() ?? new AppSettings();
            }

            var counts = (_processRules.Count, _watchdogRules.Count, _powerProfiles.Count);
            _logger.Log(counts == _lastLoadedCounts ? LogLevel.Debug : LogLevel.Information,
                "Loaded {RuleCount} process rules, {WdCount} watchdog rules, {PpCount} power profiles", counts.Item1, counts.Item2, counts.Item3);
            _lastLoadedCounts = counts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading rules from DB");
        }
    }

    // === Process Rules ===

    public ProcessRule? GetProcessRule(string processName)
    {
        return _processRules.TryGetValue(ProcessExtensions.NormalizeName(processName), out var rule) ? rule : null;
    }

    public IEnumerable<ProcessRule> GetProcessRules() => _processRules.Values.ToList();
    public IEnumerable<ProcessRule> GetAllProcessRules() => _processRules.Values.ToList();

    public void AddProcessRule(ProcessRule rule) => SaveProcessRule(rule);

    public void SaveProcessRule(ProcessRule rule)
    {
        var key = ProcessExtensions.NormalizeName(rule.ProcessName);
        string? previousKey = null;

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();

            // Match by Id first (supports renames), then by name regardless of ".exe"/case.
            var existing = rule.Id != 0 ? db.ProcessRules.Find(rule.Id) : null;
            existing ??= db.ProcessRules.AsEnumerable()
                .FirstOrDefault(r => ProcessExtensions.NormalizeName(r.ProcessName) == key);

            if (existing != null)
            {
                previousKey = ProcessExtensions.NormalizeName(existing.ProcessName);
                rule.Id = existing.Id; // the key can't be changed through SetValues
                db.Entry(existing).CurrentValues.SetValues(rule);
            }
            else
            {
                db.ProcessRules.Add(rule);
            }

            db.SaveChanges();
        }

        // Only touch the cache once the DB write has succeeded so the two can't diverge.
        if (previousKey != null && previousKey != key) _processRules.TryRemove(previousKey, out _);
        _processRules[key] = rule;
    }

    public void UpdateProcessRule(ProcessRule rule) => SaveProcessRule(rule);

    public void DeleteProcessRule(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        var rule = db.ProcessRules.Find(id);
        if (rule != null)
        {
            db.ProcessRules.Remove(rule);
            db.SaveChanges();
            _processRules.TryRemove(ProcessExtensions.NormalizeName(rule.ProcessName), out _);
        }
    }

    // === Watchdog Rules ===

    public IEnumerable<WatchdogRule> GetWatchdogRules()
    {
        lock (_sync) return _watchdogRules.ToList();
    }

    public void AddWatchdogRule(WatchdogRule rule)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        db.WatchdogRules.Add(rule);
        db.SaveChanges();
        lock (_sync) _watchdogRules.Add(rule);
    }

    public void UpdateWatchdogRule(WatchdogRule rule)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        var existing = db.WatchdogRules.Find(rule.Id);
        if (existing != null)
        {
            db.Entry(existing).CurrentValues.SetValues(rule);
            db.SaveChanges();
            lock (_sync)
            {
                var idx = _watchdogRules.FindIndex(r => r.Id == rule.Id);
                if (idx >= 0) _watchdogRules[idx] = rule;
            }
        }
    }

    public void DeleteWatchdogRule(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        var rule = db.WatchdogRules.Find(id);
        if (rule != null)
        {
            db.WatchdogRules.Remove(rule);
            db.SaveChanges();
            lock (_sync) _watchdogRules.RemoveAll(r => r.Id == id);
        }
    }

    // === Power Profiles ===

    public IEnumerable<PowerProfile> GetPowerProfiles()
    {
        lock (_sync) return _powerProfiles.ToList();
    }

    public void AddPowerProfile(PowerProfile profile)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        db.PowerProfiles.Add(profile);
        db.SaveChanges();
        lock (_sync) _powerProfiles.Add(profile);
    }

    public void UpdatePowerProfile(PowerProfile profile)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        var existing = db.PowerProfiles.Find(profile.Id);
        if (existing != null)
        {
            db.Entry(existing).CurrentValues.SetValues(profile);
            db.SaveChanges();
            lock (_sync)
            {
                var idx = _powerProfiles.FindIndex(p => p.Id == profile.Id);
                if (idx >= 0) _powerProfiles[idx] = profile;
            }
        }
    }

    public void DeletePowerProfile(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        var profile = db.PowerProfiles.Find(id);
        if (profile != null)
        {
            db.PowerProfiles.Remove(profile);
            db.SaveChanges();
            lock (_sync) _powerProfiles.RemoveAll(p => p.Id == id);
        }
    }

    // === Settings ===

    public AppSettings GetSettings()
    {
        lock (_sync) return _settings;
    }

    public void UpdateSettings(AppSettings settings)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
            var existing = db.Settings.FirstOrDefault();
            if (existing != null)
            {
                settings.Id = existing.Id; // the key can't be changed through SetValues
                db.Entry(existing).CurrentValues.SetValues(settings);
            }
            else
            {
                db.Settings.Add(settings);
            }
            db.SaveChanges();
        }

        lock (_sync) _settings = settings;
    }

    // === Config profiles ===

    public void ReplaceConfiguration(IReadOnlyList<ProcessRule> processRules, IReadOnlyList<WatchdogRule> watchdogRules, AppSettings settings)
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
            using var transaction = db.Database.BeginTransaction();

            db.ProcessRules.RemoveRange(db.ProcessRules.ToList());
            db.WatchdogRules.RemoveRange(db.WatchdogRules.ToList());
            foreach (var rule in processRules) rule.Id = 0;
            foreach (var rule in watchdogRules) rule.Id = 0;
            db.ProcessRules.AddRange(processRules);
            db.WatchdogRules.AddRange(watchdogRules);

            var existing = db.Settings.FirstOrDefault();
            if (existing != null)
            {
                settings.Id = existing.Id;
                db.Entry(existing).CurrentValues.SetValues(settings);
            }
            else
            {
                db.Settings.Add(settings);
            }

            db.SaveChanges();
            transaction.Commit();
        }

        // Only once the transaction has committed, so the caches never show half a switch.
        var rules = new ConcurrentDictionary<string, ProcessRule>(StringComparer.Ordinal);
        foreach (var rule in processRules) rules[ProcessExtensions.NormalizeName(rule.ProcessName)] = rule;
        lock (_sync)
        {
            _processRules = rules;
            _watchdogRules.Clear();
            _watchdogRules.AddRange(watchdogRules);
            _settings = settings;
        }
    }

    // === Logging ===

    public async Task LogProBalanceActionAsync(ProBalanceAction action)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
            db.ProBalanceActions.Add(action);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log ProBalance action");
        }
    }

    public void ClearProBalanceActions()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        db.ProBalanceActions.ExecuteDelete();
    }

    public IReadOnlyList<ProBalanceAction> GetRecentActions(int count)
    {
        if (count <= 0) return Array.Empty<ProBalanceAction>();
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
            // Ids only grow, so the highest are the newest (and the primary key needs no index of its own).
            var actions = db.ProBalanceActions.AsNoTracking().OrderByDescending(a => a.Id).Take(count).ToList();
            foreach (var action in actions) action.Timestamp = DateTime.SpecifyKind(action.Timestamp, DateTimeKind.Utc);
            return actions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the action log");
            return Array.Empty<ProBalanceAction>();
        }
    }
}
