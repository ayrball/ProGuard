using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// SmartTrim, after Process Lasso's [MemoryManagement]:
/// <list type="bullet">
/// <item>Every <see cref="Interval"/> (SmartTrimIntervalMins, 15), if memory load is at least
/// <see cref="SmartTrimPolicy.MinimumRamLoadPercent"/> (65 %), empties the working set of every process using at
/// least <see cref="ThresholdBytes"/> (MinimumProcessWSSInMb, 196). Never the foreground process, the protected
/// system processes or the configured exclusions.</item>
/// <item>Every <see cref="LowMemoryCheckInterval"/>, if free RAM (free + zero page lists) is below
/// <see cref="SmartTrimPolicy.StandbyFreeRamThresholdBytes"/> (ClearStandbyFreeRAMThresholdMB, 1024), purges the
/// standby list and/or the file cache, whichever is enabled. By default only while Performance Mode is on, which
/// <see cref="PowerManager"/> owns and reports through <see cref="IsPerformanceModeActive"/>.</item>
/// </list>
/// The purges need SeProfileSingleProcessPrivilege and SeIncreaseQuotaPrivilege on the engine's token; SYSTEM holds
/// both, but they start disabled, so SmartTrim enables them and logs clearly when it can't.
/// </summary>
public class SmartTrimService : ISmartTrimService, IDisposable
{
    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan LowMemoryCheckInterval = TimeSpan.FromSeconds(15);
    /// <summary>
    /// Least time between automatic purges. Purging frees the standby pages, so free RAM normally recovers at once; if
    /// it doesn't (the standby list was already empty), purging again every check would only cost CPU.
    /// </summary>
    internal static readonly TimeSpan AutomaticPurgeCooldown = TimeSpan.FromMinutes(1);

    /// <summary>Processes whose working set is never trimmed: the kernel, session and security processes, the compositor and audio.</summary>
    internal static readonly IReadOnlySet<string> ProtectedProcesses = new HashSet<string>(StringComparer.Ordinal)
    {
        "system", "idle", "registry", "memory compression", "secure system", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "lsaiso", "dwm", "audiodg", "fontdrvhost",
    };

    private static readonly string[] Privileges = { MemoryApi.SeProfileSingleProcessPrivilege, MemoryApi.SeIncreaseQuotaPrivilege };

    private readonly ILogger<SmartTrimService> _logger;
    private readonly IProcessMonitor _processMonitor;
    private readonly IMemoryStatus _memory;
    private readonly IMemoryOperations _native;
    private readonly Func<DateTime> _utcNow;

    private readonly object _sync = new();
    // Serializes passes: the two timers and IPC requests may otherwise overlap.
    private readonly object _passSync = new();

    private Timer? _trimTimer;
    private Timer? _lowMemoryTimer;
    private bool _started;
    private bool _enabled = true;
    private TimeSpan _interval = TimeSpan.FromMinutes(15);
    private SmartTrimPolicy _policy = new();
    private HashSet<string> _exclusions = new(StringComparer.Ordinal);
    private DateTime _lastAutomaticPurge = DateTime.MinValue;
    // Privilege -> last error logged, so a missing privilege is reported once rather than on every pass.
    private readonly Dictionary<string, string?> _privilegeErrors = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public long ThresholdBytes { get; set; } = 196L * 1024L * 1024L;

    /// <summary>
    /// Hook for the Performance Mode state, which <see cref="PowerManager"/> owns. The engine wires it at startup;
    /// until then Performance Mode counts as off.
    /// </summary>
    public Func<bool> IsPerformanceModeActive { get; set; } = () => false;

    public SmartTrimService(ILogger<SmartTrimService> logger, IProcessMonitor processMonitor)
        : this(logger, processMonitor, new Win32MemoryStatus(), new Win32MemoryOperations(), null)
    {
    }

