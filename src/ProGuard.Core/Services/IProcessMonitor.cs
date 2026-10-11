using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProGuard.Core.Models;

namespace ProGuard.Core.Services;

/// <summary>
/// Interface for monitoring running processes on the system.
/// </summary>
public interface IProcessMonitor
{
    /// <summary>Gets an async snapshot of all running processes.</summary>
    Task<List<ProcessInfo>> GetProcessesAsync();

    /// <summary>Gets a synchronous enumerable of all cached processes.</summary>
    IEnumerable<ProcessInfo> GetAllProcesses();

    /// <summary>Gets a single process by PID.</summary>
    ProcessInfo? GetProcess(int pid);

    /// <summary>Raised when a new process starts.</summary>
    event EventHandler<ProcessInfo> ProcessStarted;

    /// <summary>Raised when a process exits.</summary>
    event EventHandler<int> ProcessExited;

    /// <summary>
    /// Raised at the end of each refresh, once every process's CPU usage has been updated. Work that judges
    /// usage (ProBalance) runs on this rather than its own timer, so it never acts on a sample up to a whole
    /// polling interval old.
    /// </summary>
    event EventHandler Refreshed;

    /// <summary>Gets the PID of the current foreground window process.</summary>
    int GetForegroundPid();

    /// <summary>How often the process list is refreshed (AppSettings.PollingIntervalMs).</summary>
    TimeSpan PollingInterval { get; }

    /// <summary>Changes the refresh interval, taking effect immediately if monitoring has started.</summary>
    void SetPollingInterval(TimeSpan interval);
}
