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
    // Optional restraint actions ([OutOfControlProcessRestraint], see ProBalanceRestraintOptions). All off, as in Process Lasso.
    /// <summary>[RestrainByAffinity] Also restrict a restrained process to <see cref="ProBalanceRestraintAffinity"/>.</summary>
    public bool ProBalanceRestrainByAffinity { get; set; } = false;
    /// <summary>[RestraintAffinity] CPU mask for <see cref="ProBalanceRestrainByAffinity"/>; 0 = none chosen.</summary>
    public long ProBalanceRestraintAffinity { get; set; } = 0;
    /// <summary>[ProBalanceDropOneRandomCore] Restrict affinity by removing one random CPU instead.</summary>
    public bool ProBalanceDropOneRandomCore { get; set; } = false;
    /// <summary>[LowerIOPriorityDuringRestraint] I/O priority Low while restrained.</summary>
    public bool ProBalanceLowerIoPriority { get; set; } = false;
    /// <summary>[UseEfficiencyMode] EcoQoS while restrained.</summary>
    public bool ProBalanceUseEfficiencyMode { get; set; } = false;
    /// <summary>[DoNotLowerPriorityClass] Restrain only through the actions above.</summary>
    public bool ProBalanceDoNotLowerPriorityClass { get; set; } = false;
    /// <summary>[DisableProBalanceWhenSysIdle] Pause while the user is idle.</summary>
    public bool ProBalanceDisableWhenSysIdle { get; set; } = false;
    /// <summary>[DisableProBalanceIfSysIdleThisManyMS] 30000.</summary>
    public int ProBalanceDisableIfSysIdleMs { get; set; } = 30000;
    
    /// <summary>Raise the foreground process from Normal to Above Normal while it has focus (ForegroundBoostService).</summary>
    public bool ForegroundBoostEnabled { get; set; } = true;

    // SmartTrim. Defaults are Process Lasso's [MemoryManagement] ones (prolasso.ini key in brackets).
    public bool SmartTrimEnabled { get; set; } = false;
    /// <summary>[SmartTrimIntervalMins] 15.</summary>
    public int SmartTrimIntervalSeconds { get; set; } = 900;
    /// <summary>[MinimumProcessWSSInMb] Only processes with at least this working set are trimmed.</summary>
    public long SmartTrimMemoryThresholdMB { get; set; } = 196;
    /// <summary>[SmartTrimAutoMinimumRAMLoad] Periodic trims only run while memory load is at least this percent.</summary>
    public int SmartTrimMinimumRamLoadPercent { get; set; } = 65;
    /// <summary>Process names SmartTrim never trims, separated by commas, semicolons or new lines.</summary>
    public string? SmartTrimExclusions { get; set; }
    /// <summary>Also log the passes that find nothing to do.</summary>
    public bool SmartTrimLogNoAction { get; set; } = false;
    /// <summary>Purge the standby list when free RAM drops below <see cref="StandbyPurgeFreeRamThresholdMB"/>.</summary>
    public bool StandbyPurgeEnabled { get; set; } = false;
    /// <summary>[ClearStandbyFreeRAMThresholdMB] 1024.</summary>
    public long StandbyPurgeFreeRamThresholdMB { get; set; } = 1024;
    /// <summary>Only purge automatically while Performance Mode is on (Process Lasso's default).</summary>
    public bool StandbyPurgeOnlyInPerformanceMode { get; set; } = true;
    /// <summary>Also flush the system file cache when free RAM is low, and on "Purge now".</summary>
    public bool FileCachePurgeEnabled { get; set; } = false;
    
    // IdleSaver
    public bool IdleSaverEnabled { get; set; } = false;
    public int IdleTimeoutMinutes { get; set; } = 15;
    public string? IdlePowerPlanGuid { get; set; }
    
    // Performance Mode (Process Lasso's [GamingMode])
    /// <summary>Plan Performance Mode switches to (TargetPowerPlan); null = High Performance.</summary>
    public string? PerformanceModePlanGuid { get; set; }
    /// <summary>Processes that turn Performance Mode on while they run (AutomaticGamingModeProcessPaths): exe names
    /// or full paths, '*' wildcards allowed, separated by ';'.</summary>
    public string? AutoPerformanceModeProcesses { get; set; }
    /// <summary>Processes that never turn it on, even if listed or detected (AutomaticGamingModeUserExclusions).</summary>
    public string? AutoPerformanceModeExclusions { get; set; }
    /// <summary>Turn it on while a game detected from its install path (Steam, Epic, GOG, ...) runs.</summary>
    public bool AutoPerformanceModeDetectGames { get; set; } = false;
    /// <summary>Turn it on while a game from one of the Steam libraries runs (GamingModeEngageForSteam).</summary>
    public bool PerformanceModeEngageForSteam { get; set; } = false;

    // Load-based power switcher ([LoadBasedPowerSwitcher])
    public bool LoadSwitcherEnabled { get; set; } = false;
    public double LoadSwitcherLowThreshold { get; set; } = 10.0;
    public double LoadSwitcherHighThreshold { get; set; } = 50.0;
    public int LoadSwitcherAveragingWindowSamples { get; set; } = 5;
    public int LoadSwitcherMinimumTimeInPlanMs { get; set; } = 5000;
    /// <summary>null = Balanced.</summary>
    public string? LoadSwitcherLowPlanGuid { get; set; }
    /// <summary>null = High Performance.</summary>
    public string? LoadSwitcherHighPlanGuid { get; set; }
    /// <summary>At low load, go back to the plan from before instead of switching to the low plan.</summary>
    public bool LoadSwitcherDeferLowPlan { get; set; } = true;

    // [PowerManagement]
    /// <summary>Plan to switch to when the engine starts (StartWithPowerPlan); null = leave it.</summary>
    public string? StartWithPowerPlanGuid { get; set; }

    // General
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public string Theme { get; set; } = "System";
    public int PollingIntervalMs { get; set; } = 1000;

    /// <summary>Shallow copy, so a caller can change settings without mutating the instance a store has cached.</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