    /// <summary>Tests replace the memory readings and actions so the gates can be checked without touching the machine.</summary>
    internal SmartTrimService(ILogger<SmartTrimService>? logger, IProcessMonitor processMonitor,
        IMemoryStatus memory, IMemoryOperations native, Func<DateTime>? utcNow)
    {
        _logger = logger ?? NullLogger<SmartTrimService>.Instance;
        _processMonitor = processMonitor;
        _memory = memory;
        _native = native;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SmartTrim Service");
        // Enable the privileges up front so a missing one shows in the log at startup, not at the first purge.
        lock (_passSync) EnsurePrivileges();
        lock (_sync)
        {
            _started = true;
            Reschedule();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping SmartTrim Service");
        lock (_sync)
        {
            _started = false;
            Reschedule();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public bool IsEnabled
    {
        get { lock (_sync) return _enabled; }
    }

    /// <inheritdoc/>
    public TimeSpan Interval
    {
        get { lock (_sync) return _interval; }
    }

    /// <summary>The options most recently passed to <see cref="ConfigurePolicy"/>.</summary>
    public SmartTrimPolicy Policy
    {
        get { lock (_sync) return _policy; }
    }

    /// <inheritdoc/>
    public void Configure(bool enabled, TimeSpan interval, long thresholdBytes)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _interval = interval < MinInterval ? MinInterval : interval;
            ThresholdBytes = Math.Max(0, thresholdBytes);
            Reschedule();
        }
        _logger.LogInformation("SmartTrim configured: Enabled={Enabled}, Interval={Interval}s, Threshold={Threshold}MB",
            enabled, _interval.TotalSeconds, ThresholdBytes / (1024 * 1024));
    }

    /// <inheritdoc/>
    public void ConfigurePolicy(SmartTrimPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_sync)
        {
            _policy = policy with { MinimumRamLoadPercent = Math.Clamp(policy.MinimumRamLoadPercent, 0, 100) };
            _exclusions = new HashSet<string>(
                policy.Exclusions.Select(ProcessExtensions.NormalizeName).Where(n => n.Length > 0), StringComparer.Ordinal);
            Reschedule();
        }
        _logger.LogInformation(
            "SmartTrim policy: MinRamLoad={Load}%, Exclusions={Exclusions}, StandbyPurge={Standby} (below {Free}MB free, PerformanceModeOnly={PerfOnly}), FileCachePurge={FileCache}, LogNoAction={LogNoAction}",
            _policy.MinimumRamLoadPercent, _exclusions.Count, policy.StandbyPurgeEnabled, policy.StandbyFreeRamThresholdBytes / (1024 * 1024),
            policy.StandbyOnlyInPerformanceMode, policy.FileCachePurgeEnabled, policy.LogNoAction);
    }

