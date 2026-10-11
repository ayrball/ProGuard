using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// Manages Windows power plans, Performance Mode, and IdleSaver.
/// <para>
/// IdleSaver switches to the idle plan once the user has been away for the timeout and switches back as soon as
/// they return. The idle time comes from <see cref="IUserIdleSource"/> (in practice <see cref="ForegroundTracker"/>):
/// the engine runs as a service in session 0, where <c>GetLastInputInfo</c> never sees the user's input, so the UI
/// on each desktop reports it alongside the foreground process, and the engine only reads it itself when it runs
/// on a user's desktop. Unknown idle time (no UI reporting) counts as "not idle": IdleSaver never acts on a guess.
/// </para>
/// <para>
/// Precedence: Performance Mode beats IdleSaver. While it is on, IdleSaver doesn't switch; turning it on while the
/// idle plan is active takes over the pending restore, so turning it off later goes back to the plan from before
/// either of them. And neither ever restores over a plan someone changed in the meantime: a restore only happens
/// while the plan we switched to is still the active one.
/// </para>
/// </summary>
public class PowerManager : IPowerManager
{
    /// <summary>How often the idle time is checked; also how late a switch back on the user's return can be.</summary>
    internal static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger<PowerManager> _logger;
    private readonly IUserIdleSource _idleSource;
    private readonly Func<Guid> _readActivePlan;
    private readonly Func<Guid, bool> _writeActivePlan;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _idleLoop;

    // Performance Mode: the plan to go back to when it ends.
    private Guid _originalPlan;
    private bool _performanceModeActive;

    private bool _idleSaverEnabled;
    private int _idleTimeoutMinutes = 15;
    private Guid _idlePlanId = Powrprof.PowerSaver;

    // IdleSaver state. A "stretch" is one continuous period of idleness past the timeout; IdleSaver acts at most once
    // per stretch, so after someone changes the plan by hand mid-stretch it doesn't switch straight back.
    private bool _idleStretchHandled;
    private bool _idleSwitched;          // we put the idle plan on and owe a restore
    private Guid _idlePlanApplied;       // the plan we switched to
    private Guid _planBeforeIdle;        // the plan to restore

    public PowerManager(ILogger<PowerManager> logger, IUserIdleSource idleSource)
        : this(logger, idleSource, null, null)
    {
    }

