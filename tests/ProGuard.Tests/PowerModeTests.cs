using System.Windows.Threading;
using Moq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>
/// The Windows 11 power mode (the power-mode slider, an overlay on the plan): shown and set from the System page, and
/// optionally held at Best performance by Performance Mode, which puts the user's mode back afterwards.
/// </summary>
public class PowerModeTests
{
    private sealed class FakeIdle : IUserIdleSource
    {
        public TimeSpan? GetIdleTime() => TimeSpan.Zero;
    }

    private sealed class FakeOverlay : IPowerModeOverlay
    {
        public Guid? Mode { get; set; } = PowerModes.Balanced;
        public int Writes { get; private set; }
        public Guid? Read() => Mode;

        public bool Write(Guid mode)
        {
            if (Mode == null) return false;
            Writes++;
            Mode = mode;
            return true;
        }
    }

    private readonly FakeOverlay _overlay = new();
    private Guid _plan = Powrprof.Balanced;
    private readonly PowerManager _power;

    public PowerModeTests()
    {
        _power = new PowerManager(null, new FakeIdle(), () => _plan, p => { _plan = p; return true; }, overlay: _overlay);
    }

    // ------------------------------------------------------------------ Performance Mode and the power mode

    [Fact]
    public void Off_ByDefault_PerformanceModeLeavesThePowerModeAlone()
    {
        Assert.False(new ProGuard.Core.Models.AppSettings().PerformanceModeSetBestPerformance);
        _power.EnablePerformanceMode(true);
        _power.EnablePerformanceMode(false);
        Assert.Equal(0, _overlay.Writes);
    }

    [Fact]
    public void PerformanceMode_HoldsBestPerformance_ThenPutsTheUsersModeBack()
    {
        _overlay.Mode = PowerModes.BestPowerEfficiency;
        _power.ConfigurePerformanceModePowerMode(true);
        Assert.Equal(PowerModes.BestPowerEfficiency, _overlay.Mode); // nothing until Performance Mode starts

        _power.EnablePerformanceMode(true);
        Assert.Equal(PowerModes.BestPerformance, _overlay.Mode);

        _power.CheckIdle(); // the 1 s loop re-arbitrates: nothing more is written
        Assert.Equal(1, _overlay.Writes);

        _power.EnablePerformanceMode(false);
        Assert.Equal(PowerModes.BestPowerEfficiency, _overlay.Mode);
    }

    [Fact]
    public void AutomaticPerformanceMode_DoesTheSame()
    {
        _power.ConfigurePerformanceModePowerMode(true);
        _power.SetAutomaticPerformanceMode(true, "game.exe");
        Assert.Equal(PowerModes.BestPerformance, _overlay.Mode);
        _power.SetAutomaticPerformanceMode(false, null);
        Assert.Equal(PowerModes.Balanced, _overlay.Mode);
    }

    [Fact]
    public void AlreadyBestPerformance_NothingIsOwedAfterwards()
    {
        _overlay.Mode = PowerModes.BestPerformance;
        _power.ConfigurePerformanceModePowerMode(true);
        _power.EnablePerformanceMode(true);
        _power.EnablePerformanceMode(false);
        Assert.Equal(PowerModes.BestPerformance, _overlay.Mode);
        Assert.Equal(0, _overlay.Writes);
    }

    [Fact]
    public void TheUsersOwnChoice_DuringPerformanceMode_Stays()
    {
        _power.ConfigurePerformanceModePowerMode(true);
        _power.EnablePerformanceMode(true);

        Assert.True(_power.SetPowerMode(PowerModes.BestPowerEfficiency));
        _power.EnablePerformanceMode(false);
        Assert.Equal(PowerModes.BestPowerEfficiency, _overlay.Mode);
    }

    [Fact]
    public void AChangeMadeOutsideProGuard_DuringPerformanceMode_Stays()
    {
        _power.ConfigurePerformanceModePowerMode(true);
        _power.EnablePerformanceMode(true);

        _overlay.Mode = PowerModes.BestPowerEfficiency; // Windows' own slider
        _power.EnablePerformanceMode(false);
        Assert.Equal(PowerModes.BestPowerEfficiency, _overlay.Mode);
    }