    // Caller holds _sync. Each timer only runs while the service is started and its feature is enabled.
    private void Reschedule()
    {
        var trim = _started && _enabled;
        var lowMemory = _started && (_policy.StandbyPurgeEnabled || _policy.FileCachePurgeEnabled);

        if (trim)
        {
            _trimTimer ??= new Timer(_ => Safe("SmartTrim pass", () => RunTrimPass()), null, Timeout.Infinite, Timeout.Infinite);
            _trimTimer.Change(_interval, _interval);
        }
        else _trimTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        if (lowMemory)
        {
            _lowMemoryTimer ??= new Timer(_ => Safe("SmartTrim low-memory check", () => RunLowMemoryCheck()), null, Timeout.Infinite, Timeout.Infinite);
            _lowMemoryTimer.Change(LowMemoryCheckInterval, LowMemoryCheckInterval);
        }
        else _lowMemoryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    // Timer-thread entry point: an exception escaping a timer callback would terminate the engine process.
    private void Safe(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { _logger.LogError(ex, "{What} failed", what); }
    }

    /// <summary>The periodic working-set trim: only acts while memory load is at least the policy's minimum.</summary>
    internal MemoryPurgeReport RunTrimPass()
    {
        _logger.LogDebug("SmartTrim pass due");
        lock (_passSync)
        {
            _logger.LogDebug("SmartTrim pass running");
            var minimum = Policy.MinimumRamLoadPercent;
            var load = _memory.GetMemoryLoadPercent();
            if (load is not int percent)
                return NoAction("SmartTrim", "memory load unavailable");
            if (percent < minimum)
                return NoAction("SmartTrim", $"memory load {percent}% is below {minimum}%");

            var report = new MemoryPurgeReport();
            TrimEligible(report);
            return Finish("SmartTrim", $"memory load {percent}%", report);
        }
    }

    /// <summary>The periodic standby / file cache purge: only acts while free RAM is below the policy's threshold.</summary>
    internal MemoryPurgeReport RunLowMemoryCheck()
    {
        _logger.LogDebug("Low-memory check due");
        lock (_passSync)
        {
            _logger.LogDebug("Low-memory check running");
            var policy = Policy;
            if (!policy.StandbyPurgeEnabled && !policy.FileCachePurgeEnabled)
                return new MemoryPurgeReport();
            if (policy.StandbyOnlyInPerformanceMode && !SafePerformanceModeActive())
                return NoAction("Memory purge", "Performance Mode is off");

            var free = _memory.GetFreeRamBytes();
            if (free is not long freeBytes)
                return NoAction("Memory purge", "free RAM unavailable");
            if (freeBytes >= policy.StandbyFreeRamThresholdBytes)
                return NoAction("Memory purge", $"{MemoryPurgeReport.FormatMb(freeBytes)} free is not below {MemoryPurgeReport.FormatMb(policy.StandbyFreeRamThresholdBytes)}");

            var now = _utcNow();
            if (now - _lastAutomaticPurge < AutomaticPurgeCooldown)
                return NoAction("Memory purge", $"{MemoryPurgeReport.FormatMb(freeBytes)} free, but the last purge was under {AutomaticPurgeCooldown.TotalSeconds:0}s ago");
            _lastAutomaticPurge = now;

            var report = new MemoryPurgeReport();
            Purge(report, policy.StandbyPurgeEnabled, policy.FileCachePurgeEnabled);
            return Finish("Memory purge", $"{MemoryPurgeReport.FormatMb(freeBytes)} free", report);
        }
    }

    /// <inheritdoc/>
    public MemoryPurgeReport PurgeNow()
    {
        lock (_passSync)
        {
            var report = new MemoryPurgeReport();
            TrimEligible(report);
            Purge(report, standby: true, fileCache: Policy.FileCachePurgeEnabled);
            return Finish("Purge now", "requested", report);
        }
    }

    /// <inheritdoc/>
    public MemoryPurgeReport TrimMemory()
    {
        lock (_passSync)
        {
            var report = new MemoryPurgeReport();
            TrimEligible(report);
            return Finish("SmartTrim", "requested", report);
        }
    }

    /// <inheritdoc/>
    public MemoryPurgeReport TrimProcess(int pid)
    {
        var result = _native.TrimWorkingSet(pid);
        var name = _processMonitor.GetProcess(pid)?.Name ?? "process";
        var report = new MemoryPurgeReport();
        if (result.Success)
        {
            report.ProcessesTrimmed = 1;
            report.WorkingSetBytesReclaimed = result.BytesReclaimed;
            _logger.LogInformation("SmartTrim: trimmed {ProcessName} ({Pid}), reclaimed {Reclaimed}", name, pid, MemoryPurgeReport.FormatMb(result.BytesReclaimed));
        }
        else
        {
            report.Errors.Add(result.Error ?? "the working set could not be trimmed");
            _logger.LogDebug("SmartTrim: could not trim {ProcessName} ({Pid}): {Error}", name, pid, result.Error);
        }
        return report;
    }

    /// <summary>Processes a trim pass would touch: at least the threshold, not foreground, not protected or excluded.</summary>
    internal IEnumerable<ProcessInfo> EligibleProcesses()
    {
        HashSet<string> exclusions;
        long threshold;
        lock (_sync)
        {
            exclusions = _exclusions;
            threshold = ThresholdBytes;
        }

        var foregroundPid = SafeForegroundPid();
        var self = Environment.ProcessId;
        return _processMonitor.GetAllProcesses()
            .Where(p => p.WorkingSetBytes >= threshold
                        && !p.IsForeground && p.Pid != foregroundPid
                        && p.Pid > 4 && p.Pid != self)
            .Where(p =>
            {
                var name = ProcessExtensions.NormalizeName(p.Name);
                return !ProtectedProcesses.Contains(name) && !exclusions.Contains(name);
            })
            .ToList();
    }

    private void TrimEligible(MemoryPurgeReport report)
    {
        foreach (var p in EligibleProcesses())
        {
            _logger.LogDebug("SmartTrim: trimming {ProcessName} ({Pid})", p.Name, p.Pid);
            var result = _native.TrimWorkingSet(p.Pid);
            if (!result.Success)
            {
                // Usually a process that exited or that even SYSTEM can't open (protected); not worth more than debug.
                _logger.LogDebug("SmartTrim: could not trim {ProcessName} ({Pid}): {Error}", p.Name, p.Pid, result.Error);
                continue;
            }
            report.ProcessesTrimmed++;
            report.WorkingSetBytesReclaimed += result.BytesReclaimed;
            _logger.LogInformation("SmartTrim: trimmed {ProcessName} ({Pid}) {Before} -> {After}, reclaimed {Reclaimed}",
                p.Name, p.Pid, MemoryPurgeReport.FormatMb(result.BytesBefore), MemoryPurgeReport.FormatMb(result.BytesAfter),
                MemoryPurgeReport.FormatMb(result.BytesReclaimed));
        }
    }

    // Caller holds _passSync.
    private void Purge(MemoryPurgeReport report, bool standby, bool fileCache)
    {
        if (!standby && !fileCache) return;
        EnsurePrivileges();

        if (standby)
        {
            var before = _memory.GetFreeRamBytes();
            _logger.LogDebug("Memory purge: purging the standby list");
            var error = _native.PurgeStandbyList();
            if (error != null) Fail(report, error);
            else
            {
                report.StandbyPurged = true;
                report.StandbyBytesReclaimed = Gain(before, _memory.GetFreeRamBytes());
                _logger.LogInformation("Memory purge: purged standby list, reclaimed {Reclaimed}", MemoryPurgeReport.FormatMb(report.StandbyBytesReclaimed));
            }
        }

        if (fileCache)
        {
            var before = _memory.GetFileCacheBytes();
            _logger.LogDebug("Memory purge: purging the file cache");
            var error = _native.PurgeFileCache();
            if (error != null) Fail(report, error);
            else
            {
                report.FileCachePurged = true;
                report.FileCacheBytesReclaimed = Gain(_memory.GetFileCacheBytes(), before);
                _logger.LogInformation("Memory purge: purged file cache, reclaimed {Reclaimed}", MemoryPurgeReport.FormatMb(report.FileCacheBytesReclaimed));
            }
        }
    }

    private static long Gain(long? from, long? to) => from is long a && to is long b ? Math.Max(0, b - a) : 0;

    private void Fail(MemoryPurgeReport report, string error)
    {
        report.Errors.Add(error);
        _logger.LogWarning("Memory purge: {Error}", error);
    }

    // Caller holds _passSync. Retried before every purge (cheap), but each distinct failure is logged only once.
    private void EnsurePrivileges()
    {
        foreach (var privilege in Privileges)
        {
            string? error;
            try { error = _native.EnablePrivilege(privilege); }
            catch (Exception ex) { error = $"{privilege}: {ex.Message}"; }

            _privilegeErrors.TryGetValue(privilege, out var previous);
            var known = _privilegeErrors.ContainsKey(privilege);
            _privilegeErrors[privilege] = error;
            if (known && previous == error) continue;

            if (error == null) _logger.LogInformation("SmartTrim: enabled {Privilege}", privilege);
            else _logger.LogWarning("SmartTrim: could not enable {Privilege}: {Error}. {Consequence}", privilege, error,
                privilege == MemoryApi.SeProfileSingleProcessPrivilege
                    ? "Standby list purges will fail."
                    : "File cache purges will fail.");
        }
    }

    private MemoryPurgeReport Finish(string what, string context, MemoryPurgeReport report)
    {
        if (report.AnyAction || report.Errors.Count > 0)
            _logger.LogInformation("{What} ({Context}): {Summary}; {Total} reclaimed in total", what, context, report.Summary(), MemoryPurgeReport.FormatMb(report.TotalBytesReclaimed));
        else
            LogNoAction(what, context + ", nothing eligible");
        return report;
    }

    private MemoryPurgeReport NoAction(string what, string reason)
    {
        LogNoAction(what, reason);
        return new MemoryPurgeReport();
    }

    private void LogNoAction(string what, string reason)
    {
        var level = Policy.LogNoAction ? LogLevel.Information : LogLevel.Debug;
        _logger.Log(level, "{What}: no action ({Reason})", what, reason);
    }

    private bool SafePerformanceModeActive()
    {
        try { return IsPerformanceModeActive(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Performance Mode state unavailable");
            return false;
        }
    }

    private int SafeForegroundPid()
    {
        try { return _processMonitor.GetForegroundPid(); }
        catch { return 0; }
    }

    public void Dispose()
    {
        _trimTimer?.Dispose();
        _lowMemoryTimer?.Dispose();
    }
}
