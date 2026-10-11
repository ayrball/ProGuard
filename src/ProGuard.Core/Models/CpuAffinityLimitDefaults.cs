namespace ProGuard.Core.Models;

/// <summary>
/// Values the CPU Limiter's affinity-reduction mode uses when a rule sets a threshold but leaves the other
/// <see cref="ProcessRule"/> fields null.
/// </summary>
public static class CpuAffinityLimitDefaults
{
    public const int OverThresholdSeconds = 5;
    public const int CoresToRemove = 1;
    public const int DurationSeconds = 30;
}
