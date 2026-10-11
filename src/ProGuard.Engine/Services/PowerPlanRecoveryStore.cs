using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Data;

namespace ProGuard.Engine.Services;

/// <summary>A plan switch <see cref="PowerManager"/> owes a restore for: a feature put <paramref name="Applied"/> on,
/// and <paramref name="Restore"/> is the user's own plan to go back to.</summary>
internal readonly record struct PendingPlanRestore(Guid Applied, Guid Restore);

/// <summary>
/// Remembers the pending restore across engine runs, so a crash or kill while a feature (IdleSaver, Performance Mode
/// or the load switcher) holds the plan doesn't leave the user stuck on it: the next start restores it (Process
/// Lasso's IdleSaverRecoveryGUID). There is at most one pending restore, since only one feature holds the plan.
/// </summary>
internal interface IPowerPlanRecoveryStore
{
    PendingPlanRestore? Load();
    void Save(PendingPlanRestore pending);
    void Clear();
}

/// <summary>Keeps the pending restore in memory only: for tests, and for a <see cref="PowerManager"/> on a faked plan API.</summary>
internal sealed class MemoryPowerPlanRecoveryStore : IPowerPlanRecoveryStore
{
    public PendingPlanRestore? Pending { get; set; }
    public int Saves { get; private set; }
    public PendingPlanRestore? Load() => Pending;
    public void Save(PendingPlanRestore pending) { Pending = pending; Saves++; }
    public void Clear() => Pending = null;
}

/// <summary>
/// Keeps the pending restore in a small file next to the shared database (%ProgramData%\ProGuard).
/// <para>
/// Not in the database itself. <see cref="ProGuard.Core.Models.AppSettings"/> is the user's configuration: the UI
/// edits and saves it, and the engine re-reads every rule and setting whenever the database file changes, so engine
/// runtime state written on every plan switch doesn't belong there. And the record has to be readable exactly when
/// something has gone wrong: a database the UI has locked, or one left damaged by the same crash, must not cost the
/// user their power plan. ProgramData works from session 0 (unlike a per-user folder), and a file the service creates
/// there is writable only by the service and administrators; the worst a file planted by someone else could do is
/// switch, once at start, away from a plan they could switch away from themselves.
/// </para>
/// </summary>
internal sealed class FilePowerPlanRecoveryStore : IPowerPlanRecoveryStore
{
    private readonly string _path;
    private readonly ILogger _logger;

    public FilePowerPlanRecoveryStore(ILogger? logger = null)
        : this(Path.Combine(Path.GetDirectoryName(ProGuardDbContext.DatabasePath)!, "power-recovery.json"), logger)
    {
    }

    internal FilePowerPlanRecoveryStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The pending restore, or null if there is none or the file can't be read or makes no sense.</summary>
    public PendingPlanRestore? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var pending = JsonSerializer.Deserialize<PendingPlanRestore>(File.ReadAllText(_path));
            if (pending.Applied != Guid.Empty && pending.Restore != Guid.Empty) return pending;
            _logger.LogWarning("Ignoring the power plan recovery record in {Path}: it names no plan", _path);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Can't read the power plan to recover from {Path}, ignoring it", _path);
            return null;
        }
    }

    public void Save(PendingPlanRestore pending)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Write then rename, so a crash mid-write leaves the old file or the new one, never half of one; and write
            // through, so the record survives a power cut as well as an engine crash.
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                JsonSerializer.Serialize(stream, pending);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Can't save the power plan to recover to {Path}", _path);
        }
    }

    public void Clear()
    {
        try { File.Delete(_path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Can't delete {Path}", _path); }
    }
}
