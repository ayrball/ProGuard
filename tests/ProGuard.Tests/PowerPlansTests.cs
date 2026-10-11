using Moq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

/// <summary>The plans actually installed (PowerEnumerate), rather than three hardcoded GUIDs.</summary>
public class PowerPlansTests
{
    private static readonly Guid Bitsum = new("7e7c1a77-6191-4f35-95bc-f3e18e688049");

    [Fact]
    public void Enumerate_NamesEachPlan_FallingBackToWellKnownNamesOrTheGuid()
    {
        var names = new Dictionary<Guid, string?> { [Powrprof.Balanced] = "Balanced", [Bitsum] = " Bitsum Highest Performance ", [Powrprof.PowerSaver] = null };
        var plans = PowerPlans.Enumerate(
            new[] { Powrprof.Balanced, Bitsum, Bitsum, Guid.Empty, Powrprof.PowerSaver, Powrprof.HighPerformance },
            id => names.GetValueOrDefault(id));

        Assert.Equal(new[]
        {
            new PowerPlanInfo(Powrprof.Balanced, "Balanced"),
            new PowerPlanInfo(Bitsum, "Bitsum Highest Performance"),
            new PowerPlanInfo(Powrprof.PowerSaver, "Power Saver"),
            new PowerPlanInfo(Powrprof.HighPerformance, "High Performance"),
        }, plans);
    }

    [Fact]
    public void Enumerate_OnThisMachine_ListsTheActivePlan()
    {
        var plans = PowerPlans.Enumerate();
        Assert.NotEmpty(plans);
        Assert.Equal(plans.Count, plans.Select(p => p.Id).Distinct().Count());

        var active = new PowerManager(null, Mock.Of<IUserIdleSource>()).GetActivePlan();
        if (active != Guid.Empty) Assert.Contains(plans, p => p.Id == active);
    }
}