    [Fact]
    public void TheOption_TakesEffectAtOnce_WhilePerformanceModeIsOn()
    {
        _power.EnablePerformanceMode(true);
        _power.ConfigurePerformanceModePowerMode(true);
        Assert.Equal(PowerModes.BestPerformance, _overlay.Mode);
        _power.ConfigurePerformanceModePowerMode(false);
        Assert.Equal(PowerModes.Balanced, _overlay.Mode);
    }

    [Fact]
    public async Task StoppingTheEngine_PutsTheModeBack()
    {
        _power.ConfigurePerformanceModePowerMode(true);
        _power.EnablePerformanceMode(true);
        await _power.StopAsync(CancellationToken.None);
        Assert.Equal(PowerModes.Balanced, _overlay.Mode);
    }

    [Fact]
    public void WithoutASlider_NothingHappens()
    {
        _overlay.Mode = null;
        _power.ConfigurePerformanceModePowerMode(true);
        _power.EnablePerformanceMode(true);
        Assert.Null(_power.GetPowerMode());
        Assert.False(_power.SetPowerMode(PowerModes.BestPerformance));
        Assert.Equal(0, _overlay.Writes);
    }

    [Fact]
    public void AFakedPlanApi_NeverTouchesTheRealSlider()
    {
        var power = new PowerManager(null, new FakeIdle(), () => Powrprof.Balanced, _ => true);
        Assert.Null(power.GetPowerMode());
    }

    [Fact]
    public void Windows_ReadsTheSlider_WhereThereIsOne()
    {
        // Windows 11 has one; a server may not. Either way the call must not throw.
        var mode = PowerModes.ReadActual();
        if (mode is { } m) Assert.False(string.IsNullOrEmpty(PowerModes.Describe(m)));
    }

    // ------------------------------------------------------------------ through the command pipe

    [Fact]
    public void GetPower_ReportsTheModeAndTheChoices()
    {
        var h = new ThinHarness();
        h.Power.Setup(p => p.GetPowerMode()).Returns(PowerModes.BestPowerEfficiency);

        var power = h.Data<PowerStateDto>(h.Send(new ThinCaller(), CommandTypes.GetPower));
        Assert.True(power.PowerModeSupported);
        Assert.Equal(PowerModes.BestPowerEfficiency, power.PowerModeId);
        Assert.Equal(new[] { "Best power efficiency", "Balanced", "Best performance" }, power.PowerModes.Select(m => m.Name).ToArray());

        h.Power.Setup(p => p.GetPowerMode()).Returns((Guid?)null);
        Assert.False(h.Data<PowerStateDto>(h.Send(new ThinCaller(), CommandTypes.GetPower)).PowerModeSupported);
    }

    [Fact]
    public void AnyUser_MayMoveTheSlider_ToAModeItOffers()
    {
        var h = new ThinHarness();
        h.Power.Setup(p => p.GetPowerMode()).Returns(PowerModes.Balanced);
        h.Power.Setup(p => p.SetPowerMode(It.IsAny<Guid>())).Returns(true);

        Assert.True(h.Send(new ThinCaller(), CommandTypes.SetPowerMode, argument: PowerModes.BestPerformance.ToString()).Success);
        h.Power.Verify(p => p.SetPowerMode(PowerModes.BestPerformance), Times.Once);
        Assert.True(h.Send(new ThinCaller(), CommandTypes.SetPowerMode, argument: Guid.Empty.ToString()).Success);

        Assert.Contains("Invalid power mode", h.SendAsAdmin(CommandTypes.SetPowerMode, argument: "not a guid").Error);
        Assert.Contains("Invalid power mode", h.SendAsAdmin(CommandTypes.SetPowerMode, argument: Powrprof.HighPerformance.ToString()).Error);
    }

