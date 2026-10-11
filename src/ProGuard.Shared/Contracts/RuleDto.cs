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

    // Instance limit, Instance Balancer and startup boost: see ProcessRule. Null limit / mode / boost priority = off.
    public int? MaxInstances { get; set; }
    public bool MaxInstancesAllSessions { get; set; }
    /// <summary>ProGuard.Core.Models.InstanceBalancerMode as an int (0 = equal cores, 1 = fixed cores per instance).</summary>
    public int? InstanceBalancerMode { get; set; }
    public int? InstanceBalancerCoresPerInstance { get; set; }
    public bool InstanceBalancerAvoidSmt { get; set; }
    public int? InstanceBalancerReservedCores { get; set; }
    public double? InstanceBalancerMinCpuPercent { get; set; }
    /// <summary>System.Diagnostics.ProcessPriorityClass value.</summary>
    public int? StartupBoostPriorityClass { get; set; }
    public int? StartupBoostSeconds { get; set; }
}
