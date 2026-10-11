using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>Affinity mask and creation time of a live process, read through one handle.</summary>
public readonly record struct ProcessAffinityState(long Mask, DateTime StartTimeUtc);

/// <summary>
/// The live-process affinity reads and writes the CPU Limiter needs, behind an interface so its timing rules can
/// be tested against fake processes.
/// </summary>
public interface IProcessAffinityAccess
{
    /// <summary>
    /// Null if the process is gone, access was denied, or its threads span several processor groups (the
    /// single-group affinity APIs can't describe or change those).
    /// </summary>
    ProcessAffinityState? Query(int pid);

    /// <summary>
    /// Sets the affinity only if <paramref name="pid"/> is still the process created at
    /// <paramref name="startTimeUtc"/>, so a recycled PID is never touched. False if it isn't, or the call failed.
    /// </summary>
    bool TrySetAffinityMask(int pid, DateTime startTimeUtc, long mask);
}

public sealed class Win32ProcessAffinityAccess : IProcessAffinityAccess
{
    public ProcessAffinityState? Query(int pid)
    {
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try { return Read(handle); }
        finally { Kernel32.CloseHandle(handle); }
    }

    public bool TrySetAffinityMask(int pid, DateTime startTimeUtc, long mask)
    {
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation | ProcessAccessFlags.SetInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var state = Read(handle);
            if (state == null || state.Value.StartTimeUtc != startTimeUtc) return false;
            // Fails for a process whose threads span processor groups; the caller treats that as "leave it".
            return Kernel32.SetProcessAffinityMask(handle, new IntPtr(mask));
        }
        finally { Kernel32.CloseHandle(handle); }
    }

    private static ProcessAffinityState? Read(IntPtr handle)
    {
        if (!Kernel32.GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
        // Both masks come back zero when the process has threads in more than one processor group.
        if (!Kernel32.GetProcessAffinityMask(handle, out var processMask, out _) || processMask == IntPtr.Zero) return null;
        return new ProcessAffinityState(processMask.ToInt64(), DateTime.FromFileTimeUtc((long)created.ToUInt64()));
    }
}

/// <summary>
/// Process Lasso's CPU Limiter: when a process matching a rule uses at least the rule's CPU percent for the
/// rule's time, take N cores away from its affinity for a while, then put the original affinity back. This is
/// the "affinity reduction" mode; the job-object hard cap (<see cref="CpuLimiter"/>) is a separate mode.
/// </summary>
public sealed class CpuAffinityLimiter : IDisposable
{
    private readonly ILogger<CpuAffinityLimiter> _logger;
    private readonly IProcessMonitor _processMonitor;
    private readonly IRuleStore _ruleStore;
    private readonly IProcessAffinityAccess _access;
    private readonly Func<long> _nowMs;
    private readonly object _gate = new();
    private readonly Dictionary<int, long> _overThresholdSince = new();
    private readonly Dictionary<int, Constraint> _constrained = new();
    private Timer? _timer;

    private sealed record Constraint(string Name, DateTime StartTimeUtc, long Original, long Applied, long UntilMs);

    /// <summary>How often usage is evaluated, in milliseconds.</summary>
    public int EvaluationIntervalMs { get; set; } = 1000;

    public int ActiveConstraints { get { lock (_gate) return _constrained.Count; } }

    /// <summary>The processes whose affinity this limiter has reduced right now.</summary>
    public IReadOnlyCollection<int> GetConstrainedPids()
    {
        lock (_gate) return _constrained.Keys.ToList();
    }

