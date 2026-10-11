namespace ProGuard.Core.Models;

/// <summary>How the Instance Balancer splits CPU cores across the running instances of a process.</summary>
public enum InstanceBalancerMode
{
    /// <summary>Spread the available cores evenly: with more instances than cores, instances share cores.</summary>
    EqualCoresPerInstance = 0,
    /// <summary>Give each instance the same fixed number of cores, wrapping round when they run out.</summary>
    FixedCoresPerInstance = 1,
}
