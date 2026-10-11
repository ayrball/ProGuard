using System.ComponentModel.DataAnnotations;

namespace ProGuard.Core.Models;

public class AppSettings
{
    [Key]
    public int Id { get; set; }
    
    // ResponsivenessGuard
    public bool ResponsivenessGuardEnabled { get; set; } = true;
    public int ResponsivenessGuardSensitivity { get; set; } = 5;
    public double ResponsivenessGuardCpuThreshold { get; set; } = 25.0;
    public double ResponsivenessGuardRecoveryThreshold { get; set; } = 10.0;
    
    // MemoryTrim. Defaults are another process manager's [MemoryManagement] ones (otherpm.ini key in brackets).
    public bool MemoryTrimEnabled { get; set; } = false;
    /// <summary>[MemoryTrimIntervalMins] 15.</summary>
    public int MemoryTrimIntervalSeconds { get; set; } = 900;
    /// <summary>[MinimumProcessWSSInMb] Only processes with at least this working set are trimmed.</summary>
    public long MemoryTrimMemoryThresholdMB { get; set; } = 196;
    /// <summary>[MemoryTrimAutoMinimumRAMLoad] Periodic trims only run while memory load is at least this percent.</summary>
    public int MemoryTrimMinimumRamLoadPercent { get; set; } = 65;
    /// <summary>Process names MemoryTrim never trims, separated by commas, semicolons or new lines.</summary>
    public string? MemoryTrimExclusions { get; set; }
    /// <summary>Also log the passes that find nothing to do.</summary>
    public bool MemoryTrimLogNoAction { get; set; } = false;
    /// <summary>Purge the standby list when free RAM drops below <see cref="StandbyPurgeFreeRamThresholdMB"/>.</summary>
    public bool StandbyPurgeEnabled { get; set; } = false;
    /// <summary>[ClearStandbyFreeRAMThresholdMB] 1024.</summary>
    public long StandbyPurgeFreeRamThresholdMB { get; set; } = 1024;
    /// <summary>Only purge automatically while Performance Mode is on (another process manager's default).</summary>
    public bool StandbyPurgeOnlyInPerformanceMode { get; set; } = true;
    /// <summary>Also flush the system file cache when free RAM is low, and on "Purge now".</summary>
    public bool FileCachePurgeEnabled { get; set; } = false;
    
    // AwayMode
    public bool AwayModeEnabled { get; set; } = false;
    public int IdleTimeoutMinutes { get; set; } = 15;
    public string? IdlePowerPlanGuid { get; set; }
    
    // General
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public string Theme { get; set; } = "System";
    public int PollingIntervalMs { get; set; } = 1000;

    /// <summary>Shallow copy, so a caller can change settings without mutating the instance a store has cached.</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
