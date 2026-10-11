using System.ComponentModel.DataAnnotations;

namespace ProGuard.Core.Models;

public enum WatchdogCondition { CpuAbove, RamAbove, RunningLongerThan, InstanceCountAbove }
// New members go at the end: rules are stored by the numeric value.
public enum WatchdogAction { Terminate, Restart, SetPriority, SetAffinity, LogAlert, SetIoPriority, TrimWorkingSet, Launch, Notify }

/// <summary>Which memory figure a <see cref="WatchdogCondition.RamAbove"/> rule compares against its threshold.</summary>
public enum WatchdogMemoryMetric { WorkingSet, PrivateBytes }

public class WatchdogRule
{
    [Key]
    public int Id { get; set; }
    public string TargetProcessName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public WatchdogCondition ConditionType { get; set; }
    /// <summary>CPU percent of the whole machine, megabytes, seconds of run time, or a number of instances.</summary>
    public double ThresholdValue { get; set; }
    /// <summary>How long the condition must hold without a break before the action runs.</summary>
    public int DurationSeconds { get; set; }
    public WatchdogAction ActionType { get; set; }
    /// <summary>
    /// Priority class, I/O priority, affinity mask, command line to launch, notification text, or "continuous" for
    /// a working-set trim; see <c>WatchdogArguments</c>.
    /// </summary>
    public string? ActionArgument { get; set; }

    public WatchdogMemoryMetric MemoryMetric { get; set; } = WatchdogMemoryMetric.WorkingSet;
    /// <summary>For priority, I/O priority and affinity: put the old value back after this long. 0 keeps the change.</summary>
    public int ActionDurationSeconds { get; set; }
    /// <summary>
    /// After firing, the rule fires again for the same process only once the condition clears, or once this long
    /// has passed with the condition still holding. 0 waits for the condition to clear.
    /// </summary>
    public int CooldownSeconds { get; set; } = 60;
}
