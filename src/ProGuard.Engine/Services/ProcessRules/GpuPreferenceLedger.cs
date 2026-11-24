using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary>
/// One graphics preference ProGuard wrote to a user's <c>UserGpuPreferences</c>: who, which program, what it wrote, and
/// the preference that was there before (null if none), to put back when the rule goes.
/// </summary>
public sealed record GpuPreferenceEntry(string Sid, string ExePath, int Written, int? Previous);

/// <summary>
/// Remembers the graphics preferences ProGuard wrote. Unlike Image File Execution Options, a <c>UserGpuPreferences</c>
/// value has nowhere to mark it as ProGuard's (Settings &gt; Display &gt; Graphics writes the very same value), so the
/// engine keeps this list instead, to remove only its own when a rule goes or ProGuard is uninstalled.
/// </summary>
public interface IGpuPreferenceLedger
{
    IReadOnlyList<GpuPreferenceEntry> All();

    /// <summary>Records a write. The first write for a user and program keeps the preference found before it.</summary>
    void Record(string sid, string exePath, int written, int? found);

    void Forget(GpuPreferenceEntry entry);
}

/// <summary>The list in <c>gpu-preferences.json</c> in the engine's data folder (SYSTEM and administrators only).</summary>
public sealed class FileGpuPreferenceLedger : IGpuPreferenceLedger
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private List<GpuPreferenceEntry>? _entries;

    public FileGpuPreferenceLedger(ILogger? logger = null)
        : this(Path.Combine(ProGuardDbContext.DataDirectory, "gpu-preferences.json"), logger)
    {
    }

    internal FileGpuPreferenceLedger(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<GpuPreferenceEntry> All()
    {
        lock (_sync) return Entries().ToList();
    }

    public void Record(string sid, string exePath, int written, int? found)
    {
        lock (_sync)
        {
            var entries = Entries();
            var index = entries.FindIndex(e => Same(e, sid, exePath));
            if (index >= 0)
            {
                if (entries[index].Written == written) return;
                entries[index] = entries[index] with { Written = written };
            }
            else entries.Add(new GpuPreferenceEntry(sid, exePath, written, found));
            Save(entries);
        }
    }

    public void Forget(GpuPreferenceEntry entry)
    {
        lock (_sync)
        {
            var entries = Entries();
            if (entries.RemoveAll(e => Same(e, entry.Sid, entry.ExePath)) > 0) Save(entries);
        }
    }

    private static bool Same(GpuPreferenceEntry e, string sid, string exePath) =>
        string.Equals(e.Sid, sid, StringComparison.OrdinalIgnoreCase) && string.Equals(e.ExePath, exePath, StringComparison.OrdinalIgnoreCase);

    private List<GpuPreferenceEntry> Entries()
    {
        if (_entries != null) return _entries;
        try
        {
            _entries = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<GpuPreferenceEntry>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Can't read the graphics preferences ProGuard wrote from {Path}", _path);
            _entries = new();
        }
        return _entries;
    }

    private void Save(List<GpuPreferenceEntry> entries)
    {
        try
        {
            if (entries.Count == 0)
            {
                File.Delete(_path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Can't save the graphics preferences ProGuard wrote to {Path}", _path);
        }
    }
}

/// <summary>Reads and writes <c>&lt;user&gt;\Software\Microsoft\DirectX\UserGpuPreferences</c> under a root (HKEY_USERS; a test key in tests).</summary>
public sealed class UserGpuPreferences
{
    private readonly RegistryKey _users;

    public UserGpuPreferences(RegistryKey? users = null) => _users = users ?? Registry.Users;

    /// <summary>Whether the user's hive is loaded at all; nothing is ever written to one that isn't.</summary>
    public bool IsLoaded(string sid)
    {
        using var hive = _users.OpenSubKey(sid);
        return hive != null;
    }

    public string? Read(string sid, string exePath)
    {
        using var key = _users.OpenSubKey($@"{sid}\{Win32ProcessTuning.UserGpuPreferencesKey}");
        return key?.GetValue(exePath) as string;
    }

    /// <summary>Writes <paramref name="value"/>, or deletes the value when it is null.</summary>
    public void Write(string sid, string exePath, string? value)
    {
        using var key = _users.OpenSubKey($@"{sid}\{Win32ProcessTuning.UserGpuPreferencesKey}", writable: true);
        if (key == null) return;
        if (value == null) key.DeleteValue(exePath, throwOnMissingValue: false);
        else key.SetValue(exePath, value, RegistryValueKind.String);
    }
}

/// <summary>
/// Takes ProGuard's graphics preferences back out of users' registries: when no rule asks for one any more
/// (<see cref="Sync"/>, called where the launch priorities are kept in step, so deleting, renaming or clearing a rule,
/// a profile switch and a database changed on disk are all covered), and at uninstall (<see cref="RemoveAll"/>).
/// A value is only changed while it still holds what ProGuard wrote: one the user has since changed in Settings is theirs.
/// Then the preference found before ProGuard's is put back, or the GpuPreference entry removed (and the value with it if
/// nothing else is left in it).
/// </summary>
public sealed class GpuPreferenceService
{
    private readonly ILogger _logger;
    private readonly IGpuPreferenceLedger _ledger;
    private readonly UserGpuPreferences _registry;
    private readonly object _sync = new();

    public GpuPreferenceService(IGpuPreferenceLedger ledger, UserGpuPreferences? registry = null, ILogger? logger = null)
    {
        _ledger = ledger;
        _registry = registry ?? new UserGpuPreferences();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Reverts every recorded preference whose program no rule gives a GPU preference any more. Returns how many it reverted.</summary>
    public int Sync(IEnumerable<ProcessRule> rules)
    {
        var wanted = rules.Where(r => r.GpuPreference != null && !r.DisallowExecution)
            .Select(r => ProcessExtensions.NormalizeName(r.ProcessName)).ToHashSet(StringComparer.Ordinal);
        lock (_sync)
            return _ledger.All().Where(e => !wanted.Contains(ProcessExtensions.NormalizeName(Path.GetFileName(e.ExePath)))).Count(Revert);
    }

    /// <summary>Reverts every recorded preference whose user's hive is loaded. Returns how many it reverted.</summary>
    public int RemoveAll()
    {
        lock (_sync) return _ledger.All().Count(Revert);
    }

    private bool Revert(GpuPreferenceEntry entry)
    {
        try
        {
            if (!_registry.IsLoaded(entry.Sid))
            {
                // Kept for later: the user isn't signed in, and their hive can only be changed while it is loaded.
                _logger.LogInformation("The graphics preference for {Exe} stays in the registry of {Sid} until they next sign in", entry.ExePath, entry.Sid);
                return false;
            }

            var current = _registry.Read(entry.Sid, entry.ExePath);
            if (GpuPreferenceApplier.TryParse(current, out var now) && now == entry.Written)
            {
                var restored = entry.Previous is int previous ? GpuPreferenceApplier.Merge(current, previous) : GpuPreferenceApplier.Strip(current);
                _registry.Write(entry.Sid, entry.ExePath, restored);
                _logger.LogInformation("Removed ProGuard's graphics preference for {Exe} from the registry of {Sid}", entry.ExePath, entry.Sid);
            }
            _ledger.Forget(entry);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Can't remove the graphics preference for {Exe} from the registry of {Sid}", entry.ExePath, entry.Sid);
            return false;
        }
    }
}
