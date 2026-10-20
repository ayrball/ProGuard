using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Models;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary>
/// Applies a rule's persistent per-process defaults (I/O and memory priority, efficiency mode, priority boost, CPU Sets,
/// GPU priority and GPU preference) when <see cref="ProcessGovernor"/> sees the process start, and, for rules with
/// <see cref="ProcessRule.ReapplyIfChanged"/>, re-checks them at the process poll interval and puts back any that
/// something else changed. The re-check covers the rule's CPU priority too (see <see cref="PriorityAccess"/>), which is
/// otherwise applied once as the process starts. Every apply is logged; failures are logged and returned, never thrown.
/// </summary>
public sealed class PersistentRuleService : IDisposable
{
    private readonly ILogger<PersistentRuleService> _logger;
    private readonly IReadOnlyList<IProcessRuleApplier> _appliers;
    private readonly IProcessMonitor _monitor;
    private readonly IRuleStore _ruleStore;

    // The last error per (pid, setting), so a re-apply that keeps failing is logged once rather than on every poll.
    private readonly ConcurrentDictionary<(int Pid, string Setting), string> _failing = new();

    private Timer? _timer;
    private TimeSpan _timerInterval;
    private int _sweeping;

    /// <summary>The name CPU priority goes by in results and logs.</summary>
    public const string CpuPrioritySetting = "CPU priority";

    /// <summary>
    /// Reads and sets CPU priority for the re-apply check. Null (as in tests that don't need it) leaves CPU priority to
    /// the start of the process only.
    /// </summary>
    public IRuleProcessAccess? PriorityAccess { get; set; }

    /// <summary>
    /// True while something else holds the process's CPU priority for now: a ProBalance restraint, a startup boost or
    /// Foreground Boost. The re-apply check leaves it alone then; each of those puts the priority back itself.
    /// </summary>
    public Func<int, bool>? PriorityHeld { get; set; }

    public PersistentRuleService(ILogger<PersistentRuleService> logger, IProcessTuningNative native, IProcessMonitor monitor, IRuleStore ruleStore)
        : this(logger, CreateAppliers(native), monitor, ruleStore)
    {
    }

    public PersistentRuleService(ILogger<PersistentRuleService> logger, IEnumerable<IProcessRuleApplier> appliers, IProcessMonitor monitor, IRuleStore ruleStore)
    {
        _logger = logger;
        _appliers = appliers.ToList();
        _monitor = monitor;
        _ruleStore = ruleStore;
    }

    public static IReadOnlyList<IProcessRuleApplier> CreateAppliers(IProcessTuningNative native) => new IProcessRuleApplier[]
    {
        new IoPriorityApplier(native),
        new MemoryPriorityApplier(native),
        new EfficiencyModeApplier(native),
        new PriorityBoostApplier(native),
        new CpuSetsApplier(native),
        new GpuPriorityApplier(native),
        new GpuPreferenceApplier(native),
    };

    /// <summary>Applies every setting <paramref name="rule"/> configures and returns their results (unconfigured ones omitted).</summary>
    public IReadOnlyList<RuleApplyResult> Apply(ProcessRule rule, ProcessTarget target, bool onlyIfChanged = false)
    {
        var results = new List<RuleApplyResult>();
        foreach (var applier in _appliers)
        {
            RuleApplyResult result;
            try { result = applier.Apply(rule, target, onlyIfChanged); }
            catch (Exception ex) { result = new RuleApplyResult(applier.Setting, RuleApplyOutcome.Failed, ex.Message); }

            if (result.Outcome == RuleApplyOutcome.NotConfigured) continue;
            Record(result, target, onlyIfChanged);
            results.Add(result);
        }
        return results;
    }

    /// <summary>
    /// One pass of the re-apply check: every monitored process whose rule has <see cref="ProcessRule.ReapplyIfChanged"/>
    /// gets back any setting that no longer matches. Returns how many settings were re-applied.
    /// </summary>
    public int ReapplyChanged()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return 0; // the previous pass is still running
        var reapplied = 0;
        try
        {
            foreach (var process in _monitor.GetAllProcesses().ToList())
            {
                var rule = _ruleStore.GetProcessRule(process.Name);
                if (rule is not { ReapplyIfChanged: true } || rule.DisallowExecution) continue;

                var target = new ProcessTarget(process.Pid, process.Name, process.FilePath);
                reapplied += Apply(rule, target, onlyIfChanged: true).Count(r => r.Outcome == RuleApplyOutcome.Applied);
                reapplied += ReapplyPriority(rule, target);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-apply check for persistent process rules failed");
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
            FollowPollingInterval();
        }
        return reapplied;
    }

