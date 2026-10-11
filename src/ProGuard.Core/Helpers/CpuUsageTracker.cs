using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ProGuard.Core.Helpers;

/// <summary>
/// Tracks per-process CPU usage by measuring CPU time deltas between polling intervals.
/// Thread-safe for concurrent access.
/// </summary>
public class CpuUsageTracker
{
    private readonly ConcurrentDictionary<int, (TimeSpan PrevCpuTime, DateTime PrevTimestamp)> _snapshots = new();
    private static readonly int _processorCount = Environment.ProcessorCount;

    /// <summary>
    /// A process first seen within this long of starting gets its average usage since it started as its first
    /// reading, rather than 0; older ones (e.g. everything running when monitoring begins) still read 0 first.
    /// </summary>
    public static readonly TimeSpan LifetimeSeedWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the CPU usage percentage for a process since the last call.
    /// Returns 0 on the first call for any given process, unless <paramref name="seedFromLifetime"/> is set and the
    /// process started within <see cref="LifetimeSeedWindow"/>: then its usage since it started. Without that, a new
    /// CPU hog would show 0% for a whole polling interval and ProBalance would act on it a second later than Process
    /// Lasso does.
    /// </summary>
    public double GetCpuUsage(Process process, bool seedFromLifetime = false)
    {
        try
        {
            DateTime? startedUtc = null;
            if (seedFromLifetime && !_snapshots.ContainsKey(process.Id))
            {
                try { startedUtc = process.StartTime.ToUniversalTime(); } catch { /* access denied: no seed */ }
            }
            return GetCpuUsage(process.Id, process.TotalProcessorTime, startedUtc);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Same as <see cref="GetCpuUsage(Process, bool)"/> for callers that already read the process's total CPU time
    /// (for example through <see cref="ProcessProbe"/>, which does not throw for protected processes). Pass
    /// <paramref name="startedUtc"/> to seed a just-started process's first reading from its lifetime.
    /// </summary>
    public double GetCpuUsage(int pid, TimeSpan totalProcessorTime, DateTime? startedUtc = null)
    {
        try
        {
            var currentCpuTime = totalProcessorTime;
            var currentTimestamp = DateTime.UtcNow;

            if (!_snapshots.TryGetValue(pid, out var prev))
            {
                _snapshots[pid] = (currentCpuTime, currentTimestamp);
                if (startedUtc is not DateTime started) return 0;
                var age = currentTimestamp - started;
                if (age <= TimeSpan.Zero || age > LifetimeSeedWindow) return 0;
                prev = (TimeSpan.Zero, started);
            }

            var cpuDelta = (currentCpuTime - prev.PrevCpuTime).TotalMilliseconds;
            var timeDelta = (currentTimestamp - prev.PrevTimestamp).TotalMilliseconds;

            _snapshots[pid] = (currentCpuTime, currentTimestamp);

            if (timeDelta <= 0) return 0;

            // CPU usage = (CPU time used / wall time) / number of processors * 100
            var usage = (cpuDelta / timeDelta / _processorCount) * 100.0;
            return Math.Min(100.0, Math.Max(0.0, usage));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Removes tracking data for a process that has exited.
    /// </summary>
    public void RemoveProcess(int pid)
    {
        _snapshots.TryRemove(pid, out _);
    }
}
