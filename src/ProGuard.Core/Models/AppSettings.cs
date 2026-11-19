using System.ComponentModel.DataAnnotations;

namespace ProGuard.Core.Models;

public class AppSettings
{
    [Key]
    public int Id { get; set; }
    
    // Responsiveness Guard
    public bool ResponsivenessGuardEnabled { get; set; } = true;
    public int ResponsivenessGuardSensitivity { get; set; } = 5;
    public double ResponsivenessGuardCpuThreshold { get; set; } = 25.0;
    public double ResponsivenessGuardRecoveryThreshold { get; set; } = 10.0;
    // Optional restraint actions (see ResponsivenessGuardRestraintOptions). All off by default.
    /// <summary>Also restrict a restrained process to <see cref="ResponsivenessGuardRestraintAffinity"/>.</summary>
    public bool ResponsivenessGuardRestrainByAffinity { get; set; } = false;
    /// <summary>CPU mask for <see cref="ResponsivenessGuardRestrainByAffinity"/>; 0 = none chosen.</summary>
    public long ResponsivenessGuardRestraintAffinity { get; set; } = 0;
    /// <summary>Restrict affinity by removing one random CPU instead.</summary>
    public bool ResponsivenessGuardDropOneRandomCore { get; set; } = false;
    /// <summary>I/O priority Low while restrained.</summary>
    public bool ResponsivenessGuardLowerIoPriority { get; set; } = false;
    /// <summary>EcoQoS while restrained.</summary>
    public bool ResponsivenessGuardUseEfficiencyMode { get; set; } = false;
    /// <summary>Restrain only through the actions above.</summary>
    public bool ResponsivenessGuardDoNotLowerPriorityClass { get; set; } = false;
    /// <summary>Pause while the user is idle.</summary>
    public bool ResponsivenessGuardDisableWhenSysIdle { get; set; } = false;
    /// <summary>How long the user must be idle before the pause, in ms.</summary>
    public int ResponsivenessGuardDisableIfSysIdleMs { get; set; } = 30000;
    /// <summary>
    /// While any process is restrained, set the active plan's core parking minimum to
    /// 100 % (no parked cores), and put it back afterwards. Administrators only: it edits a machine-wide power plan.
    /// </summary>
    public bool ResponsivenessGuardDisableCoreParking { get; set; } = false;
    
    /// <summary>Raise the foreground process from Normal to Above Normal while it has focus (ForegroundBoostService). Off by default.</summary>
    public bool ForegroundBoostEnabled { get; set; } = false;

    // Memory Trim
    public bool MemoryTrimEnabled { get; set; } = false;
    /// <summary>Time between periodic trims; 15 minutes by default.</summary>
    public int MemoryTrimIntervalSeconds { get; set; } = 900;
    /// <summary>Only processes with at least this working set are trimmed.</summary>
    public long MemoryTrimMemoryThresholdMB { get; set; } = 196;
    /// <summary>Periodic trims only run while memory load is at least this percent.</summary>
    public int MemoryTrimMinimumRamLoadPercent { get; set; } = 65;
    /// <summary>Process names Memory Trim never trims, separated by commas, semicolons or new lines.</summary>
    public string? MemoryTrimExclusions { get; set; }
    /// <summary>Also log the passes that find nothing to do.</summary>
    public bool MemoryTrimLogNoAction { get; set; } = false;
    /// <summary>Purge the standby list when free RAM drops below <see cref="StandbyPurgeFreeRamThresholdMB"/>.</summary>
    public bool StandbyPurgeEnabled { get; set; } = false;
    /// <summary>Free RAM below which the standby list is purged; 1024 MB by default.</summary>
    public long StandbyPurgeFreeRamThresholdMB { get; set; } = 1024;
    /// <summary>Only purge automatically while Performance Mode is on (the default).</summary>
    public bool StandbyPurgeOnlyInPerformanceMode { get; set; } = true;
    /// <summary>Also flush the system file cache when free RAM is low, and on "Purge now".</summary>
    public bool FileCachePurgeEnabled { get; set; } = false;
    
    // Away Mode
    public bool AwayModeEnabled { get; set; } = false;
    public int IdleTimeoutMinutes { get; set; } = 15;
    public string? IdlePowerPlanGuid { get; set; }
    
    // Performance Mode
    /// <summary>Plan Performance Mode switches to; null = High Performance.</summary>
    public string? PerformanceModePlanGuid { get; set; }
    /// <summary>Processes that turn Performance Mode on while they run: exe names
    /// or full paths, '*' wildcards allowed, separated by ';'.</summary>
    public string? AutoPerformanceModeProcesses { get; set; }
    /// <summary>Processes that never turn it on, even if listed or detected.</summary>
    public string? AutoPerformanceModeExclusions { get; set; }
    /// <summary>Turn it on while a game detected from its install path (Steam, Epic, GOG, ...) runs.</summary>
    public bool AutoPerformanceModeDetectGames { get; set; } = false;
    /// <summary>Turn it on while a game from one of the Steam libraries runs.</summary>
    public bool PerformanceModeEngageForSteam { get; set; } = false;
    /// <summary>
    /// Also move the Windows power-mode slider to Best performance while Performance Mode is on, and back afterwards.
    /// Windows applies the mode only under the Balanced plan, so this matters when Performance Mode's plan is Balanced.
    /// </summary>
    public bool PerformanceModeSetBestPerformance { get; set; } = false;

    // Load-based power switcher
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

    // Power plan at engine start
    /// <summary>Plan to switch to when the engine starts; null = leave it.</summary>
    public string? StartWithPowerPlanGuid { get; set; }

    // System timer
    /// <summary>
    /// Timer resolution the engine asks Windows for while it runs, in 100-ns units (5000 = 0.5 ms); 0 = don't ask
    /// (the default). Administrators only.
    /// </summary>
    public int TimerResolutionRequest { get; set; } = 0;

    // General
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public string Theme { get; set; } = "System";
    public int PollingIntervalMs { get; set; } = 1000;

    /// <summary>Shallow copy, so a caller can change settings without mutating the instance a store has cached.</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
