using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>The power settings reaching the engine services and the Power page.</summary>
public class PowerSettingsTests
{
    private static readonly Guid Bitsum = new("7e7c1a77-6191-4f35-95bc-f3e18e688049");

    // ---------- SettingsApplier ----------

    private sealed class Harness
    {
        public Mock<IProcessMonitor> Monitor { get; } = new();
        public Mock<ISmartTrimService> SmartTrim { get; } = new();
        public Mock<IPowerManager> Power { get; } = new();
        public Mock<IProBalanceEngine> ProBalance { get; } = new();
        public Mock<IAutoPerformanceMode> Auto { get; } = new();
        public SettingsApplier Applier { get; }

        public Harness() =>
            Applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, Monitor.Object, SmartTrim.Object, Power.Object, ProBalance.Object, Auto.Object);
    }

    [Fact]
    public void SettingsApplier_AppliesThePowerFeatures()
    {
        var h = new Harness();
        h.Applier.Apply(new AppSettings
        {
            PerformanceModePlanGuid = Bitsum.ToString(),
            AutoPerformanceModeProcesses = "a.exe;b.exe",
            AutoPerformanceModeDetectGames = true,
            LoadSwitcherEnabled = true,
            LoadSwitcherLowThreshold = 15,
            LoadSwitcherHighThreshold = 70,
            LoadSwitcherDeferLowPlan = false,
            StartWithPowerPlanGuid = Powrprof.Balanced.ToString(),
        });

        h.Power.Verify(p => p.ConfigurePerformanceMode(Bitsum), Times.Once);
        h.Auto.Verify(a => a.Configure(It.Is<AutoPerformanceModeOptions>(o =>
            o.DetectGames && !o.EngageForSteam && o.Processes.SequenceEqual(new[] { "a.exe", "b.exe" }))), Times.Once);
        h.Power.Verify(p => p.ConfigureLoadSwitcher(It.Is<LoadSwitcherOptions>(o =>
            o.Enabled && o.LowThreshold == 15 && o.HighThreshold == 70 && !o.DeferLowPlan
            && o.LowPlan == Powrprof.Balanced && o.HighPlan == Powrprof.HighPerformance)), Times.Once);
        h.Power.Verify(p => p.ConfigureStartupPlan(Powrprof.Balanced), Times.Once);
    }

    [Fact]
    public void SettingsApplier_ReappliesThePowerFeaturesOnlyWhenTheyChange()
    {
        var h = new Harness();
        var settings = new AppSettings { AutoPerformanceModeProcesses = "a.exe" };
        h.Applier.Apply(settings);
        h.Applier.Apply(settings.Clone());
        h.Power.Verify(p => p.ConfigurePerformanceMode(It.IsAny<Guid>()), Times.Once);
        h.Power.Verify(p => p.ConfigureLoadSwitcher(It.IsAny<LoadSwitcherOptions>()), Times.Once);
        h.Auto.Verify(a => a.Configure(It.IsAny<AutoPerformanceModeOptions>()), Times.Once);

        var changed = settings.Clone();
        changed.LoadSwitcherHighThreshold = 60;
        h.Applier.Apply(changed);
        h.Power.Verify(p => p.ConfigureLoadSwitcher(It.IsAny<LoadSwitcherOptions>()), Times.Exactly(2));
        h.Auto.Verify(a => a.Configure(It.IsAny<AutoPerformanceModeOptions>()), Times.Once);

        var changedList = changed.Clone();
        changedList.AutoPerformanceModeExclusions = "c.exe";
        h.Applier.Apply(changedList);
        h.Auto.Verify(a => a.Configure(It.IsAny<AutoPerformanceModeOptions>()), Times.Exactly(2));
    }

    [Fact]
    public void SettingsApplier_UnsetPlans_MeanTheDefaults()
    {
        var h = new Harness();
        h.Applier.Apply(new AppSettings());
        h.Power.Verify(p => p.ConfigurePerformanceMode(Guid.Empty), Times.Once);   // = High Performance
        h.Power.Verify(p => p.ConfigureStartupPlan(Guid.Empty), Times.Once);       // = leave it alone
        h.Power.Verify(p => p.ConfigureLoadSwitcher(It.Is<LoadSwitcherOptions>(o => !o.Enabled)), Times.Once);
    }

    // ---------- the Power page ----------

}
