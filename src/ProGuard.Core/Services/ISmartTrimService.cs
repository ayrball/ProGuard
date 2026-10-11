using System;
using ProGuard.Core.Models;

namespace ProGuard.Core.Services;

/// <summary>
/// Interface for SmartTrim - periodic working set trimming of high-memory processes.
/// </summary>
public interface ISmartTrimService
{
    /// <summary>Trim all processes above the configured memory threshold.</summary>
    void TrimMemory();

    /// <summary>Trim a specific process by PID.</summary>
    void TrimProcess(int pid);

    /// <summary>Memory threshold in bytes. Processes above this get trimmed.</summary>
    long ThresholdBytes { get; set; }

    /// <summary>Whether the periodic trim runs. Manual <see cref="TrimMemory"/>/<see cref="TrimProcess"/> calls always work.</summary>
    bool IsEnabled { get; }

    /// <summary>Time between periodic trims.</summary>
    TimeSpan Interval { get; }

    /// <summary>Applies the SmartTrim settings, rescheduling the periodic trim if the service is running.</summary>
    void Configure(bool enabled, TimeSpan interval, long thresholdBytes);
}
