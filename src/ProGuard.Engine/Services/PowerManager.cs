using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>Which feature holds the power plan. Later members win: see <see cref="PowerManager"/>.</summary>
public enum PowerPlanOwner { None, LoadSwitcher, IdleSaver, PerformanceMode }

/// <summary>A power plan change the engine made, with the reason (shown in the log).</summary>
public sealed record PowerPlanChange(DateTime Time, Guid From, Guid To, PowerPlanOwner Owner, string Reason);

/// <summary>
/// Manages Windows power plans for Performance Mode, IdleSaver and the load-based power switcher.
/// <para>
/// <b>One arbitration point.</b> Each feature only says which plan it wants (or nothing); <see cref="Arbitrate"/> picks
/// the winner and is the only code that switches plans for them. Precedence: Performance Mode beats IdleSaver, which
/// beats the load switcher. IdleSaver is also disabled while Performance Mode is on (Process Lasso's
/// DisableEnergySaverDuringGamingMode), so it starts counting again only once Performance Mode ends.
/// </para>
/// <para>
/// The plan that was active before any feature took over is the <i>baseline</i>; when the last feature lets go, the
/// baseline comes back. Handing over between features doesn't change it, so after IdleSaver then Performance Mode the
/// user still ends up on their own plan.
/// </para>
/// <para>
/// <b>Manual changes win.</b> If the plan is changed by someone else while a feature holds it (Windows' power options,
/// another tool, <see cref="SetActivePlan"/>), that plan becomes the new baseline and the feature's current decision
/// counts as overridden: it isn't re-applied and nothing is restored over the user's choice when it ends. A later,
/// different decision (the load switcher changing level, IdleSaver on the next idle stretch) applies as usual.
/// </para>
/// <para>
/// IdleSaver switches once the user has been away for the timeout and switches back as soon as they return. The idle
/// time comes from <see cref="IUserIdleSource"/> (in practice <see cref="ForegroundTracker"/>): the engine runs as a
/// service in session 0, where <c>GetLastInputInfo</c> never sees the user's input, so the UI on each desktop reports
/// it. Unknown idle time (no UI reporting) counts as "not idle": IdleSaver never acts on a guess.
/// </para>
/// <para>
/// <b>Crash recovery.</b> While a feature holds a plan other than the baseline, the baseline is also kept in
/// <see cref="IPowerPlanRecoveryStore"/>, written before the switch away from it and cleared once it is restored (or the
/// user's own change makes it moot). If the engine dies in between, the next start restores it, unless a start-with
/// plan is configured or the plan was changed since (Process Lasso's IdleSaverRecoveryGUID).
/// </para>
/// <para>Every switch is logged with its reason, to the engine log and to the Logs page (through
/// <see cref="IRuleStore"/>), and kept in <see cref="RecentChanges"/>.</para>
/// </summary>
public class PowerManager : IPowerManager
{
    /// <summary>How often idle time and CPU load are checked; also how late a switch back on the user's return can be.</summary>
    internal static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(1);
    private const int MaxRecentChanges = 100;

    private readonly ILogger<PowerManager> _logger;
    private readonly IUserIdleSource _idleSource;
    private readonly Func<Guid> _readActivePlan;
    private readonly Func<Guid, bool> _writeActivePlan;
    private readonly Func<double> _systemCpuPercent;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<Guid, string> _describePlan;
    private readonly IRuleStore? _ruleStore;
    private readonly IPowerPlanRecoveryStore _recovery;
    private readonly object _sync = new();
    private readonly LinkedList<PowerPlanChange> _recentChanges = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _stopped;

    // Who holds the plan: the plan they put on, the plan to go back to, and whether someone changed it by hand since.
    private PowerPlanOwner _owner;
    private Guid _applied;
    private Guid _baseline;
    private bool _userOverrode;
    private (PowerPlanOwner Owner, Guid Plan)? _failedSwitch;   // not retried until the decision changes
    private PendingPlanRestore? _recorded;   // what the recovery store holds, as far as this run knows

    // Performance Mode
    private Guid _performancePlan = Powrprof.HighPerformance;
    private bool _manualPerformanceMode;
    private bool _automaticPerformanceMode;
    private string? _automaticReason;
    private bool _automaticSuppressed;   // turned off by hand while a game ran: wait for the games to go

    // IdleSaver
    private bool _idleSaverEnabled;
    private int _idleTimeoutMinutes = 15;
    private Guid _idlePlanId = Powrprof.PowerSaver;
    private bool _idleRequested;

