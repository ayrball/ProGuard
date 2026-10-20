using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Models;

namespace ProGuard.Engine.Services;

/// <summary>
/// Startup boost (Process Lasso's StartupBoostRules): raises a matching new process's priority class for
/// <see cref="ProcessRule.StartupBoostSeconds"/> after it starts, then puts back the priority it had. If
/// something else changed the priority during the boost, that change wins and the process is left alone.
/// </summary>
public sealed class StartupBoostService : IDisposable
{
    public const int MinSeconds = 1;
    public const int MaxSeconds = 3600;
    public const int DefaultSeconds = 10;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<StartupBoostService> _logger;
    private readonly IRuleProcessAccess _access;
    private readonly TimeProvider _time;
    private ITimer? _timer;

    // Boost is called from the monitor's event threads, Tick from the timer, Stop from the engine.
    private readonly object _gate = new();
    private readonly Dictionary<int, Boost> _boosts = new();

    private sealed record Boost(string Name, ProcessPriorityClass Original, ProcessPriorityClass Applied, DateTimeOffset Until);

    public StartupBoostService(ILogger<StartupBoostService> logger, IRuleProcessAccess? access = null, TimeProvider? time = null)
    {
        _logger = logger;
        _access = access ?? new Win32RuleProcessAccess();
        _time = time ?? TimeProvider.System;
    }

    public int ActiveBoostCount { get { lock (_gate) return _boosts.Count; } }

    /// <summary>Whether <paramref name="pid"/> is boosted right now (its priority is the boost's until it ends).</summary>
    public bool IsBoosting(int pid) { lock (_gate) return _boosts.ContainsKey(pid); }

    public void Start()
    {
        _timer ??= _time.CreateTimer(_ => SafeTick(), null, CheckInterval, CheckInterval);
    }

    /// <summary>Stops the timer and ends every active boost now, so nothing stays boosted after the engine stops.</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        Expire(endAll: true);
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Boosts <paramref name="process"/> if <paramref name="rule"/> asks for it and the process is still
    /// inside its boost window (measured from its start time, so processes that were already running when
    /// the engine started are not boosted). Returns true if a boost was applied.
    /// </summary>
    public bool TryBoost(ProcessInfo process, ProcessRule rule)
    {
        if (rule.StartupBoostPriorityClass is not int value) return false;
        var target = (ProcessPriorityClass)value;
        if (!Enum.IsDefined(target)) return false;
        if (CriticalProcesses.IsCriticalName(process.Name)) return false;

        int seconds = Math.Clamp(rule.StartupBoostSeconds ?? DefaultSeconds, MinSeconds, MaxSeconds);
        var now = _time.GetUtcNow();
        var started = _access.GetStartTimeUtc(process.Pid) is DateTime s ? new DateTimeOffset(s, TimeSpan.Zero) : now;
        var until = started + TimeSpan.FromSeconds(seconds);
        if (until <= now) return false;

        ProcessPriorityClass? original;
        lock (_gate)
        {
            if (_boosts.ContainsKey(process.Pid)) return false;

            original = _access.GetPriorityClass(process.Pid);
            if (original == null || original == target) return false;
            if (!_access.SetPriorityClass(process.Pid, target))
            {
                _logger.LogDebug("Startup boost: could not raise {Name} ({Pid}) to {Priority}", process.Name, process.Pid, target);
                return false;
            }

            _boosts[process.Pid] = new Boost(process.Name, original.Value, target, until);
        }

        _logger.LogInformation("Startup boost: {Name} ({Pid}) raised from {Original} to {Priority} for {Seconds}s",
            process.Name, process.Pid, original, target, (int)Math.Ceiling((until - now).TotalSeconds));
        return true;
    }

    /// <summary>Ends the boosts whose time is up. Called by the timer; public for tests.</summary>
    public void Tick() => Expire(endAll: false);

    public void OnProcessExited(int pid)
    {
        lock (_gate) _boosts.Remove(pid);
    }

    private void SafeTick()
    {
        // Timer-thread entry point: an exception escaping it would take the engine down.
        try { Tick(); }
        catch (Exception ex) { _logger.LogError(ex, "Startup boost check failed"); }
    }

    private void Expire(bool endAll)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var (pid, boost) in _boosts.Where(b => endAll || b.Value.Until <= now).ToList())
            {
                _boosts.Remove(pid);

                var current = _access.GetPriorityClass(pid);
                if (current == null) continue; // gone

                if (current != boost.Applied)
                {
                    _logger.LogInformation("Startup boost: priority of {Name} ({Pid}) was changed to {Current} during the boost, leaving it",
                        boost.Name, pid, current);
                }
                else if (_access.SetPriorityClass(pid, boost.Original))
                {
                    _logger.LogInformation("Startup boost: {Name} ({Pid}) restored to {Original}", boost.Name, pid, boost.Original);
                }
                else
                {
                    _logger.LogDebug("Startup boost: could not restore {Name} ({Pid}) to {Original}", boost.Name, pid, boost.Original);
                }
            }
        }
    }
}
