using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services.ProcessRules;

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
/// <item>Optionally (all off by default, as in Process Lasso) a restraint also narrows the CPU affinity, lowers the
/// I/O priority and turns on efficiency mode, or uses only those and leaves the priority class alone
/// (<see cref="DoNotLowerPriorityClass"/>). Each is put back when the restraint ends, unless something else changed
/// that setting meanwhile.</item>
/// <item>Optionally pauses, lifting every restraint, while the user has been idle for
/// <see cref="DisableProBalanceIfSysIdleThisManyMS"/>.</item>
/// </list>
/// Property names mirror the <c>[OutOfControlProcessRestraint]</c> keys in prolasso.ini.
/// <para>
/// <b>Decision input.</b> System load is <see cref="Core.Helpers.SystemInfo.GetProcessorTimePercent"/>: busy time
/// from <c>GetSystemTimes</c>, the same measure as each process's <see cref="ProcessInfo.CpuPercent"/> (CPU time over
/// wall time, all cores), so the system gate and the per-process thresholds compare like with like. It is not the
/// frequency-scaled <c>% Processor Utility</c> the dashboard shows to match Task Manager, which reads lower while
/// the CPU is clocked down and can pass 100 % under turbo.
/// </para>
/// </summary>
public class ProBalanceEngine : IProBalanceEngine, IDisposable
{
    private readonly ILogger<ProBalanceEngine> _logger;
    private readonly IProcessMonitor _processMonitor;
    private readonly IRuleStore _ruleStore;
    private readonly IProcessPriorityAccess _access;
    private readonly Func<double> _systemCpuPercent;
    private readonly Func<long> _nowMs;
    private readonly IUserIdleSource? _idleSource;
    private Timer? _timer;

    // Evaluate runs on a timer thread; SetEnabled, ReleaseRestraint and StopAsync come from other threads.
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, Restraint> _restrained = new();
    // PID -> time (ms) the process was first seen over quota in the current unbroken run.
    private readonly Dictionary<int, long> _overQuotaSince = new();
    // Normalised names (see ProcessExtensions.NormalizeName). Mutated from IPC/UI threads while Evaluate reads.
    private readonly ConcurrentDictionary<string, byte> _exclusions = new();
    private volatile bool _enabled = true;
    private volatile bool _pausedForIdle;
    private int _totalRestraints;

    /// <summary>A setting a restraint changed: what to put back, and what we set (to notice someone else changing it).</summary>
    private sealed record Change<T>(T Original, T Applied);

    /// <summary>What one restraint changed. Each part is dropped once something else changes that setting.</summary>
    private sealed class Restraint
    {
        public required string Name { get; init; }
        public required long SinceMs { get; init; }
        /// <summary>Priority class when the restraint began, whether or not it was lowered.</summary>
        public required ProcessPriorityClass PriorityAtStart { get; init; }
        public Change<ProcessPriorityClass>? Priority { get; set; }
        public Change<long>? Affinity { get; set; }
        public Change<int>? IoPriority { get; set; }
        public Change<EfficiencyModeState>? Efficiency { get; set; }

        public bool HoldsAnything => Priority != null || Affinity != null || IoPriority != null || Efficiency != null;
    }

    /// <summary>IO_PRIORITY_HINT IoPriorityLow, what Process Lasso lowers to.</summary>
    internal const int RestraintIoPriority = 1;

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

