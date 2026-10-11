namespace ProGuard.Shared.Contracts;

public class RuleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    // CPU Limiter, affinity-reduction mode: see ProcessRule. Null threshold = off.
    public double? CpuLimitThresholdPercent { get; set; }
    public int? CpuLimitOverThresholdSeconds { get; set; }
    public int? CpuLimitCoresToRemove { get; set; }
    public int? CpuLimitDurationSeconds { get; set; }

    // Instance limit, Instance Spread and startup boost: see ProcessRule. Null limit / mode / boost priority = off.
    public int? MaxInstances { get; set; }
    public bool MaxInstancesAllSessions { get; set; }
    /// <summary>ProGuard.Core.Models.InstanceSpreadMode as an int (0 = equal cores, 1 = fixed cores per instance).</summary>
    public int? InstanceSpreadMode { get; set; }
    public int? InstanceSpreadCoresPerInstance { get; set; }
    public bool InstanceSpreadAvoidSmt { get; set; }
    public int? InstanceSpreadReservedCores { get; set; }
    public double? InstanceSpreadMinCpuPercent { get; set; }
    /// <summary>System.Diagnostics.ProcessPriorityClass value.</summary>
    public int? StartupBoostPriorityClass { get; set; }
    public int? StartupBoostSeconds { get; set; }

    // Persistent per-process defaults; same meaning and ranges as on ProcessRule. Null leaves the setting alone.
    public int? IoPriority { get; set; }
    public int? MemoryPriority { get; set; }
    public bool? EfficiencyMode { get; set; }
    public bool? DisablePriorityBoost { get; set; }
    public string? CpuSetIds { get; set; }
    public int? GpuPriority { get; set; }
    public int? GpuPreference { get; set; }
    public bool ReapplyIfChanged { get; set; }
}
