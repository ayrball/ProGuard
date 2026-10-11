using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Shared.Contracts;

namespace ProGuard.Engine.Services;

/// <summary>
/// Process Lasso's "Keep running": when the last instance of a process whose rule has <see cref="ProcessRule.KeepRunning"/>
/// exits, start it again with the path, arguments and working directory it last ran with.
/// <para>
/// The command line is read from each instance while it runs, since it is gone once the process exits. Relaunches wait
/// a short back-off that doubles with each relaunch in the last minute. A process that has needed
/// <see cref="MaxRelaunchesPerWindow"/> relaunches within <see cref="CrashLoopWindow"/> is crash-looping: Keep Running
/// stops relaunching it and posts a notification, until it is started some other way.
/// </para>
/// Driven by <see cref="WatchdogService"/>'s poll, from the same process snapshot.
/// </summary>
public class KeepRunningService
{
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CrashLoopWindow = TimeSpan.FromMinutes(1);
    public const int MaxRelaunchesPerWindow = 5;

    private readonly ILogger<KeepRunningService> _logger;
    private readonly IRuleStore _ruleStore;
    private readonly IProcessActions _actions;
    private readonly IUserSessionLauncher _launcher;
    private readonly WatchdogEventLog _events;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);

    public KeepRunningService(
        ILogger<KeepRunningService> logger,
        IRuleStore ruleStore,
        IProcessActions actions,
        IUserSessionLauncher launcher,
        WatchdogEventLog? events = null,
        TimeProvider? time = null)
    {
        _logger = logger;
        _ruleStore = ruleStore;
        _actions = actions;
        _launcher = launcher;
        _time = time ?? TimeProvider.System;
        _events = events ?? new WatchdogEventLog(_time);
    }

    /// <summary>
    /// The Watchdog is restarting this process itself: don't relaunch it before <paramref name="untilUtc"/>, by which
    /// time the restarted instance should be running. If it isn't, Keep Running relaunches it then.
    /// </summary>
    public void ExpectRestart(string processName, DateTime untilUtc)
    {
        lock (_sync)
        {
            if (_watches.TryGetValue(ProcessExtensions.NormalizeName(processName), out var watch)) watch.RestartExpectedUntilUtc = untilUtc;
        }
    }

    internal void Tick(IReadOnlyCollection<ProcessInfo> processes)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rule in _ruleStore.GetProcessRules().Where(r => r.KeepRunning))
            {
                var key = ProcessExtensions.NormalizeName(rule.ProcessName);
                if (key.Length > 0) wanted.TryAdd(key, rule.ProcessName);
            }

            foreach (var gone in _watches.Keys.Where(k => !wanted.ContainsKey(k)).ToList()) _watches.Remove(gone);

            foreach (var (key, displayName) in wanted)
            {
                if (!_watches.TryGetValue(key, out var watch)) _watches[key] = watch = new Watch(displayName);
                try { Update(watch, processes.Where(p => ProcessExtensions.NormalizeName(p.Name) == key).ToList(), now); }
                catch (Exception ex) { _logger.LogError(ex, "Keep Running failed for {Name}", displayName); }
            }
        }
    }

    private void Update(Watch watch, List<ProcessInfo> running, DateTime now)
    {
        // The monitor's snapshot can lag behind a process we just started, so ask about those directly.
        watch.LaunchedPids.RemoveWhere(pid => !_actions.IsRunning(pid));

        if (running.Count > 0 || watch.LaunchedPids.Count > 0)
        {
            foreach (var process in running)
            {
                if (watch.CapturedPids.Contains(process.Pid)) continue;
                // Fails while a new process is still initialising its PEB; it is retried on the next poll.
                var info = _actions.GetLaunchInfo(process.Pid);
                if (info == null) continue;
                watch.LastKnown = info;
                watch.CapturedPids.Add(process.Pid);
            }
            watch.CapturedPids.IntersectWith(running.Select(p => p.Pid));

            if (watch.GaveUp && running.Any(p => !watch.LaunchedPids.Contains(p.Pid)))
            {
                // Someone started it again by hand: give it a fresh allowance.
                watch.GaveUp = false;
                watch.Relaunches.Clear();
            }

            watch.SeenRunning = true;
            watch.RelaunchDueUtc = null;
            return;
        }

        watch.CapturedPids.Clear();
        if (watch.SeenRunning)
        {
            watch.SeenRunning = false;
            ScheduleRelaunch(watch, now);
        }

        if (watch.RelaunchDueUtc is DateTime due && now >= due) Relaunch(watch, now);
    }

    private void ScheduleRelaunch(Watch watch, DateTime now)
    {
        if (watch.GaveUp) return;
        if (watch.LastKnown == null)
        {
            _logger.LogWarning("{Name} exited, but Keep Running never managed to read its command line", watch.DisplayName);
            return;
        }

        watch.Relaunches.RemoveAll(t => now - t >= CrashLoopWindow);
        if (watch.Relaunches.Count >= MaxRelaunchesPerWindow)
        {
            GiveUp(watch, $"it exited {watch.Relaunches.Count + 1} times within {CrashLoopWindow.TotalSeconds:0} seconds");
            return;
        }

        var due = now + Backoff(watch.Relaunches.Count);
        if (watch.RestartExpectedUntilUtc is DateTime restartBy && restartBy > due) due = restartBy;
        watch.RelaunchDueUtc = due;
        _logger.LogInformation("{Name} exited; relaunching at {Due:O}", watch.DisplayName, due);
    }

    /// <summary>Delay before the next relaunch, given how many there have been within the crash-loop window.</summary>
    internal static TimeSpan Backoff(int recentRelaunches)
    {
        var delay = InitialBackoff * Math.Pow(2, Math.Min(recentRelaunches, 10));
        return delay < MaxBackoff ? delay : MaxBackoff;
    }

    private void Relaunch(Watch watch, DateTime now)
    {
        watch.RelaunchDueUtc = null;
        var info = watch.LastKnown!;
        if (info.SessionId == 0)
        {
            GiveUp(watch, "it runs in session 0 (a service); use the service's recovery options instead");
            return;
        }

        watch.Relaunches.Add(now);
        try
        {
            var pid = _launcher.Launch(LaunchRequest.From(info));
            watch.LaunchedPids.Add(pid);
            watch.SeenRunning = true; // so an instant crash still counts as an exit on the next poll
            _events.Add(WatchdogEventKinds.Relaunched, $"Keep Running: {watch.DisplayName}",
                $"Relaunched {watch.DisplayName} as process {pid}", watch.DisplayName, pid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Keep Running could not relaunch {Name}", watch.DisplayName);
            _events.Add(WatchdogEventKinds.Failed, $"Keep Running: {watch.DisplayName}", $"Relaunch failed: {ex.Message}", watch.DisplayName);
            ScheduleRelaunch(watch, now);
        }
    }

    private void GiveUp(Watch watch, string why)
    {
        watch.GaveUp = true;
        watch.RelaunchDueUtc = null;
        _logger.LogWarning("Keep Running stopped relaunching {Name}: {Why}", watch.DisplayName, why);
        _events.Add(WatchdogEventKinds.Failed, $"Keep Running: {watch.DisplayName}",
            $"Stopped relaunching {watch.DisplayName}: {why}", watch.DisplayName, notify: true);
    }

    private sealed class Watch
    {
        public Watch(string displayName) => DisplayName = displayName;

        public string DisplayName { get; }
        public ProcessLaunchInfo? LastKnown;
        /// <summary>Running instances whose command line has been read.</summary>
        public readonly HashSet<int> CapturedPids = new();
        /// <summary>Instances Keep Running started and that are still alive.</summary>
        public readonly HashSet<int> LaunchedPids = new();
        public readonly List<DateTime> Relaunches = new();
        public bool SeenRunning;
        public DateTime? RelaunchDueUtc;
        public DateTime? RestartExpectedUntilUtc;
        public bool GaveUp;
    }
}
