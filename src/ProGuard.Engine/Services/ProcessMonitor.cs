using System;
using System.Diagnostics;
using System.Management;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Core.Native;
using ProGuard.Core.Helpers;

namespace ProGuard.Engine.Services;

/// <summary>
/// Monitors all running processes on the system, tracking CPU usage, memory, priority, and affinity.
/// Uses WMI event watchers for real-time process start/stop detection with polling fallback.
/// </summary>
public class ProcessMonitor : IProcessMonitor, IDisposable
{
    private readonly ILogger<ProcessMonitor> _logger;
    private readonly ConcurrentDictionary<int, ProcessInfo> _processes = new();
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private Timer? _refreshTimer;
    private readonly CpuUsageTracker _cpuTracker = new();
    private TimeSpan _pollingInterval = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan MinPollingInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MaxPollingInterval = TimeSpan.FromSeconds(60);
    
    public event EventHandler<ProcessInfo>? ProcessStarted;
    public event EventHandler<int>? ProcessExited;

    private readonly IForegroundTracker _foreground;

    public ProcessMonitor(ILogger<ProcessMonitor> logger, IForegroundTracker? foreground = null)
    {
        _logger = logger;
        _foreground = foreground ?? new ForegroundTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger<ForegroundTracker>.Instance);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Process Monitor");
        
        try
        {
            var startQuery = new WqlEventQuery("__InstanceCreationEvent", new TimeSpan(0, 0, 1), "TargetInstance isa \"Win32_Process\"");
            _startWatcher = new ManagementEventWatcher(startQuery);
            _startWatcher.EventArrived += OnProcessStarted;
            _startWatcher.Start();

            var stopQuery = new WqlEventQuery("__InstanceDeletionEvent", new TimeSpan(0, 0, 1), "TargetInstance isa \"Win32_Process\"");
            _stopWatcher = new ManagementEventWatcher(stopQuery);
            _stopWatcher.EventArrived += OnProcessStopped;
            _stopWatcher.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start WMI event watchers. Falling back to polling only.");
        }

        RefreshProcesses();
        _refreshTimer = new Timer(_ => SafeRefresh(), null, _pollingInterval, _pollingInterval);
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Process Monitor");
        _refreshTimer?.Change(Timeout.Infinite, 0);
        
        if (_startWatcher != null)
        {
            _startWatcher.Stop();
            _startWatcher.EventArrived -= OnProcessStarted;
        }
        
        if (_stopWatcher != null)
        {
            _stopWatcher.Stop();
            _stopWatcher.EventArrived -= OnProcessStopped;
        }

