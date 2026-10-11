using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>A processor power setting's AC (plugged in) and DC (on battery) values.</summary>
public readonly record struct AcDc(uint Ac, uint Dc);

/// <summary>The power-plan calls <see cref="CoreParkingService"/> makes. Tests replace them.</summary>
public interface ICoreParkingPower
{
    Guid ActiveScheme();

    /// <summary>The setting's values in <paramref name="scheme"/>, or null if the plan has no such setting.</summary>
    AcDc? Read(Guid scheme, Guid setting);

    bool Write(Guid scheme, Guid setting, AcDc values);

    /// <summary>Makes Windows pick up changed values of the active plan (re-applying it, as powercfg /setactive does).</summary>
    void Reapply(Guid scheme);
}

/// <summary>The settings a restraint changed, and what to put back.</summary>
public sealed record CoreParkingRecord(Guid Scheme, Dictionary<Guid, AcDc> Originals);

/// <summary>Keeps the record across engine runs, so a crash while cores are held unparked doesn't leave them so.</summary>
public interface ICoreParkingRecoveryStore
{
    CoreParkingRecord? Load();
    void Save(CoreParkingRecord record);
    void Clear();
}

public sealed class MemoryCoreParkingRecoveryStore : ICoreParkingRecoveryStore
{
    public CoreParkingRecord? Record { get; set; }
    public CoreParkingRecord? Load() => Record;
    public void Save(CoreParkingRecord record) => Record = record;
    public void Clear() => Record = null;
}

/// <summary>
/// The record in %ProgramData%\ProGuard\core-parking-recovery.json, next to <c>power-recovery.json</c> and for the same
/// reasons (see <see cref="FilePowerPlanRecoveryStore"/>): readable when the database isn't, written through, replaced
/// atomically.
/// </summary>
public sealed class FileCoreParkingRecoveryStore : ICoreParkingRecoveryStore
{
    private readonly string _path;
    private readonly ILogger _logger;

    public FileCoreParkingRecoveryStore(ILogger? logger = null)
        : this(Path.Combine(Path.GetDirectoryName(ProGuardDbContext.DatabasePath)!, "core-parking-recovery.json"), logger)
    {
    }

    public FileCoreParkingRecoveryStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    public CoreParkingRecord? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var record = JsonSerializer.Deserialize<CoreParkingRecord>(File.ReadAllText(_path));
            if (record != null && record.Scheme != Guid.Empty && record.Originals is { Count: > 0 }) return record;
            _logger.LogWarning("Ignoring the core parking recovery record in {Path}: it names no setting", _path);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Can't read the core parking settings to recover from {Path}, ignoring them", _path);
            return null;
        }
    }

    public void Save(CoreParkingRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                JsonSerializer.Serialize(stream, record);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Can't save the core parking settings to recover to {Path}", _path);
        }
    }

    public void Clear()
    {
        try { File.Delete(_path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Can't delete {Path}", _path); }
    }
}

public sealed class Win32CoreParkingPower : ICoreParkingPower
{
    public Guid ActiveScheme()
    {
        if (Powrprof.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return Guid.Empty;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public AcDc? Read(Guid scheme, Guid setting)
    {
        var subgroup = Powrprof.ProcessorSettingsSubgroup;
        if (Powrprof.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var ac) != 0) return null;
        if (Powrprof.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var dc) != 0) return null;
        return new AcDc(ac, dc);
    }

    public bool Write(Guid scheme, Guid setting, AcDc values)
    {
        var subgroup = Powrprof.ProcessorSettingsSubgroup;
        return Powrprof.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, values.Ac) == 0
               & Powrprof.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, values.Dc) == 0;
    }

    public void Reapply(Guid scheme) => Powrprof.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
}

/// <summary>
/// Process Lasso's <c>OocDisableCoreParkingWhileIn</c>: while ProBalance holds any process restrained, the active power
/// plan's "processor performance core parking min cores" (<c>CPMINCORES</c>, and <c>CPMINCORES1</c> for the second
/// efficiency class of a hybrid CPU) is set to 100 %, so every core stays unparked and the foreground work isn't
/// squeezed onto a few. The plan's own values come back once nothing is restrained.
/// <para>
/// Only values still at 100 % are put back: if the user (or a tool) changed one meanwhile, theirs stays. If the active
/// plan changes while cores are held unparked (Performance Mode, IdleSaver, the user), the old plan gets its values back
/// and the new one is unparked instead.
/// </para>
/// <para>
/// <b>Crash recovery.</b> As with <see cref="PowerManager"/>'s plan restore, the original values are written to
/// <see cref="ICoreParkingRecoveryStore"/> before the plan is changed and cleared once they are back, and
/// <see cref="RecoverFromLastRun"/> puts back any left over at the next start.
/// </para>
/// </summary>
public sealed class CoreParkingService
{
    /// <summary>GUID_PROCESSOR_CORE_PARKING_MIN_CORES, and its twin for efficiency class 1 on hybrid CPUs.</summary>
    public static readonly Guid MinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");
    public static readonly Guid MinCores1 = new("0cc5b647-c1df-4637-891a-dec35c318584");
    private static readonly Guid[] Settings = { MinCores, MinCores1 };
    private const uint AllCores = 100;

    private readonly ILogger<CoreParkingService> _logger;
    private readonly ICoreParkingPower _power;
    private readonly ICoreParkingRecoveryStore _recovery;
    private readonly IRuleStore? _ruleStore;
    private readonly object _sync = new();
    private bool _enabled;
    private bool _engaged;
    private CoreParkingRecord? _held;

