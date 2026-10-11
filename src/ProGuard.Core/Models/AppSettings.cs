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
    // Optional restraint actions ([OutOfControlProcessRestraint], see ResponsivenessGuardRestraintOptions). All off, as in another process manager.
    /// <summary>[RestrainByAffinity] Also restrict a restrained process to <see cref="ResponsivenessGuardRestraintAffinity"/>.</summary>
    public bool ResponsivenessGuardRestrainByAffinity { get; set; } = false;
    /// <summary>[RestraintAffinity] CPU mask for <see cref="ResponsivenessGuardRestrainByAffinity"/>; 0 = none chosen.</summary>
    public long ResponsivenessGuardRestraintAffinity { get; set; } = 0;
    /// <summary>[ResponsivenessGuardDropOneRandomCore] Restrict affinity by removing one random CPU instead.</summary>
    public bool ResponsivenessGuardDropOneRandomCore { get; set; } = false;
    /// <summary>[LowerIOPriorityDuringRestraint] I/O priority Low while restrained.</summary>
    public bool ResponsivenessGuardLowerIoPriority { get; set; } = false;
    /// <summary>[UseEfficiencyMode] EcoQoS while restrained.</summary>
    public bool ResponsivenessGuardUseEfficiencyMode { get; set; } = false;
    /// <summary>[DoNotLowerPriorityClass] Restrain only through the actions above.</summary>
    public bool ResponsivenessGuardDoNotLowerPriorityClass { get; set; } = false;
    /// <summary>[DisableResponsivenessGuardWhenSysIdle] Pause while the user is idle.</summary>
    public bool ResponsivenessGuardDisableWhenSysIdle { get; set; } = false;
    /// <summary>[DisableResponsivenessGuardIfSysIdleThisManyMS] 30000.</summary>
    public int ResponsivenessGuardDisableIfSysIdleMs { get; set; } = 30000;
    
    /// <summary>Raise the foreground process from Normal to Above Normal while it has focus (ForegroundBoostService).</summary>
    public bool ForegroundBoostEnabled { get; set; } = true;

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
    
    // Performance Mode (another process manager's [GamingMode])
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
