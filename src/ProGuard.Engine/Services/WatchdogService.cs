using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Shared.Contracts;

namespace ProGuard.Engine.Services;

/// <summary>
/// Process Lasso's Watchdog: "if a process exceeds X (CPU, memory, run time, instance count) for Y seconds, then ...".
/// <para>
/// Every second it measures each process a rule names. A rule fires for a process only once its condition has held on
/// every sample for <see cref="WatchdogRule.DurationSeconds"/>. After firing it stays quiet for that process until the
/// condition clears, or until <see cref="WatchdogRule.CooldownSeconds"/> pass with the condition still holding.
/// Instance-count rules are tracked per rule rather than per process, and act on the newest instances over the limit.
/// </para>
/// <para>
/// Priority, I/O priority and affinity changes can be temporary (<see cref="WatchdogRule.ActionDurationSeconds"/>):
/// the old value is put back when the time is up, unless something else changed it in the meantime.
/// </para>
/// </summary>
public class WatchdogService : IWatchdogService, IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    /// <summary>How often a "trim continuously" rule trims again while its condition keeps holding.</summary>
    internal static readonly TimeSpan ContinuousTrimInterval = TimeSpan.FromSeconds(5);
    /// <summary>How long Restart waits for the old process to exit before starting the new one anyway.</summary>
    internal static readonly TimeSpan RestartExitTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<WatchdogService> _logger;
    private readonly IProcessMonitor _processMonitor;
    private readonly IRuleStore _ruleStore;
    private readonly IProcessActions _actions;
    private readonly IUserSessionLauncher? _launcher;
    private readonly WatchdogEventLog _events;
    private readonly KeepRunningService? _keepRunning;
    private readonly TimeProvider _time;
    private Timer? _timer;

    // All state below is touched only inside EvaluateRules (under _tickLock) or StopAsync (also under it).
    private readonly object _tickLock = new();
    private readonly Dictionary<(int RuleId, int Pid), ConditionTracker> _trackers = new();
    private readonly Dictionary<(int Pid, RestoreKind Kind), PendingRestore> _restores = new();
    private readonly List<PendingRestart> _restarts = new();

    public WatchdogService(ILogger<WatchdogService> logger, IProcessMonitor processMonitor, IRuleStore ruleStore)
        : this(logger, processMonitor, ruleStore, new Win32ProcessActions()) { }

    public WatchdogService(
        ILogger<WatchdogService> logger,
        IProcessMonitor processMonitor,
        IRuleStore ruleStore,
        IProcessActions actions,
        IUserSessionLauncher? launcher = null,
        WatchdogEventLog? events = null,
        KeepRunningService? keepRunning = null,
        TimeProvider? time = null)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _ruleStore = ruleStore;
        _actions = actions;
        _launcher = launcher;
        _time = time ?? TimeProvider.System;
        _events = events ?? new WatchdogEventLog(_time);
        _keepRunning = keepRunning;
    }

    public WatchdogEventLog Events => _events;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Watchdog Service");
        _timer = new Timer(_ => EvaluateRules(), null, PollInterval, PollInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Watchdog Service");
        _timer?.Change(Timeout.Infinite, 0);
        lock (_tickLock) RestoreAllNow();
        return Task.CompletedTask;
    }

    public void AddRule(WatchdogRule rule)
    {
        _ruleStore.AddWatchdogRule(rule);
        _logger.LogInformation("Added watchdog rule for {ProcessName}", rule.TargetProcessName);
    }

    public void RemoveRule(int ruleId)
    {
        _ruleStore.DeleteWatchdogRule(ruleId);
        _logger.LogInformation("Removed watchdog rule {Id}", ruleId);
    }

    public void UpdateRule(WatchdogRule rule)
    {
        _ruleStore.UpdateWatchdogRule(rule);
        _logger.LogInformation("Updated watchdog rule {Id}", rule.Id);
    }

    /// <summary>One poll. Runs on a timer thread, so nothing may escape; a tick that overruns the next one makes it skip.</summary>
    internal void EvaluateRules()
    {
        if (!Monitor.TryEnter(_tickLock)) return;
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            List<ProcessInfo> processes;
            try
            {
                processes = _processMonitor.GetAllProcesses()?.ToList() ?? new List<ProcessInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watchdog could not read the process list");
                return;
            }

            RunDueRestores(now);

            try
            {
                var rules = _ruleStore.GetWatchdogRules().Where(r => r.IsEnabled).ToList();
                var live = new HashSet<(int, int)>();
                foreach (var rule in rules)
                {
                    try { EvaluateRule(rule, processes, now, live); }
                    catch (Exception ex) { _logger.LogError(ex, "Error evaluating watchdog rule {Id} ({Name})", rule.Id, rule.TargetProcessName); }
                }

                // Rules that were removed or disabled, and processes that exited, start from scratch if they come back.
                foreach (var key in _trackers.Keys.Where(k => !live.Contains(k)).ToList()) _trackers.Remove(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating watchdog rules");
            }

            RunPendingRestarts(now);

            try { _keepRunning?.Tick(processes); }
            catch (Exception ex) { _logger.LogError(ex, "Error in Keep Running"); }
        }
        finally
        {
            Monitor.Exit(_tickLock);
        }
    }

    // ---------- Conditions ----------

    private void EvaluateRule(WatchdogRule rule, List<ProcessInfo> processes, DateTime now, HashSet<(int, int)> live)
    {
        var wanted = ProcessExtensions.NormalizeName(rule.TargetProcessName);
        if (wanted.Length == 0) return;
        // Rules are stored the way the UI shows them ("chrome.exe"); the monitor reports "chrome".
        var targets = processes.Where(p => ProcessExtensions.NormalizeName(p.Name) == wanted).ToList();

        if (rule.ConditionType == WatchdogCondition.InstanceCountAbove)
        {
            var key = (rule.Id, 0);
            live.Add(key);
            var tracker = TrackerFor(key);
            var step = tracker.Advance(targets.Count > rule.ThresholdValue, now, rule);
            if (step == Step.None || (step == Step.Holding && !IsContinuousTrim(rule))) return;

            // Act on the newest instances beyond the limit, as Process Lasso's instance limit does.
            var limit = Math.Max(0, (int)Math.Floor(rule.ThresholdValue));
            var victims = targets
                .OrderByDescending(p => StartTimeUtc(p, null) ?? DateTime.MinValue)
                .ThenByDescending(p => p.Pid)
                .Take(Math.Max(1, targets.Count - limit))
                .ToList();
            var reason = $"{targets.Count} instances running, more than {Format(rule.ThresholdValue)}{DurationText(rule)}";
            Act(rule, step, victims, reason, tracker, now);
            return;
        }

        foreach (var process in targets)
        {
            var key = (rule.Id, process.Pid);
            live.Add(key);
            var tracker = TrackerFor(key);
            var value = Measure(rule, process, tracker, now);
            var step = tracker.Advance(value > rule.ThresholdValue, now, rule);
            if (step == Step.None) continue;

            Act(rule, step, new List<ProcessInfo> { process }, Describe(rule, value!.Value), tracker, now);
        }
    }

    private ConditionTracker TrackerFor((int, int) key)
    {
        if (!_trackers.TryGetValue(key, out var tracker)) _trackers[key] = tracker = new ConditionTracker();
        return tracker;
    }

    /// <summary>The figure the rule compares with its threshold, or null when it can't be read (never fires).</summary>
    private double? Measure(WatchdogRule rule, ProcessInfo process, ConditionTracker tracker, DateTime now)
    {
        switch (rule.ConditionType)
        {
            case WatchdogCondition.CpuAbove:
                return process.CpuPercent; // percent of the whole machine, like Process Lasso's
            case WatchdogCondition.RamAbove:
                var memory = _actions.GetMemory(process.Pid);
                var bytes = rule.MemoryMetric == WatchdogMemoryMetric.PrivateBytes
                    ? memory?.PrivateBytes ?? process.PrivateBytes
                    : memory?.WorkingSetBytes ?? process.WorkingSetBytes;
                return bytes / (1024.0 * 1024.0);
            case WatchdogCondition.RunningLongerThan:
                tracker.StartTimeUtc ??= StartTimeUtc(process, null);
                return tracker.StartTimeUtc is DateTime started ? (now - started).TotalSeconds : null;
            default:
                return null;
        }
    }

    private DateTime? StartTimeUtc(ProcessInfo process, DateTime? fallback)
    {
        if (process.StartTime != default)
            return process.StartTime.Kind == DateTimeKind.Utc ? process.StartTime : process.StartTime.ToUniversalTime();
        return _actions.GetStartTimeUtc(process.Pid) ?? fallback;
    }

    private static string Describe(WatchdogRule rule, double value) => rule.ConditionType switch
    {
        WatchdogCondition.CpuAbove => $"CPU {value:0.#}% above {Format(rule.ThresholdValue)}%{DurationText(rule)}",
        WatchdogCondition.RamAbove => $"{(rule.MemoryMetric == WatchdogMemoryMetric.PrivateBytes ? "private bytes" : "working set")} {value:0} MB above {Format(rule.ThresholdValue)} MB{DurationText(rule)}",
        WatchdogCondition.RunningLongerThan => $"running for {TimeSpan.FromSeconds(Math.Floor(value))}, longer than {Format(rule.ThresholdValue)} s",
        _ => rule.ConditionType.ToString()
    };

    private static string DurationText(WatchdogRule rule) => rule.DurationSeconds > 0 ? $" for {rule.DurationSeconds} s" : "";

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // ---------- Actions ----------

    private void Act(WatchdogRule rule, Step step, List<ProcessInfo> victims, string reason, ConditionTracker tracker, DateTime now)
    {
        if (step == Step.Holding)
        {
            // Between firings only "trim continuously" has anything to do.
            if (IsContinuousTrim(rule) && now - tracker.LastTrimUtc >= ContinuousTrimInterval)
            {
                tracker.LastTrimUtc = now;
                foreach (var victim in victims) TryRun(rule, victim, reason, quiet: true, () => Trim(victim));
            }
            return;
        }

        _logger.LogInformation("Watchdog rule {Id} fired for {Name}: {Reason}; action {Action}", rule.Id, rule.TargetProcessName, reason, rule.ActionType);
        var title = $"Watchdog: {rule.TargetProcessName}";

        switch (rule.ActionType)
        {
            case WatchdogAction.Notify:
                var target = victims.Count == 1 ? $"{victims[0].Name} ({victims[0].Pid})" : rule.TargetProcessName;
                var text = string.IsNullOrWhiteSpace(rule.ActionArgument) ? $"{target}: {reason}" : $"{rule.ActionArgument.Trim()} ({target}: {reason})";
                _events.Add(WatchdogEventKinds.Fired, title, text, rule.TargetProcessName, SinglePid(victims), rule.Id, notify: true);
                return;

            case WatchdogAction.LogAlert:
                _logger.LogWarning("Watchdog alert for {Name}: {Reason}", rule.TargetProcessName, reason);
                _events.Add(WatchdogEventKinds.Fired, title, $"{reason} (logged)", rule.TargetProcessName, SinglePid(victims), rule.Id);
                return;

            case WatchdogAction.Launch:
                TryRun(rule, victims.FirstOrDefault(), reason, quiet: false, () =>
                {
                    var pid = LaunchProgram(rule, victims.FirstOrDefault());
                    return $"launched {rule.ActionArgument?.Trim()} (process {pid})";
                });
                return;
        }

        foreach (var victim in victims)
        {
            if (IsProtected(victim.Pid))
            {
                _logger.LogWarning("Watchdog rule {Id} matched protected process {Pid}; not touching it", rule.Id, victim.Pid);
                continue;
            }

            TryRun(rule, victim, reason, quiet: false, () => rule.ActionType switch
            {
                WatchdogAction.Terminate => Terminate(victim),
                WatchdogAction.Restart => Restart(victim, now),
                WatchdogAction.SetPriority => SetPriority(rule, victim, now),
                WatchdogAction.SetIoPriority => SetIoPriority(rule, victim, now),
                WatchdogAction.SetAffinity => SetAffinity(rule, victim, now),
                WatchdogAction.TrimWorkingSet => Trim(victim, tracker, now),
                _ => throw new NotSupportedException($"Unknown watchdog action {rule.ActionType}")
            });
        }
    }

    /// <summary>Runs one action, recording what it did (or why it failed) in the event log.</summary>
    private void TryRun(WatchdogRule rule, ProcessInfo? victim, string reason, bool quiet, Func<string> action)
    {
        var title = $"Watchdog: {rule.TargetProcessName}";
        try
        {
            var done = action();
            if (!quiet)
                _events.Add(WatchdogEventKinds.Fired, title, $"{Capitalize(done)}: {reason}", victim?.Name ?? rule.TargetProcessName, victim?.Pid, rule.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog action {Action} failed for {Name} ({Pid})", rule.ActionType, victim?.Name ?? rule.TargetProcessName, victim?.Pid);
            _events.Add(WatchdogEventKinds.Failed, title, $"{rule.ActionType} failed: {ex.Message}", victim?.Name ?? rule.TargetProcessName, victim?.Pid, rule.Id);
        }
    }

    private string Terminate(ProcessInfo victim)
    {
        _actions.Terminate(victim.Pid);
        return $"terminated {victim.Name} ({victim.Pid})";
    }

    private string Restart(ProcessInfo victim, DateTime now)
    {
        if (_launcher == null) throw new InvalidOperationException("No launcher is available to restart processes");
        // Read before terminating: the command line lives in the process.
        var info = _actions.GetLaunchInfo(victim.Pid) ?? throw new InvalidOperationException($"Cannot read the command line of process {victim.Pid}");
        if (info.SessionId == 0) throw new InvalidOperationException($"{victim.Name} runs in session 0 (a service); restart it through the Service Control Manager");

        _keepRunning?.ExpectRestart(victim.Name, now + RestartExitTimeout);
        _actions.Terminate(victim.Pid);
        _restarts.Add(new PendingRestart(victim.Pid, victim.Name, info, now + RestartExitTimeout));
        return $"terminated {victim.Name} ({victim.Pid}) to restart it";
    }

    private string SetPriority(WatchdogRule rule, ProcessInfo victim, DateTime now)
    {
        var priority = WatchdogArguments.DefaultPriority;
        if (!string.IsNullOrWhiteSpace(rule.ActionArgument) && !WatchdogArguments.TryParsePriority(rule.ActionArgument, out priority))
            throw new ArgumentException($"Unknown priority '{rule.ActionArgument}'");

        var before = _actions.GetPriority(victim.Pid);
        _actions.SetPriority(victim.Pid, priority);
        ScheduleRestore(rule, victim, RestoreKind.Priority, (long?)before, (long)priority, now);
        return $"set {victim.Name} ({victim.Pid}) to {priority} priority{ForText(rule)}";
    }

    private string SetIoPriority(WatchdogRule rule, ProcessInfo victim, DateTime now)
    {
        var io = WatchdogArguments.DefaultIoPriority;
        if (!string.IsNullOrWhiteSpace(rule.ActionArgument) && !WatchdogArguments.TryParseIoPriority(rule.ActionArgument, out io))
            throw new ArgumentException($"Unknown I/O priority '{rule.ActionArgument}'");

        var before = _actions.GetIoPriority(victim.Pid);
        _actions.SetIoPriority(victim.Pid, io);
        ScheduleRestore(rule, victim, RestoreKind.IoPriority, before, io, now);
        return $"set {victim.Name} ({victim.Pid}) to {WatchdogArguments.IoPriorityName(io)} I/O priority{ForText(rule)}";
    }

    private string SetAffinity(WatchdogRule rule, ProcessInfo victim, DateTime now)
    {
        if (!WatchdogArguments.TryParseAffinity(rule.ActionArgument, out var mask))
            throw new ArgumentException($"Set affinity needs a CPU mask such as 0x3, not '{rule.ActionArgument}'");

        var before = _actions.GetAffinity(victim.Pid);
        _actions.SetAffinity(victim.Pid, mask);
        ScheduleRestore(rule, victim, RestoreKind.Affinity, before, mask, now);
        return $"set {victim.Name} ({victim.Pid}) affinity to 0x{mask:X}{ForText(rule)}";
    }

    private string Trim(ProcessInfo victim, ConditionTracker? tracker = null, DateTime? now = null)
    {
        _actions.TrimWorkingSet(victim.Pid);
        if (tracker != null && now != null) tracker.LastTrimUtc = now.Value;
        return $"trimmed the working set of {victim.Name} ({victim.Pid})";
    }

    private int LaunchProgram(WatchdogRule rule, ProcessInfo? context)
    {
        if (_launcher == null) throw new InvalidOperationException("No launcher is available to start programs");
        var commandLine = Environment.ExpandEnvironmentVariables(rule.ActionArgument ?? "").Trim();
        if (!WatchdogArguments.TrySplitCommandLine(commandLine, out var program, out _))
            throw new ArgumentException("Launch needs a command line");

        // On the desktop of the process that tripped the rule; a service's (session 0) goes to the console user.
        int? session = context != null ? _actions.GetSessionId(context.Pid) : null;
        if (session <= 0) session = null;
        var executable = Path.IsPathRooted(program) ? program : null;
        return _launcher.Launch(new LaunchRequest(executable, commandLine, null, session));
    }

    private static bool IsContinuousTrim(WatchdogRule rule) =>
        rule.ActionType == WatchdogAction.TrimWorkingSet && WatchdogArguments.IsContinuousTrim(rule.ActionArgument);

    private static string ForText(WatchdogRule rule) => rule.ActionDurationSeconds > 0 ? $" for {rule.ActionDurationSeconds} s" : "";

    private static int? SinglePid(List<ProcessInfo> victims) => victims.Count == 1 ? victims[0].Pid : null;

    // The System process, the idle process and the engine itself.
    private static bool IsProtected(int pid) => pid <= 4 || pid == Environment.ProcessId;

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ---------- Restart ----------

    private void RunPendingRestarts(DateTime now)
    {
        for (var i = _restarts.Count - 1; i >= 0; i--)
        {
            var pending = _restarts[i];
            if (_actions.IsRunning(pending.OldPid) && now < pending.DeadlineUtc) continue;
            _restarts.RemoveAt(i);

            try
            {
                var pid = _launcher!.Launch(LaunchRequest.From(pending.Info));
                _events.Add(WatchdogEventKinds.Relaunched, $"Watchdog: {pending.Name}", $"Restarted {pending.Name} as process {pid}", pending.Name, pid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restart {Name}", pending.Name);
                _events.Add(WatchdogEventKinds.Failed, $"Watchdog: {pending.Name}", $"Restart failed: {ex.Message}", pending.Name, pending.OldPid);
            }
        }
    }

    // ---------- Temporary changes ----------

    private void ScheduleRestore(WatchdogRule rule, ProcessInfo victim, RestoreKind kind, long? before, long applied, DateTime now)
    {
        if (rule.ActionDurationSeconds <= 0 || before == null) return;
        var due = now + TimeSpan.FromSeconds(rule.ActionDurationSeconds);
        var key = (victim.Pid, kind);

        // Firing again before the restore keeps the value from before the first change, not our own.
        if (_restores.TryGetValue(key, out var existing))
        {
            _restores[key] = existing with { Applied = applied, DueUtc = due };
            return;
        }
        if (before == applied) return;
        _restores[key] = new PendingRestore(victim.Pid, victim.Name, kind, before.Value, applied, due, _actions.GetStartTimeUtc(victim.Pid));
    }

    private void RunDueRestores(DateTime now)
    {
        foreach (var restore in _restores.Values.Where(r => now >= r.DueUtc).ToList())
        {
            _restores.Remove((restore.Pid, restore.Kind));
            Restore(restore);
        }
    }

    private void RestoreAllNow()
    {
        foreach (var restore in _restores.Values.ToList()) Restore(restore);
        _restores.Clear();
    }

    private void Restore(PendingRestore restore)
    {
        try
        {
            // Same pid but a different start time: the process exited and the pid was reused.
            if (!_actions.IsRunning(restore.Pid)) return;
            var started = _actions.GetStartTimeUtc(restore.Pid);
            if (restore.StartTimeUtc != null && started != null && started != restore.StartTimeUtc) return;

            var current = restore.Kind switch
            {
                RestoreKind.Priority => (long?)_actions.GetPriority(restore.Pid),
                RestoreKind.IoPriority => _actions.GetIoPriority(restore.Pid),
                _ => _actions.GetAffinity(restore.Pid)
            };
            if (current != restore.Applied)
            {
                _logger.LogInformation("Not restoring {Kind} of {Name} ({Pid}): it was changed meanwhile", restore.Kind, restore.Name, restore.Pid);
                return;
            }

            switch (restore.Kind)
            {
                case RestoreKind.Priority: _actions.SetPriority(restore.Pid, (ProcessPriorityClass)restore.Original); break;
                case RestoreKind.IoPriority: _actions.SetIoPriority(restore.Pid, (int)restore.Original); break;
                default: _actions.SetAffinity(restore.Pid, restore.Original); break;
            }
            _events.Add(WatchdogEventKinds.Restored, $"Watchdog: {restore.Name}", $"Restored the {RestoreName(restore)} of {restore.Name} ({restore.Pid})", restore.Name, restore.Pid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore {Kind} of {Name} ({Pid})", restore.Kind, restore.Name, restore.Pid);
        }
    }

    private static string RestoreName(PendingRestore restore) => restore.Kind switch
    {
        RestoreKind.Priority => $"priority ({(ProcessPriorityClass)restore.Original})",
        RestoreKind.IoPriority => $"I/O priority ({WatchdogArguments.IoPriorityName((int)restore.Original)})",
        _ => $"affinity (0x{restore.Original:X})"
    };

    public void Dispose() => _timer?.Dispose();

    // ---------- State ----------

    internal enum Step { None, Fire, Holding }

    private enum RestoreKind { Priority, IoPriority, Affinity }

    private sealed record PendingRestore(int Pid, string Name, RestoreKind Kind, long Original, long Applied, DateTime DueUtc, DateTime? StartTimeUtc);

    private sealed record PendingRestart(int OldPid, string Name, ProcessLaunchInfo Info, DateTime DeadlineUtc);

    /// <summary>Whether one rule's condition has held long enough for one process (or, for instance counts, one rule).</summary>
    internal sealed class ConditionTracker
    {
        public DateTime? HoldingSinceUtc;
        public DateTime? FiredAtUtc;
        public DateTime LastTrimUtc = DateTime.MinValue;
        public DateTime? StartTimeUtc;

        public Step Advance(bool holds, DateTime now, WatchdogRule rule)
        {
            if (!holds)
            {
                HoldingSinceUtc = null;
                FiredAtUtc = null;
                return Step.None;
            }

            HoldingSinceUtc ??= now;
            if (FiredAtUtc is not DateTime firedAt)
            {
                if (now - HoldingSinceUtc.Value < TimeSpan.FromSeconds(Math.Max(0, rule.DurationSeconds))) return Step.None;
                FiredAtUtc = now;
                return Step.Fire;
            }

            if (rule.CooldownSeconds > 0 && now - firedAt >= TimeSpan.FromSeconds(rule.CooldownSeconds))
            {
                FiredAtUtc = now;
                return Step.Fire;
            }
            return Step.Holding;
        }
    }
}