    /// <summary>Tests replace the power-plan API so they can run the state machine without touching the machine.</summary>
    internal PowerManager(ILogger<PowerManager>? logger, IUserIdleSource idleSource,
        Func<Guid>? readActivePlan, Func<Guid, bool>? writeActivePlan)
    {
        _logger = logger ?? NullLogger<PowerManager>.Instance;
        _idleSource = idleSource;
        _readActivePlan = readActivePlan ?? ReadActiveScheme;
        _writeActivePlan = writeActivePlan ?? WriteActiveScheme;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Power Manager");
        lock (_sync) _originalPlan = GetActivePlan();
        _cts = new CancellationTokenSource();
        _idleLoop = Task.Run(() => IdleLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Power Manager, restoring original plan");
        _cts?.Cancel();
        if (_idleLoop != null)
        {
            try { await _idleLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        lock (_sync)
        {
            if (_idleSwitched) RestoreFromIdle("engine stopping");
            if (_performanceModeActive)
            {
                RestoreIfStill(Powrprof.HighPerformance, _originalPlan, "Performance Mode");
                _performanceModeActive = false;
            }
        }
    }

    private async Task IdleLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(IdleCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try { CheckIdle(); }
                catch (Exception ex) { _logger.LogError(ex, "IdleSaver check failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One step of the IdleSaver state machine. Runs every <see cref="IdleCheckInterval"/>; tests call it directly.</summary>
    internal void CheckIdle()
    {
        lock (_sync)
        {
            var idle = _idleSaverEnabled ? _idleSource.GetIdleTime() : null;
            if (!(idle >= TimeSpan.FromMinutes(_idleTimeoutMinutes)))
            {
                // The user is back (or we can no longer tell, e.g. their UI closed): the stretch is over.
                _idleStretchHandled = false;
                if (_idleSwitched) RestoreFromIdle(idle == null ? "idle time unknown" : "user input");
                return;
            }

            // Performance Mode wins; once it ends, IdleSaver gets its turn if the user is still away.
            if (_idleStretchHandled || _performanceModeActive) return;
            _idleStretchHandled = true;

            var current = _readActivePlan();
            if (_idlePlanId == Guid.Empty || current == _idlePlanId) return;  // nothing to do, and nothing to restore
            if (current == Guid.Empty)
            {
                _logger.LogWarning("IdleSaver: can't read the active power plan, so not switching (it couldn't be restored)");
                return;
            }
            if (!_writeActivePlan(_idlePlanId)) return;

            _idleSwitched = true;
            _idlePlanApplied = _idlePlanId;
            _planBeforeIdle = current;
            _logger.LogInformation("IdleSaver: idle for {Idle}, switched power plan {From} -> {To}", idle, current, _idlePlanId);
        }
    }

    /// <summary>Ends the idle switch, going back to the earlier plan unless someone changed it since. Caller holds the lock.</summary>
    private void RestoreFromIdle(string reason)
    {
        _idleSwitched = false;
        RestoreIfStill(_idlePlanApplied, _planBeforeIdle, $"IdleSaver ({reason})");
    }

    /// <summary>Switches to <paramref name="restore"/> only while <paramref name="ourPlan"/> is still active.</summary>
    private void RestoreIfStill(Guid ourPlan, Guid restore, string what)
    {
        var current = _readActivePlan();
        // An unreadable plan is assumed unchanged: leaving the user on a power-saving (or performance) plan they
        // didn't pick is worse than the rare chance of undoing a change we couldn't see.
        if (current != ourPlan && current != Guid.Empty)
        {
            _logger.LogInformation("{What}: power plan was changed to {Plan} in the meantime, leaving it", what, current);
            return;
        }
        if (restore == Guid.Empty || restore == current) return;
        if (_writeActivePlan(restore))
            _logger.LogInformation("{What}: restored power plan {Plan}", what, restore);
    }

    /// <inheritdoc/>
    public IEnumerable<(Guid Id, string Name)> GetAvailablePlans()
    {
        var plans = new List<(Guid, string)>();
        // Add well-known plans
        plans.Add((Powrprof.HighPerformance, "High Performance"));
        plans.Add((new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), "Balanced"));
        plans.Add((Powrprof.PowerSaver, "Power Saver"));

        _logger.LogDebug("Returning {Count} power plans", plans.Count);
        return plans;
    }

    /// <inheritdoc/>
    public Guid GetActivePlan() => _readActivePlan();

    /// <inheritdoc/>
    public void SetActivePlan(Guid planId) => _writeActivePlan(planId);

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
            if (result == 0)
            {
                _logger.LogInformation("Set active power plan to {Plan}", planId);
                return true;
            }
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
            if (enable && !_performanceModeActive)
            {
                var current = _readActivePlan();
                // Take over IdleSaver's pending restore, so ending Performance Mode goes back to the user's own plan
                // rather than the idle plan (unless the plan was changed by hand since IdleSaver switched).
                _originalPlan = _idleSwitched && current == _idlePlanApplied ? _planBeforeIdle : current;
                _idleSwitched = false;
                _writeActivePlan(Powrprof.HighPerformance);
                _performanceModeActive = true;
                _logger.LogInformation("Performance Mode ENABLED");
            }
            else if (!enable && _performanceModeActive)
            {
                RestoreIfStill(Powrprof.HighPerformance, _originalPlan, "Performance Mode");
                _performanceModeActive = false;
                _logger.LogInformation("Performance Mode DISABLED");
            }
        }
    }

    /// <inheritdoc/>
    public void ConfigureIdleSaver(bool enable, int timeoutMinutes, Guid idlePlanId)
    {
        lock (_sync)
        {
            // Undo a switch made under the old settings; the next check applies the new ones if the user is still away.
            if (_idleSwitched) RestoreFromIdle(enable ? "settings changed" : "disabled");
            _idleStretchHandled = false;

            _idleSaverEnabled = enable;
            _idleTimeoutMinutes = Math.Max(1, timeoutMinutes);
            _idlePlanId = idlePlanId;
        }
        _logger.LogInformation("IdleSaver configured: Enabled={Enabled}, Timeout={Timeout}min, Plan={Plan}",
            enable, timeoutMinutes, idlePlanId);
    }
}
