using System;
using System.Collections.Generic;
using ProGuard.Core.Models;

namespace ProGuard.Core.Services;

/// <summary>
/// Interface for SmartTrim - Process Lasso's [MemoryManagement]: periodic working set trimming of high-memory
/// processes while memory load is high, plus standby list and file cache purges when free RAM runs low.
/// </summary>
public interface ISmartTrimService
{
    /// <summary>
    /// Trim every eligible process above the configured working-set threshold now, whatever the memory load.
    /// Exclusions, the protected list and the foreground skip still apply. Returns what was trimmed.
    /// </summary>
    MemoryPurgeReport TrimMemory();

    /// <summary>
    /// Trim a specific process by PID. The report says how much it gave back; when Windows refused, its
    /// <see cref="MemoryPurgeReport.Errors"/> say why.
    /// </summary>
    MemoryPurgeReport TrimProcess(int pid);

    /// <summary>Memory threshold in bytes. Processes at or above this get trimmed.</summary>
    long ThresholdBytes { get; set; }

    /// <summary>Whether the periodic trim runs. Manual <see cref="TrimMemory"/>/<see cref="TrimProcess"/> calls always work.</summary>
    bool IsEnabled { get; }

    /// <summary>Time between periodic trims.</summary>
    TimeSpan Interval { get; }

    /// <summary>Applies the SmartTrim settings, rescheduling the periodic trim if the service is running.</summary>
    void Configure(bool enabled, TimeSpan interval, long thresholdBytes);

    /// <summary>Applies the gates, exclusions and purge options.</summary>
    void ConfigurePolicy(SmartTrimPolicy policy);

    /// <summary>
    /// "Purge now": trims eligible working sets and purges the standby list (and the file cache, when that option is on)
    /// immediately, ignoring the memory-load, free-RAM and Performance Mode gates.
    /// </summary>
    MemoryPurgeReport PurgeNow();
}

/// <summary>The SmartTrim options beyond on/off, interval and working-set threshold. Names follow prolasso.ini's [MemoryManagement].</summary>
public sealed record SmartTrimPolicy
{
    /// <summary>SmartTrimAutoMinimumRAMLoad: periodic trims only run while memory load is at least this percent.</summary>
    public int MinimumRamLoadPercent { get; init; } = 65;

    /// <summary>Process names (with or without .exe) SmartTrim never trims.</summary>
    public IReadOnlyCollection<string> Exclusions { get; init; } = Array.Empty<string>();

    /// <summary>Log a line when a pass finds nothing to do, not just when it acts.</summary>
    public bool LogNoAction { get; init; }

    /// <summary>Purge the standby list when free RAM drops below <see cref="StandbyFreeRamThresholdBytes"/>.</summary>
    public bool StandbyPurgeEnabled { get; init; }

    /// <summary>ClearStandbyFreeRAMThresholdMB.</summary>
    public long StandbyFreeRamThresholdBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>Only purge the standby list automatically while Performance Mode is on (Process Lasso's default).</summary>
    public bool StandbyOnlyInPerformanceMode { get; init; } = true;

    /// <summary>Also flush the system file cache whenever the standby list is purged.</summary>
    public bool FileCachePurgeEnabled { get; init; }
}
