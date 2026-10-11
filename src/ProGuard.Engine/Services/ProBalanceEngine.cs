using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Models;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// ProBalance - temporarily lowers the priority of background processes that hog the CPU, following
/// Process Lasso's algorithm (see docs/process-lasso-internals.md):
/// <list type="bullet">
/// <item>Restrain a process once it has used at least <see cref="PerProcessUsageBeforeRestraint"/> % of total CPU
/// for <see cref="TimeOverQuotaBeforeRestraint"/> ms, while the system is at least
/// <see cref="TotalProcessorUsageBeforeRestraint"/> % busy.</item>
/// <item>Restore it once it drops to <see cref="PerProcessUsageForRestore"/> % and has been restrained for at least
/// <see cref="MinimumTimeOfRestraint"/> ms, or after <see cref="MaximumTimeOfRestraint"/> ms if that is set.</item>
/// <item>Restraint ends at once if the process comes to the foreground, and is abandoned without restoring if
/// something else (usually the user) changes the priority meanwhile.</item>
/// </list>
/// Property names mirror the <c>[OutOfControlProcessRestraint]</c> keys in prolasso.ini.
/// </summary>
public class ProBalanceEngine : IProBalanceEngine, IDisposable
{
    private readonly ILogger<ProBalanceEngine> _logger;
    private readonly IProcessMonitor _processMonitor;
    private readonly IRuleStore _ruleStore;
    private readonly IProcessPriorityAccess _access;
    private readonly Func<double> _systemCpuPercent;
    private readonly Func<long> _nowMs;
    private Timer? _timer;

    // Evaluate runs on a timer thread; SetEnabled, ReleaseRestraint and StopAsync come from other threads.
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, Restraint> _restrained = new();
    // PID -> time (ms) the process was first seen over quota in the current unbroken run.
    private readonly Dictionary<int, long> _overQuotaSince = new();
    // Normalised names (see ProcessExtensions.NormalizeName). Mutated from IPC/UI threads while Evaluate reads.
    private readonly ConcurrentDictionary<string, byte> _exclusions = new();
    private volatile bool _enabled = true;

    private sealed record Restraint(string Name, ProcessPriorityClass Original, ProcessPriorityClass Applied, long SinceMs);

