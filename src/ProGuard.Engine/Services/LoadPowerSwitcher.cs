using System;
using System.Collections.Generic;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// The decision half of Process Lasso's load-based power switcher: feed it one system CPU sample at a time and it
/// says which plan the load calls for. It never touches the power plan itself; <see cref="PowerManager"/> decides
/// whether a higher-precedence feature (Performance Mode, IdleSaver) wins. Time comes from the caller, so tests drive
/// it with a fake clock.
/// <list type="bullet">
/// <item>The load is the average of the last <see cref="LoadSwitcherOptions.AveragingWindowSamples"/> samples; no
/// decision is made until the window is full.</item>
/// <item>Above the high threshold the high plan is wanted, below the low threshold the low plan (or, with
/// <see cref="LoadSwitcherOptions.DeferLowPlan"/>, no plan: the one from before comes back). In between, the last
/// decision stands.</item>
/// <item>After a decision, the next change waits until <see cref="LoadSwitcherOptions.MinimumTimeInPlanMs"/> has
/// passed, so a load hovering around a threshold doesn't flip the plan on every sample. The first decision is
/// immediate.</item>
/// </list>
/// </summary>
public sealed class LoadPowerSwitcher
{
    public enum LoadLevel { Undecided, Low, High }

    private readonly Queue<double> _window = new();
    private double _sum;
    private TimeSpan _levelSince;

    public LoadPowerSwitcher(LoadSwitcherOptions options)
    {
        Options = options;
    }

    public LoadSwitcherOptions Options { get; }
    public LoadLevel Level { get; private set; }

    /// <summary>The moving average, or null until the window is full.</summary>
    public double? Average => _window.Count >= Options.AveragingWindowSamples ? _sum / _window.Count : null;

    /// <summary>The plan the load calls for, or null for "no opinion" (undecided, or low load with the low plan deferred).</summary>
    public Guid? RequestedPlan => Level switch
    {
        LoadLevel.High => Options.HighPlan,
        LoadLevel.Low when !Options.DeferLowPlan => Options.LowPlan,
        _ => null,
    };

    /// <summary>Adds a sample taken at <paramref name="now"/>. Returns a description of the change if the level changed.</summary>
    public string? AddSample(double cpuPercent, TimeSpan now)
    {
        if (double.IsNaN(cpuPercent)) return null;
        cpuPercent = Math.Clamp(cpuPercent, 0, 100);

        _window.Enqueue(cpuPercent);
        _sum += cpuPercent;
        while (_window.Count > Options.AveragingWindowSamples) _sum -= _window.Dequeue();
        if (Average is not { } average) return null;

        var target = average > Options.HighThreshold ? LoadLevel.High
                   : average < Options.LowThreshold ? LoadLevel.Low
                   : Level;
        if (target == Level) return null;
        if (Level != LoadLevel.Undecided && now - _levelSince < TimeSpan.FromMilliseconds(Options.MinimumTimeInPlanMs))
            return null;

        Level = target;
        _levelSince = now;
        return target == LoadLevel.High
            ? $"average CPU {average:F1}% is above {Options.HighThreshold:0.##}%"
            : $"average CPU {average:F1}% is below {Options.LowThreshold:0.##}%";
    }
}
