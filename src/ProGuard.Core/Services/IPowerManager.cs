using System;
using System.Collections.Generic;

namespace ProGuard.Core.Services;

public interface IPowerManager
{
    IEnumerable<(Guid Id, string Name)> GetAvailablePlans();
    Guid GetActivePlan();
    /// <summary>
    /// The user's own choice of plan. It is applied now and becomes the plan to return to. A feature that currently
    /// holds the plan (Performance Mode, IdleSaver, the load switcher) does not take it back or restore over it; its
    /// next decision applies as usual. Callers check <see cref="GetActivePlan"/> afterwards to see whether Windows accepted it.
    /// </summary>
    void SetActivePlan(Guid planId);
    void EnablePerformanceMode(bool enable);
    void ConfigureIdleSaver(bool enable, int timeoutMinutes, Guid idlePlanId);

    /// <summary>Whether Performance Mode is on, manually or automatically.</summary>
    bool IsPerformanceModeActive { get; }

    /// <summary>Performance Mode is on only because a listed or detected game is running, not because the user turned it on.</summary>
    bool IsPerformanceModeAutomatic { get; }

    /// <summary>
    /// Who set the active plan: "User", "PerformanceMode", "IdleSaver", "LoadSwitcher" or "StartWith". A feature that
    /// holds the plan loses it to a plan the user picks (<see cref="SetActivePlan"/>), after which this says "User".
    /// </summary>
    string PlanOwnerName { get; }

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
