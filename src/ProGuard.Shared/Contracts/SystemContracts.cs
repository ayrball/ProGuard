using System;
using System.Collections.Generic;

namespace ProGuard.Shared.Contracts;

/// <summary>
/// Wire copy of ProGuard.Core.Models.AppSettings (every property except the database key), with the same names and
/// meanings; another process manager's otherpm.ini key is given where there is one. Power plan fields hold a plan GUID as text,
/// or null for the default named in the comment.
/// </summary>
public sealed class EngineSettingsDto
{
    // ResponsivenessGuard
    public bool ResponsivenessGuardEnabled { get; set; } = true;
    /// <summary>1 (step in only under extreme load) to 10 (step in early); 5 = another process manager's defaults.</summary>
    public int ResponsivenessGuardSensitivity { get; set; } = 5;
    public double ResponsivenessGuardCpuThreshold { get; set; } = 25.0;
    public double ResponsivenessGuardRecoveryThreshold { get; set; } = 10.0;
    public bool ResponsivenessGuardRestrainByAffinity { get; set; }
    public long ResponsivenessGuardRestraintAffinity { get; set; }
    public bool ResponsivenessGuardDropOneRandomCore { get; set; }
    public bool ResponsivenessGuardLowerIoPriority { get; set; }
    public bool ResponsivenessGuardUseEfficiencyMode { get; set; }
    public bool ResponsivenessGuardDoNotLowerPriorityClass { get; set; }
    public bool ResponsivenessGuardDisableWhenSysIdle { get; set; }
    public int ResponsivenessGuardDisableIfSysIdleMs { get; set; } = 30000;

    /// <summary>Raise the foreground process from Normal to Above Normal while it has focus.</summary>
    public bool ForegroundBoostEnabled { get; set; } = true;

    // MemoryTrim
    public bool MemoryTrimEnabled { get; set; }
    public int MemoryTrimIntervalSeconds { get; set; } = 900;
    public long MemoryTrimMemoryThresholdMB { get; set; } = 196;
    public int MemoryTrimMinimumRamLoadPercent { get; set; } = 65;
    public string? MemoryTrimExclusions { get; set; }
    public bool MemoryTrimLogNoAction { get; set; }
    public bool StandbyPurgeEnabled { get; set; }
    public long StandbyPurgeFreeRamThresholdMB { get; set; } = 1024;
    public bool StandbyPurgeOnlyInPerformanceMode { get; set; } = true;
    public bool FileCachePurgeEnabled { get; set; }

    // AwayMode
    public bool AwayModeEnabled { get; set; }
    public int IdleTimeoutMinutes { get; set; } = 15;
    /// <summary>null = Power saver.</summary>
    public string? IdlePowerPlanGuid { get; set; }

    // Performance Mode
    /// <summary>null = High performance.</summary>
    public string? PerformanceModePlanGuid { get; set; }
    /// <summary>Exe names or full paths, '*' wildcards allowed, separated by ';'.</summary>
    public string? AutoPerformanceModeProcesses { get; set; }
    public string? AutoPerformanceModeExclusions { get; set; }
    public bool AutoPerformanceModeDetectGames { get; set; }
    public bool PerformanceModeEngageForSteam { get; set; }

    // Load-based power switcher
    public bool LoadSwitcherEnabled { get; set; }
    public double LoadSwitcherLowThreshold { get; set; } = 10.0;
    public double LoadSwitcherHighThreshold { get; set; } = 50.0;
    public int LoadSwitcherAveragingWindowSamples { get; set; } = 5;
    public int LoadSwitcherMinimumTimeInPlanMs { get; set; } = 5000;
    /// <summary>null = Balanced.</summary>
    public string? LoadSwitcherLowPlanGuid { get; set; }
    /// <summary>null = High performance.</summary>
    public string? LoadSwitcherHighPlanGuid { get; set; }
    public bool LoadSwitcherDeferLowPlan { get; set; } = true;

    /// <summary>Plan to switch to when the engine starts; null = leave it.</summary>
    public string? StartWithPowerPlanGuid { get; set; }

    // General
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public string Theme { get; set; } = "System";
    public int PollingIntervalMs { get; set; } = 1000;
}

public sealed class PowerPlanDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class PowerStateDto
{
    /// <summary>Every power plan installed on the machine.</summary>
    public List<PowerPlanDto> Plans { get; set; } = new();
    public Guid ActivePlanId { get; set; }
    public bool PerformanceMode { get; set; }

    /// <summary>Performance Mode is on because a listed or detected game is running, not because the user turned it on.</summary>
    public bool PerformanceModeAutomatic { get; set; }

    /// <summary>Who set the active plan: "User", "PerformanceMode", "AwayMode", "LoadSwitcher", "StartWith".</summary>
    public string Owner { get; set; } = string.Empty;
}

/// <summary>One CPU Set, for picking the IDs a <see cref="ProcessRuleDto.CpuSetIds"/> rule names.</summary>
public sealed class CpuSetDto
{
    public int Id { get; set; }
    public int Group { get; set; }
    public int LogicalProcessorIndex { get; set; }
    public int CoreIndex { get; set; }
    public int NumaNodeIndex { get; set; }

    /// <summary>Higher = faster core on a hybrid CPU (P-cores); all equal on a non-hybrid CPU.</summary>
    public int EfficiencyClass { get; set; }
    public bool IsParked { get; set; }
}

public enum ActivityKind
{
    /// <summary>ResponsivenessGuard lowered a process's priority.</summary>
    Restrained,

    /// <summary>ResponsivenessGuard or another timed action gave a process its settings back.</summary>
    Restored,

    /// <summary>A persistent rule, startup boost, CPU Limiter, instance limit or balancer acted on a process.</summary>
    Rule,

    /// <summary>A watchdog rule fired, or Keep Running relaunched a process.</summary>
    Watchdog,

    /// <summary>The active power plan changed.</summary>
    Power,

    /// <summary>MemoryTrim trimmed working sets or purged memory.</summary>
    Memory,

    /// <summary>Someone did something from the UI.</summary>
    Manual,

    /// <summary>Engine lifecycle and errors.</summary>
    System
}

public sealed class ActivityEntry
{
    public DateTime TimestampUtc { get; set; }
    public ActivityKind Kind { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public int Pid { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Result of <see cref="Messages.CommandTypes.PurgeMemory"/>; mirrors ProGuard.Core.Models.MemoryPurgeReport.</summary>
public sealed class MemoryPurgeResultDto
{
    public int ProcessesTrimmed { get; set; }
    public long WorkingSetBytesReclaimed { get; set; }
    public bool StandbyPurged { get; set; }
    public long StandbyBytesReclaimed { get; set; }
    public bool FileCachePurged { get; set; }
    public long FileCacheBytesReclaimed { get; set; }
    public List<string> Errors { get; set; } = new();

    /// <summary>One sentence describing what happened, fit to show as-is.</summary>
    public string Summary { get; set; } = string.Empty;
}