    // Optional restraint actions, all off by default (see ProBalanceRestraintOptions).
    /// <summary>Also restrict a restrained process to <see cref="RestraintAffinity"/>, intersected with its own
    /// affinity; skipped if that leaves nothing or changes nothing.</summary>
    public bool RestrainByAffinity { get; set; }
    /// <summary>CPU mask for <see cref="RestrainByAffinity"/>; 0 = none chosen.</summary>
    public long RestraintAffinity { get; set; }
    /// <summary>Restrict affinity by dropping one random CPU from the process's own mask. Turns the affinity action on
    /// by itself and takes precedence over <see cref="RestraintAffinity"/>.</summary>
    public bool ProBalanceDropOneRandomCore { get; set; }
    /// <summary>Lower the I/O priority to Low (NtSetInformationProcess, ProcessIoPriority = 33) during restraint.</summary>
    public bool LowerIOPriorityDuringRestraint { get; set; }
    /// <summary>Turn on efficiency mode (EcoQoS via ProcessPowerThrottling) during restraint.</summary>
    public bool UseEfficiencyMode { get; set; }
    /// <summary>Leave the priority class alone; restrain only through the actions above (nothing happens if none is on).</summary>
    public bool DoNotLowerPriorityClass { get; set; }
    /// <summary>Pause ProBalance, lifting every restraint, while the user has been idle this long. The idle time
    /// comes from the UI's reports (<see cref="IUserIdleSource"/>), since a session-0 service can't read it; with no
    /// UI reporting, the system never counts as idle.</summary>
    public bool DisableProBalanceWhenSysIdle { get; set; }
    public int DisableProBalanceIfSysIdleThisManyMS { get; set; } = 30000;

    /// <summary>Picks the CPU <see cref="ProBalanceDropOneRandomCore"/> drops: an index in [0, count). Tests replace it.</summary>
    internal Func<int, int> RandomIndex { get; set; } = Random.Shared.Next;

    /// <summary>True while ProBalance is paused because the user is idle.</summary>
    public bool IsPausedForIdle => _pausedForIdle;

    public bool IsEngaged => !_restrained.IsEmpty;
    public int ActiveInterventions => _restrained.Count;
    public bool IsEnabled => _enabled;
    public int TotalRestraints => Volatile.Read(ref _totalRestraints);
    public IReadOnlyCollection<int> RestrainedPids => _restrained.Keys.ToList();

    public ProBalanceEngine(
        ILogger<ProBalanceEngine> logger,
        IProcessMonitor processMonitor,
        IRuleStore ruleStore,
        Func<double>? systemCpuPercent = null,
        IProcessPriorityAccess? processAccess = null,
        Func<long>? clockMs = null,
        IUserIdleSource? idleSource = null)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _ruleStore = ruleStore;
        _systemCpuPercent = systemCpuPercent ?? Core.Helpers.SystemInfo.GetProcessorTimePercent;
        _access = processAccess ?? new Win32ProcessPriorityAccess();
        _nowMs = clockMs ?? (() => Environment.TickCount64);
        _idleSource = idleSource;
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

    public void ConfigureRestraint(ProBalanceRestraintOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            RestrainByAffinity = options.RestrainByAffinity;
            RestraintAffinity = options.RestraintAffinity;
            ProBalanceDropOneRandomCore = options.ProBalanceDropOneRandomCore;
            LowerIOPriorityDuringRestraint = options.LowerIOPriorityDuringRestraint;
            UseEfficiencyMode = options.UseEfficiencyMode;
            DoNotLowerPriorityClass = options.DoNotLowerPriorityClass;
            DisableProBalanceWhenSysIdle = options.DisableProBalanceWhenSysIdle;
            DisableProBalanceIfSysIdleThisManyMS = Math.Max(0, options.DisableProBalanceIfSysIdleThisManyMS);
        }
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
                if (PauseWhileIdle()) return;
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

