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
    /// Gets the CPU usage percentage for a process since the last call.
    /// Returns 0 on the first call for any given process.
    /// </summary>
    public double GetCpuUsage(Process process)
    {
        try
        {
            var currentCpuTime = process.TotalProcessorTime;
            var currentTimestamp = DateTime.UtcNow;

            if (!_snapshots.TryGetValue(process.Id, out var prev))
            {
                _snapshots[process.Id] = (currentCpuTime, currentTimestamp);
                return 0;
            }

            var cpuDelta = (currentCpuTime - prev.PrevCpuTime).TotalMilliseconds;
            var timeDelta = (currentTimestamp - prev.PrevTimestamp).TotalMilliseconds;

            _snapshots[process.Id] = (currentCpuTime, currentTimestamp);

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
