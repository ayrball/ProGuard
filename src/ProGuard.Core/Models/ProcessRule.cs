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
    public bool ExcludeFromProBalance { get; set; }
    public bool DisallowExecution { get; set; }

    // CPU Limiter, affinity-reduction mode (Process Lasso's CPULimitRules). Off while the threshold is null;
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
}
