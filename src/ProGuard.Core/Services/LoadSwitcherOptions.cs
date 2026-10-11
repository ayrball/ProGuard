using System;
using ProGuard.Core.Models;
using ProGuard.Core.Native;

namespace ProGuard.Core.Services;

/// <summary>
/// Settings of the load-based power switcher (Process Lasso's [LoadBasedPowerSwitcher]): the moving average of system
/// CPU over <see cref="AveragingWindowSamples"/> samples picks the high plan above <see cref="HighThreshold"/> and the
/// low plan below <see cref="LowThreshold"/>, staying at least <see cref="MinimumTimeInPlanMs"/> in a plan.
/// </summary>
public sealed record LoadSwitcherOptions
{
    public bool Enabled { get; init; }
    public double LowThreshold { get; init; } = 10.0;
    public double HighThreshold { get; init; } = 50.0;
    public int AveragingWindowSamples { get; init; } = 5;
    public int MinimumTimeInPlanMs { get; init; } = 5000;
    public Guid LowPlan { get; init; } = Powrprof.Balanced;
    public Guid HighPlan { get; init; } = Powrprof.HighPerformance;
    /// <summary>At low load, go back to the plan from before the switcher went high instead of using <see cref="LowPlan"/>.</summary>
    public bool DeferLowPlan { get; init; } = true;

    public const int MaxAveragingWindowSamples = 600;
    public const int MaxMinimumTimeInPlanMs = 3_600_000;

    /// <summary>Why these settings can't be used, or null if they can.</summary>
    public string? Validate()
    {
        if (!(LowThreshold >= 0 && LowThreshold <= 100)) return "The low CPU threshold must be between 0 and 100%.";
        if (!(HighThreshold >= 0 && HighThreshold <= 100)) return "The high CPU threshold must be between 0 and 100%.";
        if (!(HighThreshold > LowThreshold)) return "The high CPU threshold must be greater than the low one.";
        if (AveragingWindowSamples < 1 || AveragingWindowSamples > MaxAveragingWindowSamples)
            return $"The averaging window must be between 1 and {MaxAveragingWindowSamples} samples.";
        if (MinimumTimeInPlanMs < 0 || MinimumTimeInPlanMs > MaxMinimumTimeInPlanMs)
            return "The minimum time in a power plan must be between 0 and 3,600,000 ms.";
        if (HighPlan == Guid.Empty) return "Choose a high-load power plan.";
        if (!DeferLowPlan && LowPlan == Guid.Empty) return "Choose a low-load power plan.";
        return null;
    }

    public static LoadSwitcherOptions FromSettings(AppSettings s) => new()
    {
        Enabled = s.LoadSwitcherEnabled,
        LowThreshold = s.LoadSwitcherLowThreshold,
        HighThreshold = s.LoadSwitcherHighThreshold,
        AveragingWindowSamples = s.LoadSwitcherAveragingWindowSamples,
        MinimumTimeInPlanMs = s.LoadSwitcherMinimumTimeInPlanMs,
        LowPlan = Guid.TryParse(s.LoadSwitcherLowPlanGuid, out var low) ? low : Powrprof.Balanced,
        HighPlan = Guid.TryParse(s.LoadSwitcherHighPlanGuid, out var high) ? high : Powrprof.HighPerformance,
        DeferLowPlan = s.LoadSwitcherDeferLowPlan,
    };
}
