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
    
    // MemoryTrim
    public bool MemoryTrimEnabled { get; set; } = false;
    public int MemoryTrimIntervalSeconds { get; set; } = 300;
    public long MemoryTrimMemoryThresholdMB { get; set; } = 1024;
    
    // AwayMode
    public bool AwayModeEnabled { get; set; } = false;
    public int IdleTimeoutMinutes { get; set; } = 15;
    public string? IdlePowerPlanGuid { get; set; }
    
    // General
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public string Theme { get; set; } = "System";
    public int PollingIntervalMs { get; set; } = 1000;
}
