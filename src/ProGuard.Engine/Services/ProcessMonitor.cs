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
    
    public event EventHandler<ProcessInfo>? ProcessStarted;
    public event EventHandler<int>? ProcessExited;

    public ProcessMonitor(ILogger<ProcessMonitor> logger)
    {
        _logger = logger;
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
        _refreshTimer = new Timer(_ => RefreshProcesses(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        
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
            var p = Process.GetProcessById(pid);
            var info = CreateProcessInfo(p);
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

    private void RefreshProcesses()
    {
        var currentProcesses = Process.GetProcesses();
        var foregroundHwnd = User32.GetForegroundWindow();
        uint foregroundPid = 0;
        if (foregroundHwnd != IntPtr.Zero)
        {
            User32.GetWindowThreadProcessId(foregroundHwnd, out foregroundPid);
        }

        var currentPids = new HashSet<int>();

        foreach (var p in currentProcesses)
        {
            currentPids.Add(p.Id);
            try
            {
                var cpuUsage = _cpuTracker.GetCpuUsage(p);
                if (_processes.TryGetValue(p.Id, out var info))
                {
                    info.CpuPercent = cpuUsage;
                    info.WorkingSetBytes = p.WorkingSet64;
                    info.IsForeground = (p.Id == foregroundPid);
                    try { info.PriorityClass = p.PriorityClass; } catch { }
                    try { info.AffinityMask = p.ProcessorAffinity.ToInt64(); } catch { }
                }
                else
                {
                    var newInfo = CreateProcessInfo(p);
                    newInfo.CpuPercent = cpuUsage;
                    newInfo.IsForeground = (p.Id == foregroundPid);
                    _processes[p.Id] = newInfo;
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

    private ProcessInfo CreateProcessInfo(Process p)
    {
        var info = new ProcessInfo
        {
            Pid = p.Id,
            Name = p.ProcessName,
            WorkingSetBytes = p.WorkingSet64,
            Status = ProcessStatus.Running
        };

        try
        {
            info.FilePath = p.MainModule?.FileName ?? "";
            info.PriorityClass = p.PriorityClass;
            info.AffinityMask = p.ProcessorAffinity.ToInt64();
        }
        catch
        {
            // Access denied
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
    public int GetForegroundPid()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _startWatcher?.Dispose();
        _stopWatcher?.Dispose();
    }
}
