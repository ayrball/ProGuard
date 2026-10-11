using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Shared.Messages;
using ProGuard.UI.IPC;

namespace ProGuard.UI.Services;

/// <summary>Outcome of a save: <see cref="Id"/> is the saved rule's Id, when there is one.</summary>
public sealed record SaveResult(bool Success, string? Error = null, int? Id = null)
{
    public static SaveResult Ok(int? id = null) => new(true, null, id);
    public static SaveResult Fail(string error) => new(false, error);
}

/// <summary>
/// Saves settings and rules. On an installed system the database belongs to the engine service: it is created by
/// SYSTEM, and only SYSTEM and administrators can write it. So changes go to the engine over its pipe, which also
/// decides what a standard user may change. Only when no engine is running (development without the service) does
/// the UI write the database itself.
/// </summary>
public class ConfigWriter
{
    private readonly EngineProxy _engine;
    private readonly Func<ProGuardDbContext> _openDb;
    // Saves read the stored settings to work out what changed, so they must not overlap.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConfigWriter(EngineProxy engine) : this(engine, () => new ProGuardDbContext()) { }

    internal ConfigWriter(EngineProxy engine, Func<ProGuardDbContext> openDb)
    {
        _engine = engine;
        _openDb = openDb;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the stored settings. Only the properties it actually changes are sent, so a
    /// page saving its own settings can't overwrite ones another page (or the engine) changed meanwhile.
    /// </summary>
    public async Task<SaveResult> UpdateSettingsAsync(Action<AppSettings> change)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            AppSettings stored;
            using (var db = _openDb())
            {
                db.EnsureSchema();
                stored = db.Settings.AsNoTracking().FirstOrDefault() ?? new AppSettings { Id = 1 };
            }
            var updated = stored.Clone();
            change(updated);

            var changes = ChangedProperties(stored, updated);
            if (changes.Count == 0) return SaveResult.Ok();

            var result = await SendAsync(() => _engine.UpdateSettingsAsync(JsonSerializer.Serialize(changes))).ConfigureAwait(false);
            return result != null ? From(result) : WriteDirectly(db =>
            {
                var row = db.Settings.FirstOrDefault();
                if (row == null) db.Settings.Add(updated);
                else change(row);
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds <paramref name="rule"/>, or replaces the rule with its Id or, failing that, its process name.</summary>
    public async Task<SaveResult> SaveProcessRuleAsync(ProcessRule rule)
    {
        var result = await SendAsync(() => _engine.SaveProcessRuleAsync(rule)).ConfigureAwait(false);
        if (result != null) return From(result);

        var key = ProcessExtensions.NormalizeName(rule.ProcessName);
        int? id = null;
        var saved = WriteDirectly(db =>
        {
            var existing = (rule.Id != 0 ? db.ProcessRules.Find(rule.Id) : null)
                           ?? db.ProcessRules.AsEnumerable().FirstOrDefault(r => ProcessExtensions.NormalizeName(r.ProcessName) == key);
            if (existing != null)
            {
                rule.Id = existing.Id;
                db.Entry(existing).CurrentValues.SetValues(rule);
            }
            else db.ProcessRules.Add(rule);
        }, () => id = rule.Id);
        return saved.Success ? SaveResult.Ok(id) : saved;
    }

    /// <summary>
    /// Reads the process rule for <paramref name="processName"/> (a new one if there is none), lets
    /// <paramref name="change"/> edit it, and saves it. <paramref name="change"/> returns false to leave things as they are.
    /// </summary>
    public async Task<SaveResult> UpdateProcessRuleAsync(string processName, Func<ProcessRule, bool> change)
    {
        var key = ProcessExtensions.NormalizeName(processName);
        ProcessRule rule;
        using (var db = _openDb())
        {
            db.EnsureSchema();
            rule = db.ProcessRules.AsNoTracking().AsEnumerable().FirstOrDefault(r => ProcessExtensions.NormalizeName(r.ProcessName) == key)
                   ?? new ProcessRule { ProcessName = processName };
        }
        return change(rule) ? await SaveProcessRuleAsync(rule).ConfigureAwait(false) : SaveResult.Ok(rule.Id);
    }

    public async Task<SaveResult> DeleteProcessRuleAsync(int id)
    {
        var result = await SendAsync(() => _engine.DeleteProcessRuleAsync(id)).ConfigureAwait(false);
        return result != null ? From(result) : WriteDirectly(db =>
        {
            var rule = db.ProcessRules.Find(id);
            if (rule != null) db.ProcessRules.Remove(rule);
        });
    }

    /// <summary>Adds (Id 0) or replaces a Watchdog rule.</summary>
    public async Task<SaveResult> SaveWatchdogRuleAsync(WatchdogRule rule)
    {
        var result = await SendAsync(() => _engine.SaveWatchdogRuleAsync(rule)).ConfigureAwait(false);
        if (result != null) return From(result);

        int? id = null;
        var saved = WriteDirectly(db =>
        {
            var existing = rule.Id != 0 ? db.WatchdogRules.Find(rule.Id) : null;
            if (existing != null) db.Entry(existing).CurrentValues.SetValues(rule);
            else db.WatchdogRules.Add(rule);
        }, () => id = rule.Id);
        return saved.Success ? SaveResult.Ok(id) : saved;
    }

    public async Task<SaveResult> DeleteWatchdogRuleAsync(int id)
    {
        var result = await SendAsync(() => _engine.DeleteWatchdogRuleAsync(id)).ConfigureAwait(false);
        return result != null ? From(result) : WriteDirectly(db =>
        {
            var rule = db.WatchdogRules.Find(id);
            if (rule != null) db.WatchdogRules.Remove(rule);
        });
    }

    public async Task<SaveResult> ClearActionLogAsync()
    {
        var result = await SendAsync(() => _engine.ClearActionLogAsync()).ConfigureAwait(false);
        return result != null ? From(result) : WriteDirectly(db => db.ProBalanceActions.RemoveRange(db.ProBalanceActions));
    }

    /// <summary>The engine's reply, or null when no engine is running (skipping the connection timeout when its pipe doesn't exist).</summary>
    private Task<CommandResult?> SendAsync(Func<Task<CommandResult?>> send) =>
        _engine.IsConnected || _engine.IsEngineListening ? send() : Task.FromResult<CommandResult?>(null);

    private static SaveResult From(CommandResult result)
    {
        if (!result.Success) return SaveResult.Fail(result.Error ?? "The engine refused the change.");
        return SaveResult.Ok(int.TryParse(result.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null);
    }

    private SaveResult WriteDirectly(Action<ProGuardDbContext> write, Action? afterSave = null)
    {
        try
        {
            using var db = _openDb();
            db.EnsureSchema();
            write(db);
            db.SaveChanges();
            afterSave?.Invoke();
            return SaveResult.Ok();
        }
        catch (Exception ex)
        {
            return SaveResult.Fail("The ProGuard engine service isn't running, and the change couldn't be saved directly: "
                                   + (ex.InnerException ?? ex).Message + ". Start the service, or run ProGuard as administrator.");
        }
    }

    /// <summary>Property name -> new value for every writable setting <paramref name="updated"/> changes.</summary>
    internal static Dictionary<string, object?> ChangedProperties(AppSettings stored, AppSettings updated)
    {
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.CanWrite && p.Name != nameof(AppSettings.Id)))
        {
            var after = property.GetValue(updated);
            if (!Equals(property.GetValue(stored), after)) changes[property.Name] = after;
        }
        return changes;
    }
}
