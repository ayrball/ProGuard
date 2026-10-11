using System.ComponentModel.DataAnnotations;

namespace ProGuard.Core.Models;

public class AppSettings
{
    [Key]
    public int Id { get; set; }
    
    // ProBalance
    public bool ProBalanceEnabled { get; set; } = true;
    public int ProBalanceSensitivity { get; set; } = 5;
    public double ProBalanceCpuThreshold { get; set; } = 25.0;
    public double ProBalanceRecoveryThreshold { get; set; } = 10.0;
    
    // SmartTrim
    public bool SmartTrimEnabled { get; set; } = false;
    public int SmartTrimIntervalSeconds { get; set; } = 300;
    public long SmartTrimMemoryThresholdMB { get; set; } = 1024;
    
    // IdleSaver
    public bool IdleSaverEnabled { get; set; } = false;
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
