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

    /// <summary>Gets the PID of the current foreground window process.</summary>
    int GetForegroundPid();
}
