using Moq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

/// <summary>The plans actually installed (PowerEnumerate), rather than three hardcoded GUIDs.</summary>
public class PowerPlansTests
{
    private static readonly Guid Vendor = new("3c1e5f2a-8b4d-4e6f-9a07-5d2c1b8e4f60");

    [Fact]
    public void Enumerate_NamesEachPlan_FallingBackToWellKnownNamesOrTheGuid()
    {
        var names = new Dictionary<Guid, string?> { [Powrprof.Balanced] = "Balanced", [Vendor] = " Vendor Max Performance ", [Powrprof.PowerSaver] = null };
        var plans = PowerPlans.Enumerate(
            new[] { Powrprof.Balanced, Vendor, Vendor, Guid.Empty, Powrprof.PowerSaver, Powrprof.HighPerformance },
            id => names.GetValueOrDefault(id));

        Assert.Equal(new[]
        {
            new PowerPlanInfo(Powrprof.Balanced, "Balanced"),
            new PowerPlanInfo(Vendor, "Vendor Max Performance"),
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

        var active = new PowerManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<PowerManager>.Instance, Mock.Of<IUserIdleSource>()).GetActivePlan();
        if (active != Guid.Empty) Assert.Contains(plans, p => p.Id == active);
    }
}