        return Task.CompletedTask;
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var targetInstance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var pid = Convert.ToInt32(targetInstance["ProcessId"]);
            using var p = Process.GetProcessById(pid);
            var probed = ProcessProbe.TryQuery(pid, includeImagePath: true, out var probe);
            var info = CreateProcessInfo(p, probed, probe);
            UpdateDynamic(info, p, probed, probe, _foreground.GetForegroundPid(), 0);
            _processes[pid] = info;
            ProcessStarted?.Invoke(this, info);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error handling process start event");
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var targetInstance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var pid = Convert.ToInt32(targetInstance["ProcessId"]);
            _processes.TryRemove(pid, out _);
            _cpuTracker.RemoveProcess(pid);
            ProcessExited?.Invoke(this, pid);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error handling process stop event");
        }
    }

    // Timer-thread entry point: an exception escaping a timer callback would terminate the engine process.
    private void SafeRefresh()
    {
        try { RefreshProcesses(); }
        catch (Exception ex) { _logger.LogError(ex, "Error refreshing process list"); }
    }

    private void RefreshProcesses()
    {
        var currentProcesses = Process.GetProcesses();
        var foregroundPid = _foreground.GetForegroundPid();

        var currentPids = new HashSet<int>(currentProcesses.Length);

        foreach (var p in currentProcesses)
        {
            var pid = p.Id;
            currentPids.Add(pid);
            try
            {
                _processes.TryGetValue(pid, out var info);

                // One limited-rights handle yields CPU time, priority and affinity (and, for a process we haven't seen,
                // its parent, start time and path) without the access-denied exceptions Process throws for most system
                // and service processes.
                var probed = ProcessProbe.TryQuery(pid, includeImagePath: info == null, out var probe);
                var cpuUsage = probed ? _cpuTracker.GetCpuUsage(pid, probe.CpuTime) : 0;

                // The id now belongs to a different process: the old one exited between two polls.
                if (info != null && probed && IsRecycled(info, probe))
                {
                    _processes.TryRemove(pid, out _);
                    _cpuTracker.RemoveProcess(pid);
                    ProcessExited?.Invoke(this, pid);
                    info = null;
                    probed = ProcessProbe.TryQuery(pid, includeImagePath: true, out probe);
                    cpuUsage = 0;
                }

                if (info != null)
                {
                    UpdateDynamic(info, p, probed, probe, foregroundPid, cpuUsage);
                }
                else
                {
                    var newInfo = CreateProcessInfo(p, probed, probe);
                    UpdateDynamic(newInfo, p, probed, probe, foregroundPid, cpuUsage);
                    _processes[pid] = newInfo;
                    ProcessStarted?.Invoke(this, newInfo);
                }
            }
            catch (Exception)
            {
                // Ignore access denied for system processes
            }
            finally
            {
                p.Dispose();
            }
        }

        var toRemove = _processes.Keys.Except(currentPids).ToList();
        foreach (var pid in toRemove)
        {
            _processes.TryRemove(pid, out _);
            _cpuTracker.RemoveProcess(pid);
            ProcessExited?.Invoke(this, pid);
        }
    }

    private static bool IsRecycled(ProcessInfo known, in ProcessProbeResult probe) =>
        known.StartTime != DateTime.MinValue && probe.StartTimeUtc != DateTime.MinValue && known.StartTime != probe.StartTimeUtc;

    /// <summary>What changes from poll to poll. Priority and affinity keep their last value when they can't be read this time.</summary>
    private static void UpdateDynamic(ProcessInfo info, Process p, bool probed, in ProcessProbeResult probe, int foregroundPid, double cpuUsage)
    {
        info.CpuPercent = cpuUsage;
        info.WorkingSetBytes = p.WorkingSet64;
        info.PrivateBytes = p.PrivateMemorySize64;
        info.HandleCount = p.HandleCount;
        info.IsForeground = p.Id == foregroundPid;
        if (probed)
        {
            if (probe.Priority is { } priority) info.PriorityClass = priority;
            if (probe.AffinityMask is { } mask) info.AffinityMask = mask;
        }

        var (threads, suspended) = ReadThreads(p);
        info.ThreadCount = threads;
        info.Status = suspended ? ProcessStatus.Suspended : ProcessStatus.Running;
    }

    /// <summary>
    /// Thread count, and whether every thread is waiting with reason Suspended (what NtSuspendProcess, and Windows
    /// freezing a background app, leave behind). The thread states are in the data <see cref="Process.GetProcesses()"/>
    /// already fetched, so this opens nothing.
    /// </summary>
    private static (int Threads, bool Suspended) ReadThreads(Process p)
    {
        try
        {
            var threads = p.Threads;
            var count = threads.Count;
            if (count == 0) return (0, false);
            foreach (ProcessThread thread in threads)
            {
                if (thread.ThreadState != System.Diagnostics.ThreadState.Wait || thread.WaitReason != ThreadWaitReason.Suspended)
                    return (count, false);
            }
            return (count, true);
        }
        catch (Exception)
        {
            return (0, false);
        }
    }

    private static ProcessInfo CreateProcessInfo(Process p, bool probed, in ProcessProbeResult probe)
    {
        var info = new ProcessInfo
        {
            Pid = p.Id,
            Name = p.ProcessName,
            WorkingSetBytes = p.WorkingSet64,
            Status = ProcessStatus.Running
        };
        try { info.SessionId = p.SessionId; } catch { /* the process is gone */ }

        // Whatever the process wouldn't tell us stays unknown (null / MinValue) rather than failing the lot.
        if (probed)
        {
            info.FilePath = probe.ImagePath;
            info.ParentPid = probe.ParentPid > 0 ? probe.ParentPid : null;
            info.StartTime = probe.StartTimeUtc;
            info.PriorityClass = probe.Priority;
            if (probe.AffinityMask is { } mask) info.AffinityMask = mask;
        }

        return info;
    }

    /// <inheritdoc/>
    public Task<List<ProcessInfo>> GetProcessesAsync()
    {
        return Task.FromResult(_processes.Values.ToList());
    }

    /// <inheritdoc/>
    public IEnumerable<ProcessInfo> GetAllProcesses() => _processes.Values;

    /// <inheritdoc/>
    public ProcessInfo? GetProcess(int pid) => _processes.TryGetValue(pid, out var p) ? p : null;

    /// <inheritdoc/>
    public int GetForegroundPid() => _foreground.GetForegroundPid();

    /// <inheritdoc/>
    public TimeSpan PollingInterval => _pollingInterval;

    /// <inheritdoc/>
    public void SetPollingInterval(TimeSpan interval)
    {
        // Clamped: a tiny interval would pin a core enumerating processes, a huge one makes the list useless.
        if (interval < MinPollingInterval) interval = MinPollingInterval;
        if (interval > MaxPollingInterval) interval = MaxPollingInterval;
        if (interval == _pollingInterval) return;

        _pollingInterval = interval;
        _refreshTimer?.Change(interval, interval);
        _logger.LogInformation("Process polling interval set to {Interval}ms", interval.TotalMilliseconds);
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _startWatcher?.Dispose();
        _stopWatcher?.Dispose();
    }
}