    /// <summary>
    /// Never restrained: lowering these hurts the whole machine (compositor, audio, session/security
    /// processes) or is pointless/denied, and ProGuard must not throttle itself.
    /// </summary>
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.Ordinal)
    {
        "system", "idle", "registry", "memory compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "dwm", "audiodg", "fontdrvhost", "proguard.engine", "proguard.ui"
    };

    /// <summary>System-wide CPU % that must be reached before anything is restrained.</summary>
    public double TotalProcessorUsageBeforeRestraint { get; set; } = 10.0;
    /// <summary>A process must use at least this % of total CPU (all cores)...</summary>
    public double PerProcessUsageBeforeRestraint { get; set; } = 5.0;
    /// <summary>...continuously for this many ms before it is restrained.</summary>
    public int TimeOverQuotaBeforeRestraint { get; set; } = 900;
    /// <summary>Restore once the process uses no more than this % of total CPU.</summary>
    public double PerProcessUsageForRestore { get; set; } = 2.0;
    /// <summary>A restraint lasts at least this many ms, whatever the usage does.</summary>
    public int MinimumTimeOfRestraint { get; set; } = 4200;
    /// <summary>Force a restore after this many ms (the process may be restrained again later). 0 = no limit.</summary>
    public int MaximumTimeOfRestraint { get; set; }
    /// <summary>Only restrain processes currently at Normal priority class.</summary>
    public bool TameOnlyNormal { get; set; } = true;
    /// <summary>Restrain to Idle instead of BelowNormal.</summary>
    public bool LowerToIdleInsteadOfBelowNormal { get; set; }
    /// <summary>The foreground process is exempt, and a restrained process that comes to the foreground is restored
    /// (prolasso.ini: <c>ExcludeForegroundProcesses2</c>).</summary>
    public bool ExcludeForegroundProcesses { get; set; } = true;
    /// <summary>Direct children (by parent PID) of the foreground process are exempt.</summary>
    public bool ExcludeChildrenOfForeground { get; set; } = true;
    /// <summary>Processes in session 0 (services) are exempt.</summary>
    public bool ExcludeServices { get; set; } = true;
    /// <summary>How often usage is evaluated (prolasso.ini: <c>UpdateSpeedCore</c>). Applies on the next start.</summary>
    public int UpdateSpeedCore { get; set; } = 1000;

    public bool IsEngaged => !_restrained.IsEmpty;
    public int ActiveInterventions => _restrained.Count;

    public ProBalanceEngine(
        ILogger<ProBalanceEngine> logger,
        IProcessMonitor processMonitor,
        IRuleStore ruleStore,
        Func<double>? systemCpuPercent = null,
        IProcessPriorityAccess? processAccess = null,
        Func<long>? clockMs = null)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _ruleStore = ruleStore;
        _systemCpuPercent = systemCpuPercent ?? Core.Helpers.SystemInfo.GetOverallCpuPercent;
        _access = processAccess ?? new Win32ProcessPriorityAccess();
        _nowMs = clockMs ?? (() => Environment.TickCount64);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting ProBalance Engine");
        _timer = new Timer(Evaluate, null, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(Math.Max(100, UpdateSpeedCore)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping ProBalance Engine");
        _timer?.Change(Timeout.Infinite, 0);
        RestoreAll("ProBalance stopped");
        return Task.CompletedTask;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        // Evaluate() stops running when disabled, so nothing else would ever lift the restraints.
        if (!enabled) RestoreAll("ProBalance disabled");
    }

    /// <summary>
    /// 1 (least aggressive) .. 10 (most). Level 5 is Process Lasso's defaults; higher levels restrain at lower
    /// usage and sooner.
    /// </summary>
    public void SetSensitivity(int level)
    {
        level = Math.Clamp(level, 1, 10);
        TotalProcessorUsageBeforeRestraint = 15.0 - level;          // 14 .. 5
        PerProcessUsageBeforeRestraint = 7.5 - 0.5 * level;         // 7 .. 2.5
        TimeOverQuotaBeforeRestraint = 1500 - 120 * level;          // 1380 .. 300 ms
        PerProcessUsageForRestore = Math.Min(2.0, PerProcessUsageBeforeRestraint / 2);
    }

    public void ExcludeProcess(string name) => _exclusions[Core.Helpers.ProcessExtensions.NormalizeName(name)] = 0;
    public void RemoveExclusion(string name) => _exclusions.TryRemove(Core.Helpers.ProcessExtensions.NormalizeName(name), out _);

    public bool ReleaseRestraint(int pid)
    {
        lock (_gate)
        {
            if (!_restrained.ContainsKey(pid)) return false;
            Restore(pid, "Came to the foreground");
            return true;
        }
    }

    internal void Evaluate(object? state)
    {
        if (!_enabled) return;
        try
        {
            lock (_gate)
            {
                if (!_enabled) return;
                var now = _nowMs();
                var processes = _processMonitor.GetAllProcesses().ToList();
                var byPid = new Dictionary<int, ProcessInfo>(processes.Count);
                foreach (var p in processes) byPid[p.Pid] = p;

                var foregroundPid = ResolveForegroundPid(processes);
                UpdateRestraints(byPid, foregroundPid, now);
                RestrainHogs(processes, foregroundPid, now);

                foreach (var pid in _overQuotaSince.Keys.Where(pid => !byPid.ContainsKey(pid)).ToList())
                    _overQuotaSince.Remove(pid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ProBalance evaluation loop");
        }
    }

    private int ResolveForegroundPid(List<ProcessInfo> processes)
    {
        var pid = _processMonitor.GetForegroundPid();
        if (pid > 0) return pid;
        return processes.FirstOrDefault(p => p.IsForeground)?.Pid ?? 0;
    }

    /// <summary>Decides, for every process currently restrained, whether the restraint should end.</summary>
    private void UpdateRestraints(Dictionary<int, ProcessInfo> byPid, int foregroundPid, long now)
    {
        foreach (var (pid, r) in _restrained.ToList())
        {
            // Exited: forget it, never write the old priority onto a recycled PID.
            if (!byPid.TryGetValue(pid, out var info))
            {
                _restrained.TryRemove(pid, out _);
                _logger.LogInformation("ProBalance restraint of {Name} ({Pid}) ended: process exited", r.Name, pid);
                continue;
            }

            var current = _access.GetPriorityClass(pid);
            if (current == null)
            {
                _restrained.TryRemove(pid, out _);
                continue;
            }
            if (current != r.Applied)
            {
                // Someone else (usually the user) set a priority while we held it down; theirs wins.
                _restrained.TryRemove(pid, out _);
                _logger.LogInformation("ProBalance restraint of {Name} ({Pid}) ended: priority changed externally to {Priority}",
                    r.Name, pid, current);
                continue;
            }

            var restrainedFor = now - r.SinceMs;
            if (ExcludeForegroundProcesses && (pid == foregroundPid || info.IsForeground))
                Restore(pid, "Came to the foreground");
            else if (MaximumTimeOfRestraint > 0 && restrainedFor >= MaximumTimeOfRestraint)
                Restore(pid, $"Maximum restraint time ({MaximumTimeOfRestraint} ms) reached");
            else if (restrainedFor >= MinimumTimeOfRestraint && info.CpuPercent <= PerProcessUsageForRestore)
                Restore(pid, $"CPU usage dropped to {info.CpuPercent:F1}%");
        }
    }

    private void RestrainHogs(List<ProcessInfo> processes, int foregroundPid, long now)
    {
        var systemBusy = _systemCpuPercent() >= TotalProcessorUsageBeforeRestraint;

        foreach (var p in processes)
        {
            if (_restrained.ContainsKey(p.Pid)) continue;

            // The over-quota run must be unbroken: any sample that doesn't qualify starts it over.
            if (!systemBusy || p.CpuPercent < PerProcessUsageBeforeRestraint || IsExempt(p, foregroundPid))
            {
                _overQuotaSince.Remove(p.Pid);
                continue;
            }

            if (!_overQuotaSince.TryGetValue(p.Pid, out var since))
                _overQuotaSince[p.Pid] = since = now;

            if (now - since >= TimeOverQuotaBeforeRestraint)
            {
                _overQuotaSince.Remove(p.Pid);
                TryRestrain(p, now);
            }
        }
    }

    /// <summary>Cheap checks first; the parent/session lookups only run for processes that are over quota.</summary>
    private bool IsExempt(ProcessInfo p, int foregroundPid)
    {
        if (p.Pid <= 4) return true;
        var name = Core.Helpers.ProcessExtensions.NormalizeName(p.Name);
        if (ProtectedProcesses.Contains(name) || _exclusions.ContainsKey(name)) return true;

        if (ExcludeForegroundProcesses && (p.Pid == foregroundPid || p.IsForeground)) return true;

        var rule = _ruleStore.GetProcessRule(p.Name);
        if (rule != null && rule.ExcludeFromProBalance) return true;

        if (ExcludeChildrenOfForeground && foregroundPid > 0
            && (p.ParentPid ?? _access.GetParentPid(p.Pid)) == foregroundPid)
            return true;

        if (ExcludeServices && _access.GetSessionId(p.Pid) == 0) return true;

        return false;
    }

    private void TryRestrain(ProcessInfo p, long now)
    {
        var original = _access.GetPriorityClass(p.Pid);
        if (original == null) return;

        var target = LowerToIdleInsteadOfBelowNormal ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
        if (TameOnlyNormal ? original != ProcessPriorityClass.Normal : Rank(original.Value) <= Rank(target))
            return;

        if (!_access.SetPriorityClass(p.Pid, target))
        {
            _logger.LogDebug("ProBalance could not restrain {Name} ({Pid})", p.Name, p.Pid);
            return;
        }

        _restrained[p.Pid] = new Restraint(p.Name, original.Value, target, now);
        LogAction(p.Name, p.Pid, original.Value, target, $"High CPU usage ({p.CpuPercent:F1}%)", restored: false);
        _logger.LogInformation("ProBalance restrained {Name} ({Pid}) from {Orig} to {Target} at {Cpu:F1}% CPU",
            p.Name, p.Pid, original, target, p.CpuPercent);
    }

    private void RestoreAll(string reason)
    {
        lock (_gate)
        {
            foreach (var pid in _restrained.Keys.ToList()) Restore(pid, reason);
            _overQuotaSince.Clear();
        }
    }

    /// <summary>Puts the original priority back, unless something else changed it while restrained.</summary>
    private void Restore(int pid, string reason)
    {
        if (!_restrained.TryRemove(pid, out var r)) return;

        var current = _access.GetPriorityClass(pid);
        if (current != r.Applied)
        {
            _logger.LogInformation("ProBalance left {Name} ({Pid}) at {Priority}: changed externally during restraint",
                r.Name, pid, current);
            return;
        }

        if (_access.SetPriorityClass(pid, r.Original))
        {
            LogAction(r.Name, pid, r.Applied, r.Original, $"Restored: {reason}", restored: true);
            _logger.LogInformation("ProBalance restored {Name} ({Pid}) to {Orig}: {Reason}", r.Name, pid, r.Original, reason);
        }
    }

    private void LogAction(string name, int pid, ProcessPriorityClass from, ProcessPriorityClass to, string reason, bool restored)
    {
        _ = _ruleStore.LogProBalanceActionAsync(new ProBalanceAction
        {
            Timestamp = DateTime.UtcNow,
            ProcessName = name,
            Pid = pid,
            OriginalPriority = (int)from,
            NewPriority = (int)to,
            Reason = reason,
            IsRestored = restored
        });
    }

    /// <summary>ProcessPriorityClass values aren't ordered, so compare by scheduling rank.</summary>
    private static int Rank(ProcessPriorityClass c) => c switch
    {
        ProcessPriorityClass.Idle => 0,
        ProcessPriorityClass.BelowNormal => 1,
        ProcessPriorityClass.Normal => 2,
        ProcessPriorityClass.AboveNormal => 3,
        ProcessPriorityClass.High => 4,
        ProcessPriorityClass.RealTime => 5,
        _ => 2
    };

    public void Dispose() => _timer?.Dispose();
}
