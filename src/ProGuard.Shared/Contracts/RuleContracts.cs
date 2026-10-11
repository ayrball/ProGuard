namespace ProGuard.Shared.Contracts;

// Wire copies of ProGuard.Core.Models.ProcessRule and WatchdogRule. The UI has no reference to ProGuard.Core, so
// these mirror the stored models property for property (same names, same meaning) and the engine copies between
// them by name. A test fails if a model property has no counterpart here. Enums mirror the Core enums value for
// value; they have their own names so engine code can use both namespaces without ambiguity.

/// <summary>Mirror of ProGuard.Core.Models.InstanceBalancerMode.</summary>
public enum BalancerMode { EqualCoresPerInstance, FixedCoresPerInstance }

/// <summary>Mirror of ProGuard.Core.Models.WatchdogCondition.</summary>
public enum WatchdogTrigger { CpuAbove, RamAbove, RunningLongerThan, InstanceCountAbove }

/// <summary>Mirror of ProGuard.Core.Models.WatchdogAction. New members go at the end: rules are stored by value.</summary>
public enum WatchdogResponse { Terminate, Restart, SetPriority, SetAffinity, LogAlert, SetIoPriority, TrimWorkingSet, Launch, Notify }

/// <summary>Mirror of ProGuard.Core.Models.WatchdogMemoryMetric.</summary>
public enum MemoryMetric { WorkingSet, PrivateBytes }

/// <summary>
/// A persistent per-process rule, applied to every matching process as it starts. Id 0 = new. Every nullable setting
/// is "leave as Windows set it" while null. See ProGuard.Core.Models.ProcessRule for each field's details.
/// </summary>
public sealed class ProcessRuleDto
{
    public int Id { get; set; }

    /// <summary>Executable name ("chrome" and "chrome.exe" are equivalent), wildcard pattern or full path.</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>System.Diagnostics.ProcessPriorityClass value.</summary>
    public int? PriorityClass { get; set; }
    public long? AffinityMask { get; set; }

    /// <summary>Hard CPU cap (job object), percent of the whole machine.</summary>
    public double? CpuLimitPercent { get; set; }
    public int? MaxInstances { get; set; }
    public string? PowerProfileGuid { get; set; }
    public bool ExcludeFromProBalance { get; set; }
    public bool DisallowExecution { get; set; }

    // CPU Limiter (affinity reduction). Off while the threshold is null.
    public double? CpuLimitThresholdPercent { get; set; }
    public int? CpuLimitOverThresholdSeconds { get; set; }
    public int? CpuLimitCoresToRemove { get; set; }
    public int? CpuLimitDurationSeconds { get; set; }

    // Instance limit and Instance Balancer. Balancer off while the mode is null.
    public bool MaxInstancesAllSessions { get; set; }
    public BalancerMode? InstanceBalancerMode { get; set; }
    public int? InstanceBalancerCoresPerInstance { get; set; }
    public bool InstanceBalancerAvoidSmt { get; set; }
    public int? InstanceBalancerReservedCores { get; set; }
    public double? InstanceBalancerMinCpuPercent { get; set; }

    // Startup boost. Off while the priority class is null.
    public int? StartupBoostPriorityClass { get; set; }
    public int? StartupBoostSeconds { get; set; }

    // Persistent per-process defaults.
    /// <summary>IO_PRIORITY_HINT: 0 Very Low, 1 Low, 2 Normal, 3 High.</summary>
    public int? IoPriority { get; set; }
    /// <summary>MEMORY_PRIORITY: 1 Very Low .. 5 Normal.</summary>
    public int? MemoryPriority { get; set; }
    /// <summary>True = EcoQoS on; false = explicitly off (prefers P-cores); null = leave alone.</summary>
    public bool? EfficiencyMode { get; set; }
    public bool? DisablePriorityBoost { get; set; }
    /// <summary>Comma-separated CPU Set IDs (<see cref="CpuSetDto.Id"/>); empty clears the process's CPU Sets.</summary>
    public string? CpuSetIds { get; set; }
    /// <summary>0 Idle, 1 Below Normal, 2 Normal, 3 Above Normal, 4 High.</summary>
    public int? GpuPriority { get; set; }
    /// <summary>0 let Windows decide, 1 power saving, 2 high performance. Takes effect on the next launch.</summary>
    public int? GpuPreference { get; set; }
    public bool ReapplyIfChanged { get; set; }

    /// <summary>Relaunch the process with its last command line whenever it exits.</summary>
    public bool KeepRunning { get; set; }
}

/// <summary>"If this happens to that process for this long, do this." Id 0 = new. See ProGuard.Core.Models.WatchdogRule.</summary>
public sealed class WatchdogRuleDto
{
    public int Id { get; set; }
    public string TargetProcessName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public WatchdogTrigger ConditionType { get; set; }

    /// <summary>CPU percent of the whole machine, megabytes, seconds of run time, or a number of instances.</summary>
    public double ThresholdValue { get; set; }

    /// <summary>How long the condition must hold without a break before the action runs.</summary>
    public int DurationSeconds { get; set; }
    public WatchdogResponse ActionType { get; set; }

    /// <summary>
    /// Priority class name, I/O priority (0-3), affinity mask, command line to launch, notification text, or
    /// "continuous" for a working-set trim.
    /// </summary>
    public string? ActionArgument { get; set; }
    public MemoryMetric MemoryMetric { get; set; } = MemoryMetric.WorkingSet;

    /// <summary>For priority, I/O priority and affinity: put the old value back after this long. 0 keeps the change.</summary>
    public int ActionDurationSeconds { get; set; }

    /// <summary>Fire again for the same process only once the condition clears, or after this long. 0 waits for it to clear.</summary>
    public int CooldownSeconds { get; set; } = 60;
}
