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
/// Responsiveness Guard - temporarily lowers the priority of background processes that hog the CPU:
/// <list type="bullet">
/// <item>Restrain a process once it has used at least <see cref="RestrainAtProcessCpuPercent"/> % of total CPU
/// for <see cref="RestrainAfterMs"/> ms, while the system is at least
/// <see cref="RestrainAtSystemCpuPercent"/> % busy.</item>
/// <item>Restore it once it drops to <see cref="RestoreAtProcessCpuPercent"/> % and has been restrained for at least
/// <see cref="MinRestraintMs"/> ms, or after <see cref="MaxRestraintMs"/> ms if that is set.</item>
/// <item>Restraint ends at once if the process comes to the foreground, and is abandoned without restoring if
/// something else (usually the user) changes the priority meanwhile.</item>
/// <item>When nobody has said which program is in the foreground (no session agent or UI reporting, see
/// <see cref="IForegroundFallback"/>), every process that could be it is treated as the foreground process: not
/// restrained, and released if it was.</item>
/// <item>Optionally (all off by default) a restraint also narrows the CPU affinity, lowers the
/// I/O priority and turns on efficiency mode, or uses only those and leaves the priority class alone
/// (<see cref="KeepPriorityClass"/>). Each is put back when the restraint ends, unless something else changed
/// that setting meanwhile.</item>
/// <item>Optionally pauses, lifting every restraint, while the user has been idle for
/// <see cref="PauseAfterIdleMs"/>.</item>
/// </list>
/// <para>
/// <b>Decision input.</b> System load is <see cref="Core.Helpers.SystemInfo.GetProcessorTimePercent"/>: busy time
/// from <c>GetSystemTimes</c>, the same measure as each process's <see cref="ProcessInfo.CpuPercent"/> (CPU time over
/// wall time, all cores), so the system gate and the per-process thresholds compare like with like. It is not the
/// frequency-scaled <c>% Processor Utility</c> the dashboard shows to match Task Manager, which reads lower while
/// the CPU is clocked down and can pass 100 % under turbo.
/// </para>
/// </summary>
public class ResponsivenessGuardEngine : IResponsivenessGuardEngine, IDisposable
{
    private readonly ILogger<ResponsivenessGuardEngine> _logger;
    private readonly IProcessMonitor _processMonitor;
    private readonly IRuleStore _ruleStore;
    private readonly IProcessPriorityAccess _access;
    private readonly Func<double> _systemCpuPercent;
    private readonly Func<long> _nowMs;
    private readonly IUserIdleSource? _idleSource;
    private readonly IForegroundFallback? _foregroundFallback;
    private bool _started;
    private bool _foregroundWasUnknown;

    // Evaluate runs on the process monitor's refresh thread; SetEnabled, ReleaseRestraint and StopAsync come from other threads.
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

    /// <summary>IO_PRIORITY_HINT IoPriorityLow, what a restraint lowers to.</summary>
    internal const int RestraintIoPriority = 1;

