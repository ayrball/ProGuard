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
}