    public CpuAffinityLimiter(
        ILogger<CpuAffinityLimiter> logger,
        IProcessMonitor processMonitor,
        IRuleStore ruleStore,
        IProcessAffinityAccess? access = null,
        Func<long>? clockMs = null)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _ruleStore = ruleStore;
        _access = access ?? new Win32ProcessAffinityAccess();
        _nowMs = clockMs ?? (() => Environment.TickCount64);
        _processMonitor.ProcessExited += OnProcessExited;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting CPU Limiter (affinity reduction)");
        _timer = new Timer(Evaluate, null, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(Math.Max(100, EvaluationIntervalMs)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping CPU Limiter (affinity reduction)");
        _timer?.Change(Timeout.Infinite, 0);
        RestoreAll("CPU Limiter stopped");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _processMonitor.ProcessExited -= OnProcessExited;
        _timer?.Dispose();
    }

    /// <summary>
    /// <paramref name="mask"/> with its highest <paramref name="cores"/> set bits cleared, always leaving at
    /// least one. Returns the mask unchanged when it has a single core (or none).
    /// </summary>
    public static long RemoveTopCores(long mask, int cores, out int removed)
    {
        removed = 0;
        var result = (ulong)mask;
        var available = BitOperations.PopCount(result);
        var toRemove = Math.Min(Math.Max(0, cores), available - 1);
        for (; removed < toRemove; removed++)
            result &= ~(1UL << (63 - BitOperations.LeadingZeroCount(result)));
        return (long)result;
    }

    internal void Evaluate(object? state)
    {
        try
        {
            lock (_gate)
            {
                var now = _nowMs();
                var byPid = new Dictionary<int, ProcessInfo>();
                foreach (var p in _processMonitor.GetAllProcesses()) byPid[p.Pid] = p;

                UpdateConstraints(byPid, now);
                ConstrainHogs(byPid.Values, now);

                foreach (var pid in _overThresholdSince.Keys.Where(pid => !byPid.ContainsKey(pid)).ToList())
                    _overThresholdSince.Remove(pid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in CPU Limiter evaluation loop");
        }
    }

    private void OnProcessExited(object? sender, int pid)
    {
        lock (_gate)
        {
            _overThresholdSince.Remove(pid);
            if (_constrained.Remove(pid, out var c))
                _logger.LogInformation("CPU Limiter constraint of {Name} ({Pid}) ended: process exited", c.Name, pid);
        }
    }

    private void UpdateConstraints(Dictionary<int, ProcessInfo> byPid, long now)
    {
        foreach (var (pid, c) in _constrained.ToList())
        {
            var current = byPid.ContainsKey(pid) ? _access.Query(pid) : null;
            if (current == null || current.Value.StartTimeUtc != c.StartTimeUtc)
            {
                // Exited (or the PID now belongs to another process): never write the old mask onto it.
                _constrained.Remove(pid);
                _logger.LogInformation("CPU Limiter constraint of {Name} ({Pid}) ended: process exited", c.Name, pid);
                continue;
            }

            if (current.Value.Mask != c.Applied)
            {
                // Someone else (usually the user) set an affinity while we held it down; theirs wins.
                _constrained.Remove(pid);
                _logger.LogInformation("CPU Limiter left {Name} ({Pid}) at 0x{Mask:X}: affinity changed externally during the limit",
                    c.Name, pid, current.Value.Mask);
                continue;
            }

            if (now >= c.UntilMs)
                Restore(pid, "limit period ended");
            else if (_ruleStore.GetProcessRule(c.Name)?.CpuLimitThresholdPercent == null)
                Restore(pid, "rule removed");
        }
    }

    private void ConstrainHogs(IEnumerable<ProcessInfo> processes, long now)
    {
        foreach (var p in processes)
        {
            if (p.Pid <= 4 || _constrained.ContainsKey(p.Pid)) continue;

            var rule = _ruleStore.GetProcessRule(p.Name);
            var threshold = rule?.CpuLimitThresholdPercent;

            // The over-threshold run must be unbroken: any sample below it starts the wait over.
            if (rule == null || threshold == null || p.CpuPercent < threshold.Value)
            {
                _overThresholdSince.Remove(p.Pid);
                continue;
            }

            if (!_overThresholdSince.TryGetValue(p.Pid, out var since))
                _overThresholdSince[p.Pid] = since = now;

            var overMs = 1000L * Math.Max(0, rule.CpuLimitOverThresholdSeconds ?? CpuAffinityLimitDefaults.OverThresholdSeconds);
            if (now - since >= overMs)
            {
                _overThresholdSince.Remove(p.Pid);
                TryConstrain(p, rule, now);
            }
        }
    }

    private void TryConstrain(ProcessInfo p, ProcessRule rule, long now)
    {
        var current = _access.Query(p.Pid);
        if (current == null) return; // gone, denied, or spread over several processor groups

        var cores = Math.Max(1, rule.CpuLimitCoresToRemove ?? CpuAffinityLimitDefaults.CoresToRemove);
        var newMask = RemoveTopCores(current.Value.Mask, cores, out var removed);
        if (removed == 0) return; // already down to one core

        var durationMs = 1000L * Math.Max(1, rule.CpuLimitDurationSeconds ?? CpuAffinityLimitDefaults.DurationSeconds);
        if (!_access.TrySetAffinityMask(p.Pid, current.Value.StartTimeUtc, newMask))
        {
            _logger.LogDebug("CPU Limiter could not constrain {Name} ({Pid})", p.Name, p.Pid);
            return;
        }

        _constrained[p.Pid] = new Constraint(p.Name, current.Value.StartTimeUtc, current.Value.Mask, newMask, now + durationMs);
        // Same wording as Process Lasso's log line.
        var message = $"Constraining CPU affinity by {removed} CPUs for {durationMs} ms. " +
                      $"New CPU affinity is 0x{newMask:X}, original was 0x{current.Value.Mask:X}";
        LogAction(p.Name, p.Pid, $"{message} (CPU usage {p.CpuPercent:F1}%)", restored: false);
        _logger.LogInformation("CPU Limiter: {Name} ({Pid}): {Message}", p.Name, p.Pid, message);
    }

    private void RestoreAll(string reason)
    {
        lock (_gate)
        {
            foreach (var pid in _constrained.Keys.ToList())
            {
                var c = _constrained[pid];
                var current = _access.Query(pid);
                if (current?.StartTimeUtc == c.StartTimeUtc && current.Value.Mask == c.Applied)
                    Restore(pid, reason);
                else
                    _constrained.Remove(pid);
            }
            _overThresholdSince.Clear();
        }
    }

    /// <summary>Puts the original affinity back. Callers have already checked nobody else changed it.</summary>
    private void Restore(int pid, string reason)
    {
        if (!_constrained.Remove(pid, out var c)) return;

        if (_access.TrySetAffinityMask(pid, c.StartTimeUtc, c.Original))
        {
            LogAction(c.Name, pid, $"Restored CPU affinity to 0x{c.Original:X}: {reason}", restored: true);
            _logger.LogInformation("CPU Limiter restored {Name} ({Pid}) to 0x{Mask:X}: {Reason}", c.Name, pid, c.Original, reason);
        }
        else
        {
            _logger.LogDebug("CPU Limiter could not restore {Name} ({Pid}) to 0x{Mask:X}", c.Name, pid, c.Original);
        }
    }

    // ProBalanceAction has no affinity columns; the priority fields stay 0 and the reason carries the masks.
    private void LogAction(string name, int pid, string reason, bool restored)
    {
        _ = _ruleStore.LogProBalanceActionAsync(new ProBalanceAction
        {
            Timestamp = DateTime.UtcNow,
            ProcessName = name,
            Pid = pid,
            Reason = reason,
            IsRestored = restored
        });
    }
}