    // Load switcher
    private LoadPowerSwitcher? _loadSwitcher;

    private Guid _startupPlan;

    public PowerManager(ILogger<PowerManager> logger, IUserIdleSource idleSource, IRuleStore? ruleStore = null)
        : this(logger, idleSource, null, null, ruleStore: ruleStore)
    {
    }

    /// <summary>Tests replace the power-plan API, the CPU load, the clock and the recovery store, so they can run the
    /// state machine without touching the machine or the disk, or waiting.</summary>
    internal PowerManager(ILogger<PowerManager>? logger, IUserIdleSource idleSource,
        Func<Guid>? readActivePlan, Func<Guid, bool>? writeActivePlan,
        Func<double>? systemCpuPercent = null, Func<TimeSpan>? clock = null, IRuleStore? ruleStore = null,
        IPowerPlanRecoveryStore? recoveryStore = null)
    {
        _logger = logger ?? NullLogger<PowerManager>.Instance;
        _idleSource = idleSource;
        _readActivePlan = readActivePlan ?? ReadActiveScheme;
        _writeActivePlan = writeActivePlan ?? WriteActiveScheme;
        _systemCpuPercent = systemCpuPercent ?? SystemInfo.GetOverallCpuPercent;
        _clock = clock ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
        _ruleStore = ruleStore;
        // Reading names goes to powrprof; with a faked plan API, keep the log free of real lookups.
        _describePlan = readActivePlan == null ? PowerPlans.Describe : id => PowerPlans.WellKnownName(id) ?? id.ToString();
        // Likewise, a faked plan API never leaves a record on the real disk.
        _recovery = recoveryStore ?? (readActivePlan == null
            ? new FilePowerPlanRecoveryStore(_logger)
            : new MemoryPowerPlanRecoveryStore());
    }

    /// <summary>The plan changes made since the engine started, oldest first.</summary>
    public IReadOnlyList<PowerPlanChange> RecentChanges
    {
        get { lock (_sync) return _recentChanges.ToList(); }
    }

    /// <summary>Raised after every plan change the engine makes, with its reason.</summary>
    public event EventHandler<PowerPlanChange>? PlanChanged;

    public PowerPlanOwner Owner
    {
        get { lock (_sync) return _owner; }
    }

    /// <summary>Whether Performance Mode is on (manually or automatically). SmartTrim reads it to gate its standby list purge.</summary>
    public bool IsPerformanceModeActive
    {
        get { lock (_sync) return PerformanceModeWanted; }
    }

    private bool PerformanceModeWanted => _manualPerformanceMode || (_automaticPerformanceMode && !_automaticSuppressed);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Power Manager");
        lock (_sync)
        {
            _stopped = false;
            // Settings are applied before this, but nothing asks for a plan until the loop and the command pipe start,
            // so no switch in this run can have replaced the last run's record yet.
            RecoverFromLastRun();
            if (_startupPlan != Guid.Empty)
            {
                var current = _readActivePlan();
                if (current != _startupPlan && _writeActivePlan(_startupPlan))
                    Record(current, _startupPlan, PowerPlanOwner.None, "engine started (start with power plan)");
            }
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Power Manager, restoring original plan");
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        lock (_sync)
        {
            _stopped = true;
            Arbitrate("engine stopping");
        }
    }