    public CoreParkingService(ILogger<CoreParkingService>? logger = null, IRuleStore? ruleStore = null,
        ICoreParkingPower? power = null, ICoreParkingRecoveryStore? recovery = null)
    {
        _logger = logger ?? NullLogger<CoreParkingService>.Instance;
        _ruleStore = ruleStore;
        _power = power ?? new Win32CoreParkingPower();
        // A faked power API never leaves a record on the real disk.
        _recovery = recovery ?? (power == null ? new FileCoreParkingRecoveryStore(_logger) : new MemoryCoreParkingRecoveryStore());
    }

    public bool IsEnabled
    {
        get { lock (_sync) return _enabled; }
    }

    /// <summary>The plan whose cores are held unparked now, or null.</summary>
    public Guid? HeldScheme
    {
        get { lock (_sync) return _held?.Scheme; }
    }

    public void Configure(bool enabled)
    {
        lock (_sync)
        {
            if (enabled == _enabled) return;
            _enabled = enabled;
            _logger.LogInformation("Unparking cores while ProBalance restrains: {State}", enabled ? "on" : "off");
            if (!enabled) RestoreLocked("the option was turned off");
            else if (_engaged) ApplyLocked();
        }
    }

    /// <summary>ProBalance's state after each of its passes: whether it holds any process restrained.</summary>
    public void Update(bool anyRestrained)
    {
        lock (_sync)
        {
            _engaged = anyRestrained;
            if (anyRestrained && _enabled) ApplyLocked();
            else if (!anyRestrained) RestoreLocked("no process is restrained any more");
        }
    }

    /// <summary>Puts the plan's values back. Run when the engine stops.</summary>
    public void Stop()
    {
        lock (_sync) RestoreLocked("the engine is stopping");
    }

    /// <summary>
    /// Puts back values a previous run changed and never restored (it crashed or was killed). Run at startup, before
    /// ProBalance starts.
    /// </summary>
    public void RecoverFromLastRun()
    {
        lock (_sync)
        {
            if (_recovery.Load() is not { } record) return;
            _logger.LogWarning("The engine last stopped while it held the cores of {Plan} unparked; putting its settings back", PowerPlans.Describe(record.Scheme));
            PutBack(record, "the engine last stopped while ProBalance was restraining");
            _recovery.Clear();
        }
    }

    private void ApplyLocked()
    {
        var active = _power.ActiveScheme();
        if (active == Guid.Empty) return;
        if (_held?.Scheme == active) return;
        // The plan changed under us: give the old plan its values back, then follow the new one.
        if (_held != null) RestoreLocked("the power plan changed");

        var originals = new Dictionary<Guid, AcDc>();
        foreach (var setting in Settings)
            if (_power.Read(active, setting) is { } values) originals[setting] = values;
        if (originals.Count == 0)
        {
            _logger.LogDebug("The active power plan has no core parking settings; nothing to unpark");
            return;
        }
        if (originals.Values.All(v => v.Ac == AllCores && v.Dc == AllCores)) return; // already unparked by the plan itself

        var record = new CoreParkingRecord(active, originals);
        // Recorded before the change, so there is no moment the engine could die with the plan changed and no record.
        _recovery.Save(record);
        _held = record;
        foreach (var setting in originals.Keys) _power.Write(active, setting, new AcDc(AllCores, AllCores));
        _power.Reapply(active);
        Log($"Core parking off on {PowerPlans.Describe(active)} (min cores {Describe(originals[MinCores0(originals)])} -> 100%) while ProBalance restrains");
    }

    private void RestoreLocked(string reason)
    {
        if (_held is not { } record) return;
        _held = null;
        PutBack(record, reason);
        _recovery.Clear();
    }

    /// <summary>Writes the originals back over values still at 100 %; one changed by someone else meanwhile stays.</summary>
    private void PutBack(CoreParkingRecord record, string reason)
    {
        bool any = false;
        foreach (var (setting, original) in record.Originals)
        {
            if (_power.Read(record.Scheme, setting) is not { } now) continue;
            var restored = new AcDc(now.Ac == AllCores ? original.Ac : now.Ac, now.Dc == AllCores ? original.Dc : now.Dc);
            if (restored == now) continue;
            _power.Write(record.Scheme, setting, restored);
            any = true;
        }
        if (_power.ActiveScheme() == record.Scheme) _power.Reapply(record.Scheme);
        if (any) Log($"Core parking restored on {PowerPlans.Describe(record.Scheme)}: {reason}");
        else _logger.LogInformation("Core parking settings of {Plan} were changed meanwhile; leaving them ({Reason})", PowerPlans.Describe(record.Scheme), reason);
    }

    private static Guid MinCores0(Dictionary<Guid, AcDc> originals) => originals.ContainsKey(MinCores) ? MinCores : originals.Keys.First();

    private static string Describe(AcDc v) => v.Ac == v.Dc ? $"{v.Ac}%" : $"{v.Ac}% plugged in, {v.Dc}% on battery";

    private void Log(string message)
    {
        _logger.LogInformation("{Message}", message);
        // Shown on the Logs page with the power plan changes.
        _ = _ruleStore?.LogProBalanceActionAsync(new ProBalanceAction
        {
            Timestamp = DateTime.UtcNow,
            ProcessName = ProBalanceAction.PowerPlanEntryName,
            Reason = message,
            IsRestored = true,
        });
    }
}
