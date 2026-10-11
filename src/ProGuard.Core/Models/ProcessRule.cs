using System.ComponentModel.DataAnnotations;

namespace ProGuard.Core.Models;

public class ProcessRule
{
    [Key]
    public int Id { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public int? PriorityClass { get; set; }
    public long? AffinityMask { get; set; }
    public double? CpuLimitPercent { get; set; }
    public int? MaxInstances { get; set; }
    public string? PowerProfileGuid { get; set; }
    public bool ExcludeFromResponsivenessGuard { get; set; }
    public bool DisallowExecution { get; set; }

    // CPU Limiter, affinity-reduction mode (another process manager's CPULimitRules). Off while the threshold is null;
    // the other three fall back to CpuAffinityLimiter's defaults when null. CpuLimitPercent above is the
    // separate job-object hard cap.
    /// <summary>Total-CPU percent the process must reach before cores are taken away.</summary>
    public double? CpuLimitThresholdPercent { get; set; }
    /// <summary>How long usage must stay at or above the threshold, in seconds.</summary>
    public int? CpuLimitOverThresholdSeconds { get; set; }
    /// <summary>How many cores to remove from the process's affinity (never below one remaining).</summary>
    public int? CpuLimitCoresToRemove { get; set; }
    /// <summary>How long to keep the reduced affinity before restoring the original, in seconds.</summary>
    public int? CpuLimitDurationSeconds { get; set; }

    // Instance count limit (another process manager's InstanceLimitedProcesses): MaxInstances above is the limit, off while null.
    /// <summary>Count instances in every session instead of only the new instance's session.</summary>
    public bool MaxInstancesAllSessions { get; set; }

    // Instance Spread (InstanceManagedCPUAffinities): splits CPU cores across running instances. Off while the
    // mode is null; the other values fall back to InstanceSpread's defaults when null.
    public InstanceSpreadMode? InstanceSpreadMode { get; set; }
    /// <summary>Cores each instance gets in <see cref="Models.InstanceSpreadMode.FixedCoresPerInstance"/> mode.</summary>
    public int? InstanceSpreadCoresPerInstance { get; set; }
    /// <summary>Hand out only the first logical processor of each physical core.</summary>
    public bool InstanceSpreadAvoidSmt { get; set; }
    /// <summary>How many of the lowest cores no instance is given.</summary>
    public int? InstanceSpreadReservedCores { get; set; }
    /// <summary>Total-CPU percent an instance must reach before it gets its own share of cores.</summary>
    public double? InstanceSpreadMinCpuPercent { get; set; }

    // Startup boost (StartupBoostRules): raise a new process's priority class for a while, then restore it.
    // Off while the priority class is null.
    public int? StartupBoostPriorityClass { get; set; }
    /// <summary>How long the boost lasts, 1-3600 seconds.</summary>
    public int? StartupBoostSeconds { get; set; }
}