    [Fact]
    public void SetPowerMode_SaysSo_WhenWindowsHasNoSliderOrRefuses()
    {
        var h = new ThinHarness();
        h.Power.Setup(p => p.GetPowerMode()).Returns((Guid?)null);
        Assert.Contains("no power mode", h.SendAsAdmin(CommandTypes.SetPowerMode, argument: PowerModes.BestPerformance.ToString()).Error);

        h.Power.Setup(p => p.GetPowerMode()).Returns(PowerModes.Balanced);
        h.Power.Setup(p => p.SetPowerMode(It.IsAny<Guid>())).Returns(false);
        Assert.Contains("wouldn't", h.SendAsAdmin(CommandTypes.SetPowerMode, argument: PowerModes.BestPerformance.ToString()).Error);
    }

    [Fact]
    public void TheSettingReachesThePowerManager()
    {
        var h = new ThinHarness();
        var settings = h.Data<EngineSettingsDto>(h.SendAsAdmin(CommandTypes.GetSettings));
        settings.PerformanceModeSetBestPerformance = true;
        Assert.True(h.Send(new ThinCaller(), CommandTypes.SaveSettings, argument: settings).Success);
        h.Power.Verify(p => p.ConfigurePerformanceModePowerMode(true), Times.Once);
    }

    // ------------------------------------------------------------------ the System page

    private static SystemViewModel NewPage()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var toaster = new Toaster(dispatcher);
        var hub = new EngineHub(new EngineClient("ProGuard.Tests.NoEngine." + Guid.NewGuid().ToString("N")), dispatcher, toaster);
        return new ShellViewModel(hub, toaster, new IconCache(dispatcher), new NoStartup()).System;
    }

    private sealed class NoStartup : IStartupRegistration
    {
        public bool IsEnabled => false;
        public void Enable() { }
        public void Disable() { }
    }

    private static PowerStateDto Power(Guid plan, bool supported = true, Guid? mode = null) => new()
    {
        Plans =
        {
            new PowerPlanDto { Id = Powrprof.Balanced, Name = "Balanced" },
            new PowerPlanDto { Id = Powrprof.HighPerformance, Name = "High performance" },
        },
        ActivePlanId = plan,
        PowerModeSupported = supported,
        PowerModeId = mode ?? Guid.Empty,
        PowerModes = PowerModes.Offered.Select(m => new PowerModeDto { Id = m.Id, Name = m.Name }).ToList(),
    };

    [Fact]
    public void Page_ShowsTheSlider_WithTheModeInForceLit()
    {
        var vm = NewPage();
        vm.Load(new EngineSettingsDto(), Power(Powrprof.Balanced, mode: PowerModes.BestPerformance));

        Assert.True(vm.PowerModeSupported);
        Assert.Equal(new[] { "BEST POWER EFFICIENCY", "BALANCED", "BEST PERFORMANCE" }, vm.PowerModes.Select(m => m.Label).ToArray());
        Assert.Equal(PowerModes.BestPerformance, vm.PowerModes.Single(m => m.IsOn).Value);
        Assert.DoesNotContain("only under", vm.PowerModeNote);
    }

    [Fact]
    public void Page_SaysWhenThePlanIgnoresTheMode_AndShowsAModeItDoesntOffer()
    {
        var vm = NewPage();
        vm.Load(new EngineSettingsDto(), Power(Powrprof.HighPerformance, mode: PowerModes.BetterPerformance));

        Assert.Contains("only under the Balanced plan", vm.PowerModeNote);
        var other = vm.PowerModes.Single(m => m.IsOn);
        Assert.Equal(PowerModes.BetterPerformance, other.Value);
        Assert.StartsWith("OTHER", other.Label);
    }

    [Fact]
    public void Page_HidesTheSlider_WhereWindowsHasNone()
    {
        var vm = NewPage();
        vm.Load(new EngineSettingsDto(), Power(Powrprof.Balanced, supported: false));
        Assert.False(vm.PowerModeSupported);
        Assert.DoesNotContain(vm.PowerModes, m => m.IsOn);
    }
}
