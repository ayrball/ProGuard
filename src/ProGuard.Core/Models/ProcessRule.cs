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
}