    /// <summary>True while ProBalance should stand aside because the user is idle; lifts every restraint on entry.</summary>
    private bool PauseWhileIdle()
    {
        var idle = DisableProBalanceWhenSysIdle ? _idleSource?.GetIdleTime() : null;
        if (idle is { } t && t.TotalMilliseconds >= DisableProBalanceIfSysIdleThisManyMS)
        {
            if (!_pausedForIdle)
            {
                _pausedForIdle = true;
                _logger.LogInformation("ProBalance paused: the user has been idle for {Idle:F0} s", t.TotalSeconds);
                RestoreAll("System idle");
            }
            return true;
        }

        if (_pausedForIdle)
        {
            _pausedForIdle = false;
            _logger.LogInformation("ProBalance resumed: the user is back (or the idle pause was turned off)");
        }
        return false;
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

            if (r.Priority != null)
            {
                var current = _access.GetPriorityClass(pid);
                if (current == null)
                {
                    _restrained.TryRemove(pid, out _);
                    continue;
                }
                if (current != r.Priority.Applied)
                {
                    // Someone else (usually the user) set a priority while we held it down; theirs wins, and the
                    // restraint ends, putting back whatever else it changed: they evidently want the process to run.
                    r.Priority = null;
                    _logger.LogInformation("ProBalance restraint of {Name} ({Pid}) ended: priority changed externally to {Priority}",
                        r.Name, pid, current);
                    Restore(pid, "priority changed externally");
                    continue;
                }
            }

            ForgetExternalChanges(pid, r);
            if (!r.HoldsAnything)
            {
                _restrained.TryRemove(pid, out _);
                _logger.LogInformation("ProBalance restraint of {Name} ({Pid}) ended: everything it changed was changed externally",
                    r.Name, pid);
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
        if (TameOnlyNormal && original != ProcessPriorityClass.Normal) return;

        var r = new Restraint { Name = p.Name, SinceMs = now, PriorityAtStart = original.Value };
        if (!DoNotLowerPriorityClass)
        {
            var target = LowerToIdleInsteadOfBelowNormal ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
            if (Rank(original.Value) <= Rank(target)) return;
            if (!_access.SetPriorityClass(p.Pid, target))
            {
                _logger.LogDebug("ProBalance could not restrain {Name} ({Pid})", p.Name, p.Pid);
                return;
            }
            r.Priority = new(original.Value, target);
        }

        ApplyOptionalActions(p.Pid, r);
        if (!r.HoldsAnything) return;

        _restrained[p.Pid] = r;
        Interlocked.Increment(ref _totalRestraints);
        var actions = Describe(r);
        LogAction(p.Name, p.Pid, original.Value, r.Priority?.Applied ?? original.Value,
            $"High CPU usage ({p.CpuPercent:F1}%): {actions}", restored: false);
        _logger.LogInformation("ProBalance restrained {Name} ({Pid}) at {Cpu:F1}% CPU: {Actions}", p.Name, p.Pid, p.CpuPercent, actions);
    }

    /// <summary>Affinity, I/O priority and efficiency mode, as configured. Each is skipped if it would change nothing
    /// or can't be read or written; the restraint holds whatever did apply.</summary>
    private void ApplyOptionalActions(int pid, Restraint r)
    {
        if ((RestrainByAffinity || ProBalanceDropOneRandomCore) && _access.GetAffinityMask(pid) is { } mask
            && RestraintMask(mask) is { } narrowed && _access.SetAffinityMask(pid, narrowed))
            r.Affinity = new(mask, narrowed);

        if (LowerIOPriorityDuringRestraint && _access.GetIoPriority(pid) is { } io && io > RestraintIoPriority
            && _access.SetIoPriority(pid, RestraintIoPriority))
            r.IoPriority = new(io, RestraintIoPriority);

        if (UseEfficiencyMode && _access.GetEfficiencyMode(pid) is { } eco && eco != EfficiencyModeState.On
            && _access.SetEfficiencyMode(pid, EfficiencyModeState.On))
            r.Efficiency = new(eco, EfficiencyModeState.On);
    }

    /// <summary>The affinity to restrain a process with mask <paramref name="current"/> to, or null to leave it.</summary>
    private long? RestraintMask(long current)
    {
        if (ProBalanceDropOneRandomCore)
        {
            var count = BitOperations.PopCount((ulong)current);
            if (count < 2) return null;
            var drop = Math.Clamp(RandomIndex(count), 0, count - 1);
            var bits = (ulong)current;
            for (var i = 0; i < drop; i++) bits &= bits - 1;   // clear the lowest set bits until the chosen one is lowest
            return current & ~(long)(bits & (~bits + 1));
        }

        var narrowed = current & RestraintAffinity;
        return narrowed == 0 || narrowed == current ? null : narrowed;
    }

    /// <summary>Stops tracking any optional action whose setting someone else changed; it won't be put back.</summary>
    private void ForgetExternalChanges(int pid, Restraint r)
    {
        if (r.Affinity != null && _access.GetAffinityMask(pid) is { } mask && mask != r.Affinity.Applied)
        {
            r.Affinity = null;
            _logger.LogInformation("ProBalance will leave the affinity of {Name} ({Pid}) at 0x{Mask:X}: changed externally", r.Name, pid, mask);
        }
        if (r.IoPriority != null && _access.GetIoPriority(pid) is { } io && io != r.IoPriority.Applied)
        {
            r.IoPriority = null;
            _logger.LogInformation("ProBalance will leave the I/O priority of {Name} ({Pid}) at {Io}: changed externally", r.Name, pid, io);
        }
        if (r.Efficiency != null && _access.GetEfficiencyMode(pid) is { } eco && eco != r.Efficiency.Applied)
        {
            r.Efficiency = null;
            _logger.LogInformation("ProBalance will leave efficiency mode of {Name} ({Pid}) {State}: changed externally", r.Name, pid, eco);
        }
    }

    private static string Describe(Restraint r)
    {
        var parts = new List<string>();
        if (r.Priority != null) parts.Add($"priority {r.Priority.Applied}");
        if (r.Affinity != null) parts.Add($"affinity 0x{r.Affinity.Applied:X} (was 0x{r.Affinity.Original:X})");
        if (r.IoPriority != null) parts.Add("I/O priority Low");
        if (r.Efficiency != null) parts.Add("efficiency mode on");
        return string.Join(", ", parts);
    }

    private void RestoreAll(string reason)
    {
        lock (_gate)
        {
            foreach (var pid in _restrained.Keys.ToList()) Restore(pid, reason);
            _overQuotaSince.Clear();
        }
    }

    /// <summary>Puts back everything the restraint changed, except settings something else changed meanwhile.</summary>
    private void Restore(int pid, string reason)
    {
        if (!_restrained.TryRemove(pid, out var r)) return;

        var restored = new List<string>();
        var leftAlone = new List<string>();
        // The action log records priorities; without a priority restore, from and to are both the start priority.
        ProcessPriorityClass from = r.PriorityAtStart, to = r.PriorityAtStart;

        if (r.Priority is { } priority)
        {
            if (_access.GetPriorityClass(pid) == priority.Applied && _access.SetPriorityClass(pid, priority.Original))
            {
                restored.Add($"priority {priority.Original}");
                (from, to) = (priority.Applied, priority.Original);
            }
            else leftAlone.Add("priority");
        }
        if (r.Affinity is { } affinity)
        {
            if (_access.GetAffinityMask(pid) == affinity.Applied && _access.SetAffinityMask(pid, affinity.Original))
                restored.Add($"affinity 0x{affinity.Original:X}");
            else leftAlone.Add("affinity");
        }
        if (r.IoPriority is { } io)
        {
            if (_access.GetIoPriority(pid) == io.Applied && _access.SetIoPriority(pid, io.Original))
                restored.Add($"I/O priority {io.Original}");
            else leftAlone.Add("I/O priority");
        }
        if (r.Efficiency is { } eco)
        {
            if (_access.GetEfficiencyMode(pid) == eco.Applied && _access.SetEfficiencyMode(pid, eco.Original))
                restored.Add($"efficiency mode {eco.Original}");
            else leftAlone.Add("efficiency mode");
        }

        if (leftAlone.Count > 0)
            _logger.LogInformation("ProBalance left the {Settings} of {Name} ({Pid}) alone: changed externally during restraint",
                string.Join(", ", leftAlone), r.Name, pid);

        if (restored.Count > 0)
        {
            LogAction(r.Name, pid, from, to, $"Restored: {reason}", restored: true);
            _logger.LogInformation("ProBalance restored {Name} ({Pid}) to {Settings}: {Reason}", r.Name, pid, string.Join(", ", restored), reason);
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
