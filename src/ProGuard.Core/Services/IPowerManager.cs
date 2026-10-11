using System;
using System.Collections.Generic;

namespace ProGuard.Core.Services;

public interface IPowerManager
{
    IEnumerable<(Guid Id, string Name)> GetAvailablePlans();
    Guid GetActivePlan();
    void SetActivePlan(Guid planId);
    void EnablePerformanceMode(bool enable);
    void ConfigureIdleSaver(bool enable, int timeoutMinutes, Guid idlePlanId);

    /// <summary>Whether Performance Mode is on, manually or automatically.</summary>
    bool IsPerformanceModeActive { get; }

    /// <summary>The plan Performance Mode switches to; <see cref="Guid.Empty"/> = High Performance.</summary>
    void ConfigurePerformanceMode(Guid targetPlan);

    /// <summary>
    /// Automatic Performance Mode: on while a listed or detected game runs. Unlike <see cref="EnablePerformanceMode"/>,
    /// turning it off doesn't end Performance Mode the user turned on themselves.
    /// </summary>
    void SetAutomaticPerformanceMode(bool active, string? reason);

    void ConfigureLoadSwitcher(LoadSwitcherOptions options);

    /// <summary>Plan to switch to when the engine starts; <see cref="Guid.Empty"/> leaves the plan alone.</summary>
    void ConfigureStartupPlan(Guid planId);
}
