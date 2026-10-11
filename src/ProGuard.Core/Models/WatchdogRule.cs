using System.ComponentModel.DataAnnotations;

namespace ProGuard.Core.Models;

public enum WatchdogCondition { CpuAbove, RamAbove, RunningLongerThan, InstanceCountAbove }
public enum WatchdogAction { Terminate, Restart, SetPriority, SetAffinity, LogAlert }

public class WatchdogRule
{
    [Key]
    public int Id { get; set; }
    public string TargetProcessName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public WatchdogCondition ConditionType { get; set; }
    public double ThresholdValue { get; set; }
    public int DurationSeconds { get; set; }
    public WatchdogAction ActionType { get; set; }
    public string? ActionArgument { get; set; }
}