    /// <summary>
    /// Never restrained: lowering these hurts the whole machine (compositor, audio, session/security
    /// processes) or is pointless/denied, and ProGuard must not throttle itself.
    /// </summary>
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.Ordinal)
    {
        "system", "idle", "registry", "memory compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "dwm", "audiodg", "fontdrvhost", "proguard.engine", "proguard.ui", "proguard.sessionagent"
    };

    /// <summary>System-wide CPU % that must be reached before anything is restrained.</summary>
    public double RestrainAtSystemCpuPercent { get; set; } = 10.0;
    /// <summary>A process must use at least this % of total CPU (all cores)...</summary>
    public double RestrainAtProcessCpuPercent { get; set; } = 5.0;
    /// <summary>...continuously for this many ms before it is restrained.</summary>
    public int RestrainAfterMs { get; set; } = 900;
    /// <summary>Restore once the process uses no more than this % of total CPU.</summary>
    public double RestoreAtProcessCpuPercent { get; set; } = 2.0;
    /// <summary>A restraint lasts at least this many ms, whatever the usage does.</summary>
    public int MinRestraintMs { get; set; } = 4200;
    /// <summary>Force a restore after this many ms (the process may be restrained again later). 0 = no limit.</summary>
    public int MaxRestraintMs { get; set; }
    /// <summary>Only restrain processes currently at Normal priority class.</summary>
    public bool RestrainOnlyNormalPriority { get; set; } = true;
    /// <summary>Restrain to Idle instead of BelowNormal.</summary>
    public bool LowerToIdle { get; set; }
    /// <summary>The foreground process is exempt, and a restrained process that comes to the foreground is restored.</summary>
    public bool ExemptForeground { get; set; } = true;
    /// <summary>Direct children (by parent PID) of the foreground process are exempt.</summary>
    public bool ExemptForegroundChildren { get; set; } = true;
    /// <summary>Processes in session 0 (services) are exempt.</summary>
    public bool ExemptServices { get; set; } = true;
    // Optional restraint actions, all off by default (see ResponsivenessGuardRestraintOptions).
    /// <summary>Also restrict a restrained process to <see cref="RestrictedAffinity"/>, intersected with its own
    /// affinity; skipped if that leaves nothing or changes nothing.</summary>
    public bool RestrictAffinity { get; set; }
    /// <summary>CPU mask for <see cref="RestrictAffinity"/>; 0 = none chosen.</summary>
    public long RestrictedAffinity { get; set; }
    /// <summary>Restrict affinity by dropping one random CPU from the process's own mask. Turns the affinity action on
    /// by itself and takes precedence over <see cref="RestrictedAffinity"/>.</summary>
    public bool DropOneRandomCore { get; set; }
    /// <summary>Lower the I/O priority to Low (NtSetInformationProcess, ProcessIoPriority = 33) during restraint.</summary>
    public bool LowerIoPriority { get; set; }
    /// <summary>Turn on efficiency mode (EcoQoS via ProcessPowerThrottling) during restraint.</summary>
    public bool EfficiencyModeWhileRestrained { get; set; }
    /// <summary>Leave the priority class alone; restrain only through the actions above (nothing happens if none is on).</summary>
    public bool KeepPriorityClass { get; set; }
    /// <summary>Pause Responsiveness Guard, lifting every restraint, while the user has been idle this long. The idle time
    /// comes from the UI's reports (<see cref="IUserIdleSource"/>), since a session-0 service can't read it; with no
    /// UI reporting, the system never counts as idle.</summary>
    public bool PauseWhenIdle { get; set; }
    public int PauseAfterIdleMs { get; set; } = 30000;

    /// <summary>Picks the CPU <see cref="DropOneRandomCore"/> drops: an index in [0, count). Tests replace it.</summary>
    internal Func<int, int> RandomIndex { get; set; } = Random.Shared.Next;

    /// <summary>True while Responsiveness Guard is paused because the user is idle.</summary>
    public bool IsPausedForIdle => _pausedForIdle;

    public bool IsEngaged => !_restrained.IsEmpty;
    public int ActiveInterventions => _restrained.Count;
    public bool IsEnabled => _enabled;
    public int TotalRestraints => Volatile.Read(ref _totalRestraints);
    public IReadOnlyCollection<int> RestrainedPids => _restrained.Keys.ToList();

    /// <summary>
    /// Raised after every pass and whenever restraints are lifted outside one, with whether any process is restrained
    /// now. Raised outside Responsiveness Guard's lock. <see cref="CoreParkingService"/> follows it.
    /// </summary>
    public event Action<bool>? RestraintsUpdated;

    /// <summary>Which processes a rule may act on (see <see cref="RuleScope"/>). Tests supply fake owners.</summary>
    internal RuleScope Scope { get; set; } = RuleScope.Default;

    public ResponsivenessGuardEngine(
        ILogger<ResponsivenessGuardEngine> logger,
        IProcessMonitor processMonitor,
        IRuleStore ruleStore,
        Func<double>? systemCpuPercent = null,
        IProcessPriorityAccess? processAccess = null,
        Func<long>? clockMs = null,
        IUserIdleSource? idleSource = null,
        IForegroundFallback? foregroundFallback = null)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _ruleStore = ruleStore;
        _systemCpuPercent = systemCpuPercent ?? Core.Helpers.SystemInfo.GetProcessorTimePercent;
        _access = processAccess ?? new Win32ProcessPriorityAccess();
        _nowMs = clockMs ?? (() => Environment.TickCount64);
        _idleSource = idleSource;
        _foregroundFallback = foregroundFallback;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Responsiveness Guard engine");
        // Evaluate right after each process refresh, sampling and deciding in one loop. On a separate timer the usage it judged could be up to a
        // whole interval old, which made restraints and restores about a second late.
        if (!_started) _processMonitor.Refreshed += OnProcessesRefreshed;
        _started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Responsiveness Guard engine");
        Unsubscribe();
        RestoreAll("Responsiveness Guard stopped");
        RaiseRestraintsUpdated();
        return Task.CompletedTask;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        // Evaluate() stops running when disabled, so nothing else would ever lift the restraints.
        if (!enabled)
        {
            RestoreAll("Responsiveness Guard disabled");
            RaiseRestraintsUpdated();
        }
    }

    /// <summary>
    /// 1 (least aggressive) .. 10 (most). Level 5 is the default timing; higher levels restrain at lower
    /// usage and sooner.
    /// </summary>
    public void SetSensitivity(int level)
    {
        level = Math.Clamp(level, 1, 10);
        RestrainAtSystemCpuPercent = 15.0 - level;          // 14 .. 5
        RestrainAtProcessCpuPercent = 7.5 - 0.5 * level;         // 7 .. 2.5
        RestrainAfterMs = 1500 - 120 * level;          // 1380 .. 300 ms
        RestoreAtProcessCpuPercent = Math.Min(2.0, RestrainAtProcessCpuPercent / 2);
    }

    public void ConfigureRestraint(ResponsivenessGuardRestraintOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            RestrictAffinity = options.RestrictAffinity;
            RestrictedAffinity = options.RestrictedAffinity;
            DropOneRandomCore = options.DropOneRandomCore;
            LowerIoPriority = options.LowerIoPriority;
            EfficiencyModeWhileRestrained = options.EfficiencyModeWhileRestrained;
            KeepPriorityClass = options.KeepPriorityClass;
            PauseWhenIdle = options.PauseWhenIdle;
            PauseAfterIdleMs = Math.Max(0, options.PauseAfterIdleMs);
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
        }
        RaiseRestraintsUpdated();
        return true;
    }

    private void RaiseRestraintsUpdated()
    {
        try { RestraintsUpdated?.Invoke(!_restrained.IsEmpty); }
        catch (Exception ex) { _logger.LogError(ex, "RestraintsUpdated handler failed"); }
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
                var foregroundUnknown = IsForegroundUnknown();
                UpdateRestraints(byPid, foregroundPid, foregroundUnknown, now);
                RestrainHogs(processes, foregroundPid, foregroundUnknown, now);

                foreach (var pid in _overQuotaSince.Keys.Where(pid => !byPid.ContainsKey(pid)).ToList())
                    _overQuotaSince.Remove(pid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Responsiveness Guard evaluation loop");
        }
        finally
        {
            // Also after a pass cut short by the idle pause, which lifts every restraint. Outside the lock.
            RaiseRestraintsUpdated();
        }
    }

    /// <summary>True while Responsiveness Guard should stand aside because the user is idle; lifts every restraint on entry.</summary>
    private bool PauseWhileIdle()
    {
        var idle = PauseWhenIdle ? _idleSource?.GetIdleTime() : null;
        if (idle is { } t && t.TotalMilliseconds >= PauseAfterIdleMs)
        {
            if (!_pausedForIdle)
            {
                _pausedForIdle = true;
                _logger.LogInformation("Responsiveness Guard paused: the user has been idle for {Idle:F0} s", t.TotalSeconds);
                RestoreAll("System idle");
            }
            return true;
        }

        if (_pausedForIdle)
        {
            _pausedForIdle = false;
            _logger.LogInformation("Responsiveness Guard resumed: the user is back (or the idle pause was turned off)");
        }
        return false;
    }

    /// <summary>
    /// True when the foreground exemption is on but nobody has said which program is in the foreground, so any program
    /// that could be it must be treated as it (the program in use is never restrained; we can't tell which
    /// that is, so we don't restrain any candidate).
    /// </summary>
    private bool IsForegroundUnknown()
    {
        var unknown = _foregroundFallback is { IsForegroundKnown: false };
        if (unknown != _foregroundWasUnknown)
        {
            _foregroundWasUnknown = unknown;
            if (unknown)
                _logger.LogInformation("No foreground report from any user session: until one arrives, Responsiveness Guard leaves alone every " +
                                   "Windows program in a user session, since any of them could be the one in use");
            else
                _logger.LogInformation("Foreground reports are back: Responsiveness Guard exempts only the foreground process again");
        }
        return unknown && ExemptForeground;
    }

    /// <summary>Under <see cref="IsForegroundUnknown"/>: could this be the program the user is using?</summary>
    private bool MightBeForeground(ProcessInfo p) =>
        _access.GetSessionId(p.Pid) is > 0 && _foregroundFallback!.MightBeForeground(p.Pid, p.FilePath);

    private int ResolveForegroundPid(List<ProcessInfo> processes)
    {
        var pid = _processMonitor.GetForegroundPid();
        if (pid > 0) return pid;
        return processes.FirstOrDefault(p => p.IsForeground)?.Pid ?? 0;
    }

    /// <summary>Decides, for every process currently restrained, whether the restraint should end.</summary>
    private void UpdateRestraints(Dictionary<int, ProcessInfo> byPid, int foregroundPid, bool foregroundUnknown, long now)
    {
        foreach (var (pid, r) in _restrained.ToList())
        {
            // Exited: forget it, never write the old priority onto a recycled PID.
            if (!byPid.TryGetValue(pid, out var info))
            {
                _restrained.TryRemove(pid, out _);
                _logger.LogInformation("Responsiveness Guard restraint of {Name} ({Pid}) ended: process exited", r.Name, pid);
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
                    _logger.LogInformation("Responsiveness Guard restraint of {Name} ({Pid}) ended: priority changed externally to {Priority}",
                        r.Name, pid, current);
                    Restore(pid, "priority changed externally");
                    continue;
                }
            }

            ForgetExternalChanges(pid, r);
            if (!r.HoldsAnything)
            {
                _restrained.TryRemove(pid, out _);
                _logger.LogInformation("Responsiveness Guard restraint of {Name} ({Pid}) ended: everything it changed was changed externally",
                    r.Name, pid);
                continue;
            }

            var restrainedFor = now - r.SinceMs;
            if (ExemptForeground && (pid == foregroundPid || info.IsForeground))
                Restore(pid, "Came to the foreground");
            else if (foregroundUnknown && MightBeForeground(info))
                Restore(pid, "No foreground report: it may be the program in use");
            else if (MaxRestraintMs > 0 && restrainedFor >= MaxRestraintMs)
                Restore(pid, $"Maximum restraint time ({MaxRestraintMs} ms) reached");
            else if (restrainedFor >= MinRestraintMs && info.CpuPercent <= RestoreAtProcessCpuPercent)
                Restore(pid, $"CPU usage dropped to {info.CpuPercent:F1}%");
        }
    }

    private void RestrainHogs(List<ProcessInfo> processes, int foregroundPid, bool foregroundUnknown, long now)
    {
        var systemBusy = _systemCpuPercent() >= RestrainAtSystemCpuPercent;

        foreach (var p in processes)
        {
            if (_restrained.ContainsKey(p.Pid)) continue;

            // The over-quota run must be unbroken: any sample that doesn't qualify starts it over.
            if (!systemBusy || p.CpuPercent < RestrainAtProcessCpuPercent || IsExempt(p, foregroundPid, foregroundUnknown))
            {
                _overQuotaSince.Remove(p.Pid);
                continue;
            }

            if (!_overQuotaSince.TryGetValue(p.Pid, out var since))
                _overQuotaSince[p.Pid] = since = now;

            if (now - since >= RestrainAfterMs)
            {
                _overQuotaSince.Remove(p.Pid);
                TryRestrain(p, now);
            }
        }
    }

    /// <summary>Cheap checks first; the parent/session lookups only run for processes that are over quota.</summary>
    private bool IsExempt(ProcessInfo p, int foregroundPid, bool foregroundUnknown)
    {
        if (p.Pid <= 4) return true;
        var name = Core.Helpers.ProcessExtensions.NormalizeName(p.Name);
        if (ProtectedProcesses.Contains(name) || _exclusions.ContainsKey(name)) return true;

        if (ExemptForeground && (p.Pid == foregroundPid || p.IsForeground)) return true;

        // A standard user's exclusion spares only their own processes, not another user's of the same name.
        var rule = Scope.RuleFor(_ruleStore, p);
        if (rule != null && rule.ExcludeFromResponsivenessGuard) return true;

        if (ExemptForegroundChildren && foregroundPid > 0
            && (p.ParentPid ?? _access.GetParentPid(p.Pid)) == foregroundPid)
            return true;

        if (ExemptServices && _access.GetSessionId(p.Pid) == 0) return true;

        // Last: it may read the program file (once per program).
        if (foregroundUnknown && MightBeForeground(p)) return true;

        return false;
    }

    private void TryRestrain(ProcessInfo p, long now)
    {
        var original = _access.GetPriorityClass(p.Pid);
        if (original == null) return;
        if (RestrainOnlyNormalPriority && original != ProcessPriorityClass.Normal) return;

        var r = new Restraint { Name = p.Name, SinceMs = now, PriorityAtStart = original.Value };
        if (!KeepPriorityClass)
        {
            var target = LowerToIdle ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
            if (Rank(original.Value) <= Rank(target)) return;
            if (!_access.SetPriorityClass(p.Pid, target))
            {
                _logger.LogDebug("Responsiveness Guard could not restrain {Name} ({Pid})", p.Name, p.Pid);
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
        _logger.LogInformation("Responsiveness Guard restrained {Name} ({Pid}) at {Cpu:F1}% CPU: {Actions}", p.Name, p.Pid, p.CpuPercent, actions);
    }

    /// <summary>Affinity, I/O priority and efficiency mode, as configured. Each is skipped if it would change nothing
    /// or can't be read or written; the restraint holds whatever did apply.</summary>
    private void ApplyOptionalActions(int pid, Restraint r)
    {
        if ((RestrictAffinity || DropOneRandomCore) && _access.GetAffinityMask(pid) is { } mask
            && RestraintMask(mask) is { } narrowed && _access.SetAffinityMask(pid, narrowed))
            r.Affinity = new(mask, narrowed);

        if (LowerIoPriority && _access.GetIoPriority(pid) is { } io && io > RestraintIoPriority
            && _access.SetIoPriority(pid, RestraintIoPriority))
            r.IoPriority = new(io, RestraintIoPriority);

        if (EfficiencyModeWhileRestrained && _access.GetEfficiencyMode(pid) is { } eco && eco != EfficiencyModeState.On
            && _access.SetEfficiencyMode(pid, EfficiencyModeState.On))
            r.Efficiency = new(eco, EfficiencyModeState.On);
    }

    /// <summary>The affinity to restrain a process with mask <paramref name="current"/> to, or null to leave it.</summary>
    private long? RestraintMask(long current)
    {
        if (DropOneRandomCore)
        {
            var count = BitOperations.PopCount((ulong)current);
            if (count < 2) return null;
            var drop = Math.Clamp(RandomIndex(count), 0, count - 1);
            var bits = (ulong)current;
            for (var i = 0; i < drop; i++) bits &= bits - 1;   // clear the lowest set bits until the chosen one is lowest
            return current & ~(long)(bits & (~bits + 1));
        }

        var narrowed = current & RestrictedAffinity;
        return narrowed == 0 || narrowed == current ? null : narrowed;
    }

    /// <summary>Stops tracking any optional action whose setting someone else changed; it won't be put back.</summary>
    private void ForgetExternalChanges(int pid, Restraint r)
    {
        if (r.Affinity != null && _access.GetAffinityMask(pid) is { } mask && mask != r.Affinity.Applied)
        {
            r.Affinity = null;
            _logger.LogInformation("Responsiveness Guard will leave the affinity of {Name} ({Pid}) at 0x{Mask:X}: changed externally", r.Name, pid, mask);
        }
        if (r.IoPriority != null && _access.GetIoPriority(pid) is { } io && io != r.IoPriority.Applied)
        {
            r.IoPriority = null;
            _logger.LogInformation("Responsiveness Guard will leave the I/O priority of {Name} ({Pid}) at {Io}: changed externally", r.Name, pid, io);
        }
        if (r.Efficiency != null && _access.GetEfficiencyMode(pid) is { } eco && eco != r.Efficiency.Applied)
        {
            r.Efficiency = null;
            _logger.LogInformation("Responsiveness Guard will leave efficiency mode of {Name} ({Pid}) {State}: changed externally", r.Name, pid, eco);
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
            _logger.LogInformation("Responsiveness Guard left the {Settings} of {Name} ({Pid}) alone: changed externally during restraint",
                string.Join(", ", leftAlone), r.Name, pid);

        if (restored.Count > 0)
        {
            LogAction(r.Name, pid, from, to, $"Restored: {reason}", restored: true);
            _logger.LogInformation("Responsiveness Guard restored {Name} ({Pid}) to {Settings}: {Reason}", r.Name, pid, string.Join(", ", restored), reason);
        }
    }

    private void LogAction(string name, int pid, ProcessPriorityClass from, ProcessPriorityClass to, string reason, bool restored)
    {
        _ = _ruleStore.LogResponsivenessGuardActionAsync(new ResponsivenessGuardAction
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

    private void OnProcessesRefreshed(object? sender, EventArgs e) => Evaluate(null);

    private void Unsubscribe()
    {
        if (_started) _processMonitor.Refreshed -= OnProcessesRefreshed;
        _started = false;
    }

    public void Dispose() => Unsubscribe();
}