    /// <summary>Puts back the rule's CPU priority if the process (or anything but ProGuard) changed it. Returns 1 when it did.</summary>
    private int ReapplyPriority(ProcessRule rule, ProcessTarget target)
    {
        if (rule.PriorityClass is not int value || PriorityAccess is not { } access) return 0;
        var wanted = (ProcessPriorityClass)value;
        if (!Enum.IsDefined(wanted) || PriorityHeld?.Invoke(target.Pid) == true) return 0;

        RuleApplyResult result;
        try
        {
            if (access.GetPriorityClass(target.Pid) is not { } current)
            {
                Record(new RuleApplyResult(CpuPrioritySetting, RuleApplyOutcome.Skipped, "current value can't be read"), target, reapply: true);
                return 0;
            }
            if (current == wanted) return 0;
            result = access.SetPriorityClass(target.Pid, wanted)
                ? new RuleApplyResult(CpuPrioritySetting, RuleApplyOutcome.Applied, $"{wanted} (was {current})")
                : new RuleApplyResult(CpuPrioritySetting, RuleApplyOutcome.Failed, "Windows refused the change");
        }
        catch (Exception ex)
        {
            result = new RuleApplyResult(CpuPrioritySetting, RuleApplyOutcome.Failed, ex.Message);
        }

        Record(result, target, reapply: true);
        return result.Outcome == RuleApplyOutcome.Applied ? 1 : 0;
    }

    /// <summary>Drops what is remembered about <paramref name="pid"/> once it exits, so a reused pid starts clean.</summary>
    public void Forget(int pid)
    {
        foreach (var key in _failing.Keys.Where(k => k.Pid == pid).ToList())
            _failing.TryRemove(key, out _);
    }

    public void Start()
    {
        _timerInterval = _monitor.PollingInterval;
        _timer ??= new Timer(_ => ReapplyChanged(), null, _timerInterval, _timerInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    // Runs on the same cadence as the process poll, which the user can change at runtime.
    private void FollowPollingInterval()
    {
        var interval = _monitor.PollingInterval;
        if (_timer == null || interval == _timerInterval || interval <= TimeSpan.Zero) return;
        _timerInterval = interval;
        try { _timer.Change(interval, interval); } catch (ObjectDisposedException) { }
    }

    private void Record(RuleApplyResult result, ProcessTarget target, bool reapply)
    {
        var key = (target.Pid, result.Setting);
        switch (result.Outcome)
        {
            case RuleApplyOutcome.Applied:
                _failing.TryRemove(key, out _);
                if (reapply)
                    _logger.LogInformation("Re-applied {Setting} {Detail} to {Name} ({Pid}): it had been changed", result.Setting, result.Detail, target.Name, target.Pid);
                else
                    _logger.LogInformation("Applied {Setting} {Detail} to {Name} ({Pid})", result.Setting, result.Detail, target.Name, target.Pid);
                break;

            case RuleApplyOutcome.Failed:
                var error = result.Detail ?? "unknown error";
                if (_failing.TryGetValue(key, out var previous) && previous == error)
                {
                    _logger.LogDebug("Still failing to apply {Setting} to {Name} ({Pid}): {Error}", result.Setting, target.Name, target.Pid, error);
                }
                else
                {
                    _failing[key] = error;
                    _logger.LogWarning("Failed to apply {Setting} to {Name} ({Pid}): {Error}", result.Setting, target.Name, target.Pid, error);
                }
                break;

            case RuleApplyOutcome.Unchanged:
                _failing.TryRemove(key, out _);
                if (!reapply)
                    _logger.LogInformation("{Setting} of {Name} ({Pid}) is already {Detail}", result.Setting, target.Name, target.Pid, result.Detail);
                break;

            case RuleApplyOutcome.Skipped:
                _logger.LogDebug("Skipped re-checking {Setting} of {Name} ({Pid}): {Detail}", result.Setting, target.Name, target.Pid, result.Detail);
                break;
        }
    }
}
