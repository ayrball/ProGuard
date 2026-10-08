using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// SQLite-backed rule store with in-memory caching for fast lookups.
/// </summary>
public class RuleStore : IRuleStore
{
    private readonly ILogger<RuleStore> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, ProcessRule> _processRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WatchdogRule> _watchdogRules = new();
    private readonly List<PowerProfile> _powerProfiles = new();
    private AppSettings _settings = new();

    public RuleStore(ILogger<RuleStore> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        LoadRules();
    }
    
    private void LoadRules()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
            db.Database.EnsureCreated();
            
            foreach (var rule in db.ProcessRules.ToList())
            {
                _processRules[rule.ProcessName] = rule;
            }
            
            _watchdogRules.Clear();
            _watchdogRules.AddRange(db.WatchdogRules.ToList());

            _powerProfiles.Clear();
            _powerProfiles.AddRange(db.PowerProfiles.ToList());

            _settings = db.Settings.FirstOrDefault() ?? new AppSettings();
            
            _logger.LogInformation("Loaded {RuleCount} process rules, {WdCount} watchdog rules, {PpCount} power profiles", 
                _processRules.Count, _watchdogRules.Count, _powerProfiles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading rules from DB");
        }
    }

    // === Process Rules ===

    public ProcessRule? GetProcessRule(string processName)
    {
        return _processRules.TryGetValue(processName, out var rule) ? rule : null;
    }

    public IEnumerable<ProcessRule> GetProcessRules() => _processRules.Values;
    public IEnumerable<ProcessRule> GetAllProcessRules() => _processRules.Values;

    public void AddProcessRule(ProcessRule rule) => SaveProcessRule(rule);

    public void SaveProcessRule(ProcessRule rule)
    {
        _processRules[rule.ProcessName] = rule;
        
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        
        var existing = db.ProcessRules.FirstOrDefault(r => r.ProcessName == rule.ProcessName);
        if (existing != null)
        {
            db.Entry(existing).CurrentValues.SetValues(rule);
        }
        else
        {
            db.ProcessRules.Add(rule);
        }
        db.SaveChanges();
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
            _processRules.TryRemove(rule.ProcessName, out _);
        }
    }

    // === Watchdog Rules ===

    public IEnumerable<WatchdogRule> GetWatchdogRules() => _watchdogRules;

    public void AddWatchdogRule(WatchdogRule rule)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        db.WatchdogRules.Add(rule);
        db.SaveChanges();
        _watchdogRules.Add(rule);
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
            var idx = _watchdogRules.FindIndex(r => r.Id == rule.Id);
            if (idx >= 0) _watchdogRules[idx] = rule;
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
            _watchdogRules.RemoveAll(r => r.Id == id);
        }
    }

    // === Power Profiles ===

    public IEnumerable<PowerProfile> GetPowerProfiles() => _powerProfiles;

    public void AddPowerProfile(PowerProfile profile)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        db.PowerProfiles.Add(profile);
        db.SaveChanges();
        _powerProfiles.Add(profile);
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
            var idx = _powerProfiles.FindIndex(p => p.Id == profile.Id);
            if (idx >= 0) _powerProfiles[idx] = profile;
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
            _powerProfiles.RemoveAll(p => p.Id == id);
        }
    }

    // === Settings ===

    public AppSettings GetSettings() => _settings;

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProGuardDbContext>();
        var existing = db.Settings.FirstOrDefault();
        if (existing != null)
        {
            db.Entry(existing).CurrentValues.SetValues(settings);
        }
        else
        {
            db.Settings.Add(settings);
        }
        db.SaveChanges();
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
}