    /// <summary>
    /// Restores the plan a previous run switched away from and never got to restore (it crashed or was killed), if
    /// that switch is still in effect. A configured start-with plan takes priority. Caller holds the lock.
    /// </summary>
    private void RecoverFromLastRun()
    {
        if (_recovery.Load() is not { } pending) return;
        _recorded = pending;
        _logger.LogWarning("The engine last stopped while {Applied} was on in place of the user's plan {Plan}",
            _describePlan(pending.Applied), _describePlan(pending.Restore));

        if (_startupPlan != Guid.Empty)
        {
            _logger.LogInformation("Not restoring it: the start-with plan {Plan} applies instead", _describePlan(_startupPlan));
            SetRecord(null);
            return;
        }

        var current = _readActivePlan();
        // As in Arbitrate, an unreadable plan is assumed unchanged.
        if (current != pending.Applied && current != Guid.Empty)
        {
            _logger.LogInformation("Not restoring it: the power plan was changed to {Plan} since", _describePlan(current));
            SetRecord(null);
            return;
        }
        if (current == pending.Restore)
        {
            SetRecord(null);
            return;
        }
        if (_writeActivePlan(pending.Restore))
        {
            Record(current, pending.Restore, PowerPlanOwner.None,
                "engine started; restored the plan from before the engine last stopped unexpectedly");
            SetRecord(null);
        }
        else
        {
            // Kept for the next start, and as the baseline if a feature takes over meanwhile (see Arbitrate).
            _logger.LogError("Failed to restore power plan {Plan} left over from the last run", _describePlan(pending.Restore));
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(IdleCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    SampleLoad();
                    CheckIdle();
                }
                catch (Exception ex) { _logger.LogError(ex, "Power plan check failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One IdleSaver step. Runs every <see cref="IdleCheckInterval"/>; tests call it directly.</summary>
    internal void CheckIdle()
    {
        lock (_sync)
        {
            var idle = _idleSaverEnabled ? _idleSource.GetIdleTime() : null;
            // Performance Mode disables IdleSaver outright, so it starts over once Performance Mode ends.
            var wanted = idle >= TimeSpan.FromMinutes(_idleTimeoutMinutes) && !PerformanceModeWanted && _idlePlanId != Guid.Empty;
            if (wanted == _idleRequested)
            {
                Arbitrate("periodic check");
                return;
            }

            _idleRequested = wanted;
            Arbitrate(wanted ? $"IdleSaver: no input for {(int)idle!.Value.TotalMinutes} min"
                : idle == null ? "IdleSaver: idle time unknown" : "IdleSaver: user input");
        }
    }

    /// <summary>One load-switcher step: adds a CPU sample. Runs every <see cref="IdleCheckInterval"/>; tests call it directly.</summary>
    internal void SampleLoad()
    {
        lock (_sync)
        {
            if (_loadSwitcher is not { } switcher) return;
            var change = switcher.AddSample(_systemCpuPercent(), _clock());
            if (change != null)
            {
                var deferred = switcher.Level == LoadPowerSwitcher.LoadLevel.Low && switcher.RequestedPlan == null;
                Arbitrate($"Load switcher: {change}{(deferred ? " (low plan deferred)" : "")}");
            }
        }
    }

    /// <summary>What the features want, highest precedence first.</summary>
    private (PowerPlanOwner Owner, Guid Plan) Decide()
    {
        if (_stopped) return (PowerPlanOwner.None, Guid.Empty);
        if (PerformanceModeWanted)
            return (PowerPlanOwner.PerformanceMode, _performancePlan == Guid.Empty ? Powrprof.HighPerformance : _performancePlan);
        if (_idleRequested) return (PowerPlanOwner.IdleSaver, _idlePlanId);
        if (_loadSwitcher?.RequestedPlan is { } loadPlan) return (PowerPlanOwner.LoadSwitcher, loadPlan);
        return (PowerPlanOwner.None, Guid.Empty);
    }

    /// <summary>
    /// The single place features' plan changes happen: applies the winning feature's plan, or restores the baseline
    /// when none wants one. Caller holds the lock; <paramref name="reason"/> is what just changed.
    /// </summary>
    private void Arbitrate(string reason)
    {
        var (owner, plan) = Decide();
        var current = _readActivePlan();

        // An unreadable plan is assumed unchanged: leaving the user on a plan they didn't pick is worse than the rare
        // chance of undoing a change we couldn't see.
        if (_owner != PowerPlanOwner.None && !_userOverrode && current != Guid.Empty && current != _applied)
        {
            _userOverrode = true;
            _baseline = current;
            _logger.LogInformation("Power plan was changed to {Plan} while {Owner} held it; leaving the user's choice",
                _describePlan(current), _owner);
            UpdateRecord();
        }

        if (owner == _owner)
        {
            // Same feature, same decision: nothing to do (also when the user overrode it).
            if (owner == PowerPlanOwner.None || plan == _applied || _failedSwitch == (owner, plan)) return;
            // Same feature, new decision (the load switcher changed level): it applies even over a manual change.
            Switch(current, owner, plan, reason);
            return;
        }

        if (owner == PowerPlanOwner.None)
        {
            var previous = _owner;
            var overridden = _userOverrode;
            _owner = PowerPlanOwner.None;
            _userOverrode = false;
            _failedSwitch = null;
            if (overridden || _baseline == Guid.Empty || _baseline == current)
            {
                UpdateRecord();
                return;
            }
            if (_writeActivePlan(_baseline))
            {
                Record(current, _baseline, previous, $"{reason}; restored the plan from before {Describe(previous)}");
                UpdateRecord();
            }
            else
            {
                // The record stays, so the next start (or the next feature to take over) still knows the user's plan.
                _logger.LogError("Failed to restore power plan {Plan} after {Owner} ended", _describePlan(_baseline), previous);
            }
            return;
        }

        // Another feature takes over. Coming from no owner, the current plan is the one to come back to later; from
        // another feature, the baseline carries over (the user's own plan, or the one they picked meanwhile).
        if (_owner == PowerPlanOwner.None)
        {
            if (current == Guid.Empty)
            {
                _logger.LogWarning("{Owner}: can't read the active power plan, so not switching (it couldn't be restored)", owner);
                return;
            }
            // A restore still owed (it failed, in this run or the last) means this is a feature's plan, not the user's.
            _baseline = _recorded is { } owed && owed.Applied == current ? owed.Restore : current;
        }
        _owner = owner;
        _userOverrode = false;
        _failedSwitch = null;
        Switch(current, owner, plan, reason);
    }

    private void Switch(Guid current, PowerPlanOwner owner, Guid plan, string reason)
    {
        _applied = plan;
        // Recorded before the switch, so there is no moment the engine could die on the new plan with no record of it.
        UpdateRecord();
        if (plan == current) return;
        if (_writeActivePlan(plan))
        {
            _failedSwitch = null;
            Record(current, plan, owner, reason);
        }
        else
        {
            // Carry on as if the feature had kept the current plan, so this isn't mistaken for a manual change.
            _applied = current;
            _failedSwitch = (owner, plan);
            _logger.LogError("{Owner}: failed to switch power plan to {Plan}", owner, _describePlan(plan));
            UpdateRecord();
        }
    }

    /// <summary>
    /// Brings the recovery record in line with the state: a restore is owed while a feature holds a plan other than the
    /// baseline and the user hasn't overridden it. Caller holds the lock.
    /// </summary>
    private void UpdateRecord() =>
        SetRecord(_owner != PowerPlanOwner.None && !_userOverrode && _applied != _baseline
                  && _applied != Guid.Empty && _baseline != Guid.Empty
            ? new PendingPlanRestore(_applied, _baseline)
            : null);

    /// <summary>Writes the record only when it changes, since <see cref="Arbitrate"/> runs every second.</summary>
    private void SetRecord(PendingPlanRestore? pending)
    {
        if (pending == _recorded) return;
        if (pending is { } p) _recovery.Save(p);
        else _recovery.Clear();
        _recorded = pending;
    }

    private void Record(Guid from, Guid to, PowerPlanOwner owner, string reason)
    {
        var change = new PowerPlanChange(DateTime.Now, from, to, owner, reason);
        _recentChanges.AddLast(change);
        while (_recentChanges.Count > MaxRecentChanges) _recentChanges.RemoveFirst();
        var message = $"{_describePlan(from)} -> {_describePlan(to)}: {reason}";
        _logger.LogInformation("Power plan {Change}", message);
        // The Logs page reads the action log. IsRestored keeps it out of the dashboard's count of restrained processes.
        _ = _ruleStore?.LogProBalanceActionAsync(new ProBalanceAction
        {
            Timestamp = DateTime.UtcNow,
            ProcessName = ProBalanceAction.PowerPlanEntryName,
            Reason = message,
            IsRestored = true,
        });
        try { PlanChanged?.Invoke(this, change); }
        catch (Exception ex) { _logger.LogError(ex, "PlanChanged handler failed"); }
    }

    private static string Describe(PowerPlanOwner owner) => owner switch
    {
        PowerPlanOwner.PerformanceMode => "Performance Mode",
        PowerPlanOwner.IdleSaver => "IdleSaver",
        PowerPlanOwner.LoadSwitcher => "the load switcher",
        _ => "it",
    };

    /// <inheritdoc/>
    public IEnumerable<(Guid Id, string Name)> GetAvailablePlans() =>
        PowerPlans.Enumerate().Select(p => (p.Id, p.Name)).ToList();

    /// <inheritdoc/>
    public Guid GetActivePlan() => _readActivePlan();

    /// <inheritdoc/>
    public void SetActivePlan(Guid planId)
    {
        lock (_sync)
        {
            var current = _readActivePlan();
            if (!_writeActivePlan(planId)) return;
            Record(current, planId, PowerPlanOwner.None, "set by the user");
            // A manual choice: whatever feature holds the plan must not restore over it.
            if (_owner != PowerPlanOwner.None)
            {
                _userOverrode = true;
                _baseline = planId;
                UpdateRecord();
            }
        }
    }

    private Guid ReadActiveScheme()
    {
        try
        {
            if (Powrprof.PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) == 0 && ptr != IntPtr.Zero)
            {
                var guid = (Guid)Marshal.PtrToStructure(ptr, typeof(Guid))!;
                Marshal.FreeHGlobal(ptr);
                return guid;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active power scheme");
        }
        return Guid.Empty;
    }

    private bool WriteActiveScheme(Guid planId)
    {
        try
        {
            var result = Powrprof.PowerSetActiveScheme(IntPtr.Zero, ref planId);
            if (result == 0) return true;
            _logger.LogError("Failed to set power plan. Error: {Error}", result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set active power scheme");
        }
        return false;
    }

    /// <inheritdoc/>
    public void EnablePerformanceMode(bool enable)
    {
        lock (_sync)
        {
            var wasOn = PerformanceModeWanted;
            _manualPerformanceMode = enable;
            // Turned off by hand while a game runs: stay off until the games have gone, rather than coming straight back.
            if (!enable && _automaticPerformanceMode) _automaticSuppressed = true;
            if (PerformanceModeWanted == wasOn) return;

            if (enable) _idleRequested = false;
            _logger.LogInformation("Performance Mode {State}", enable ? "ENABLED" : "DISABLED");
            Arbitrate(enable ? "Performance Mode turned on" : "Performance Mode turned off");
        }
    }

    /// <inheritdoc/>
    public void SetAutomaticPerformanceMode(bool active, string? reason)
    {
        lock (_sync)
        {
            if (active == _automaticPerformanceMode && (!active || reason == _automaticReason)) return;
            var wasOn = PerformanceModeWanted;
            _automaticPerformanceMode = active;
            _automaticReason = active ? reason : null;
            if (!active) _automaticSuppressed = false;

            _logger.LogInformation("Automatic Performance Mode {State}{Reason}", active ? "engaged" : "released",
                reason == null ? "" : $": {reason}");
            if (PerformanceModeWanted == wasOn) return;
            if (active) _idleRequested = false;
            Arbitrate(active ? $"Performance Mode engaged automatically: {reason}"
                : $"Performance Mode released{(reason == null ? "" : $": {reason}")}");
        }
    }

    /// <inheritdoc/>
    public void ConfigurePerformanceMode(Guid targetPlan)
    {
        lock (_sync)
        {
            if (targetPlan == _performancePlan) return;
            _performancePlan = targetPlan;
            Arbitrate("Performance Mode plan changed");
        }
    }

    /// <inheritdoc/>
    public void ConfigureIdleSaver(bool enable, int timeoutMinutes, Guid idlePlanId)
    {
        lock (_sync)
        {
            // Undo a switch made under the old settings; the next check applies the new ones if the user is still away.
            _idleRequested = false;
            _idleSaverEnabled = enable;
            _idleTimeoutMinutes = Math.Max(1, timeoutMinutes);
            _idlePlanId = idlePlanId;
            Arbitrate(enable ? "IdleSaver settings changed" : "IdleSaver disabled");
        }
        _logger.LogInformation("IdleSaver configured: Enabled={Enabled}, Timeout={Timeout}min, Plan={Plan}",
            enable, timeoutMinutes, idlePlanId);
    }

    /// <inheritdoc/>
    public void ConfigureLoadSwitcher(LoadSwitcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_sync)
        {
            if (_loadSwitcher?.Options == options) return;

            var error = options.Enabled ? options.Validate() : null;
            if (error != null)
                _logger.LogError("Load-based power switcher disabled: {Error}", error);
            // Start over: the old decision was made under other thresholds or plans.
            _loadSwitcher = options.Enabled && error == null ? new LoadPowerSwitcher(options) : null;
            Arbitrate(_loadSwitcher == null ? "load switcher disabled" : "load switcher settings changed");
        }
        _logger.LogInformation(
            "Load switcher configured: Enabled={Enabled}, Low={Low}% High={High}%, Window={Samples} samples, MinTime={Min}ms, DeferLowPlan={Defer}",
            options.Enabled, options.LowThreshold, options.HighThreshold, options.AveragingWindowSamples,
            options.MinimumTimeInPlanMs, options.DeferLowPlan);
    }

    /// <inheritdoc/>
    public void ConfigureStartupPlan(Guid planId)
    {
        lock (_sync) _startupPlan = planId;
    }
}
