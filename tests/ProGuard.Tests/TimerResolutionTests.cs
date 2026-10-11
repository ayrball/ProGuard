using System.Text.Json;
using System.Windows.Threading;
using ProGuard.Core.Models;
using ProGuard.Engine.Services;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>Windows' timer in memory: one process's requests, over a 0.5-15.625 ms timer that some other program holds at 1 ms.</summary>
internal sealed class FakeTimerNative : ITimerResolutionNative
{
    public const uint Coarsest = 156250;
    public const uint Finest = 5000;
    public const uint OtherProgram = 10000;

    public List<(uint Resolution, bool Set)> Calls { get; } = new();
    public uint? Ours { get; private set; }
    public bool Refuse { get; set; }
    public bool GlobalRequestsSupported { get; set; } = true;
    public bool Global { get; private set; }

    public TimerResolutions? Query() => new(Coarsest, Finest, Math.Min(OtherProgram, Ours ?? uint.MaxValue));

    public uint? Set(uint resolution, bool set)
    {
        Calls.Add((resolution, set));
        if (Refuse) return null;
        if (set) Ours = resolution;
        else if (Ours == resolution) Ours = null;
        return Query()!.Value.Current;
    }

    public bool ReadGlobalRequests() => Global;
    public void WriteGlobalRequests(bool enabled) => Global = enabled;
}

/// <summary>
/// The system timer resolution (Process Lasso's SetTimerResolutionAtStartup): the engine holds the configured request,
/// administrators alone may change it, and the System page shows the timer's current, minimum and maximum values.
/// </summary>
public class TimerResolutionTests
{
    // ------------------------------------------------------------------ the service

    [Fact]
    public void Configure_HoldsOneRequest_KeptInRange_AndWithdrawsIt()
    {
        var native = new FakeTimerNative();
        var timer = new TimerResolutionService(native: native);

        timer.Configure(5000);
        Assert.Equal(5000u, native.Ours);
        Assert.Equal(5000u, timer.Held);

        // A new value replaces the old request rather than adding a second one.
        timer.Configure(20000);
        Assert.Equal(new[] { (5000u, true), (5000u, false), (20000u, true) }, native.Calls);
        Assert.Equal(20000u, native.Ours);

        // Finer than the timer can go, or coarser: kept within its range.
        timer.Configure(1);
        Assert.Equal(FakeTimerNative.Finest, native.Ours);
        timer.Configure(900_000);
        Assert.Equal(FakeTimerNative.Coarsest, native.Ours);

        timer.Configure(0);
        Assert.Null(native.Ours);
        Assert.Equal(0u, timer.Held);
        Assert.Equal(0, timer.Requested);
    }

    [Fact]
    public void Configure_TheSameValueAgain_DoesNothing_ButRetriesARefusedOne()
    {
        var native = new FakeTimerNative { Refuse = true };
        var timer = new TimerResolutionService(native: native);

        timer.Configure(5000);
        Assert.Equal(0u, timer.Held);

        native.Refuse = false;
        timer.Configure(5000);
        Assert.Equal(5000u, timer.Held);

        int calls = native.Calls.Count;
        timer.Configure(5000);
        Assert.Equal(calls, native.Calls.Count);
    }

    [Fact]
    public void Release_WithdrawsTheRequest_WhenTheEngineStops()
    {
        var native = new FakeTimerNative();
        var timer = new TimerResolutionService(native: native);
        timer.Configure(5000);

        timer.Release();
        timer.Release();

        Assert.Null(native.Ours);
        Assert.Single(native.Calls, c => !c.Set);
    }

    [Fact]
    public void State_ReportsTheTimerAndTheRequest()
    {
        var native = new FakeTimerNative();
        var timer = new TimerResolutionService(native: native);
        timer.Configure(5000);
        timer.SetGlobalRequests(true);

        var state = timer.GetState();
        Assert.Equal(new TimerResolutionState(5000, FakeTimerNative.Coarsest, FakeTimerNative.Finest, 5000, 5000, true, true), state);
    }

    [Fact]
    public void Windows_ReportsAnOrderedRange_AndGrantsARequestWithinIt()
    {
        var native = new Win32TimerResolution();
        var range = native.Query();
        Assert.NotNull(range);
        var (coarsest, finest, current) = (range!.Value.Minimum, range.Value.Maximum, range.Value.Current);
        Assert.True(finest > 0 && finest <= current && current <= coarsest, $"finest {finest}, current {current}, coarsest {coarsest}");

        // Unelevated is fine: the request is this test process's own, and goes when withdrawn.
        var timer = new TimerResolutionService(native: native);
        timer.Configure((int)coarsest);
        Assert.Equal(coarsest, timer.Held);
        timer.Release();
        Assert.Equal(0u, timer.Held);
    }

    // ------------------------------------------------------------------ through the command pipe

    [Fact]
    public void AnyoneMayReadTheTimer()
    {
        var h = new ThinHarness();
        var state = h.Data<TimerResolutionDto>(h.Send(new ThinCaller(), CommandTypes.GetTimerResolution));
        Assert.Equal((int)FakeTimerNative.OtherProgram, state.Current);
        Assert.Equal((int)FakeTimerNative.Coarsest, state.Minimum);
        Assert.Equal((int)FakeTimerNative.Finest, state.Maximum);
        Assert.Equal(0, state.Requested);
        Assert.Equal(0, state.Held);
    }

    [Fact]
    public void AnAdministrator_SetsItAtOnce_AndItIsSaved()
    {
        var h = new ThinHarness();
        var state = h.Data<TimerResolutionDto>(h.SendAsAdmin(CommandTypes.SetTimerResolution, argument: new TimerResolutionArgument { Resolution = 5000, GlobalRequests = true }));

        Assert.Equal(5000, state.Requested);
        Assert.Equal(5000, state.Held);
        Assert.Equal(5000, state.Current);
        Assert.True(state.GlobalRequests);
        Assert.Equal(5000, h.Stored.SetTimerResolutionAtStartup);
        Assert.Equal(5000u, h.Timer.Ours);
        Assert.True(h.Timer.Global);

        h.Data<TimerResolutionDto>(h.SendAsAdmin(CommandTypes.SetTimerResolution, argument: new TimerResolutionArgument { Resolution = 0 }));
        Assert.Null(h.Timer.Ours);
        Assert.True(h.Timer.Global); // null leaves the switch alone
        Assert.Equal(0, h.Stored.SetTimerResolutionAtStartup);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void OutOfRangeValues_AreRefused(int resolution)
    {
        var h = new ThinHarness();
        Assert.False(h.SendAsAdmin(CommandTypes.SetTimerResolution, argument: new TimerResolutionArgument { Resolution = resolution }).Success);
        var settings = DtoMapper(h);
        settings.SetTimerResolutionAtStartup = resolution;
        Assert.False(h.SendAsAdmin(CommandTypes.SaveSettings, argument: settings).Success);
        Assert.Null(h.Timer.Ours);
    }

    [Fact]
    public void TheGlobalSwitch_IsRefusedWhereWindowsHasNone()
    {
        var h = new ThinHarness();
        h.Timer.GlobalRequestsSupported = false;
        var result = h.SendAsAdmin(CommandTypes.SetTimerResolution, argument: new TimerResolutionArgument { Resolution = 5000, GlobalRequests = true });
        Assert.False(result.Success);
        Assert.Contains("already system-wide", result.Error);
        Assert.Equal(0, h.Stored.SetTimerResolutionAtStartup); // nothing half-done
    }

    [Fact]
    public void AStandardUser_MayNotChangeIt_ThroughAnyCommand()
    {
        var h = new ThinHarness();
        var user = new ThinCaller();

        Assert.Contains("Access denied", h.Send(user, CommandTypes.SetTimerResolution, argument: new TimerResolutionArgument { Resolution = 5000 }).Error);

        var settings = DtoMapper(h);
        settings.SetTimerResolutionAtStartup = 5000;
        var refused = h.Send(user, CommandTypes.SaveSettings, argument: settings);
        Assert.Contains("Access denied", refused.Error);
        Assert.Contains("timer resolution", refused.Error);

        // The legacy whole-settings request too.
        var legacy = h.Dispatcher.Dispatch(new CommandMessage { Type = MessageType.SetSetting, Payload = JsonSerializer.Serialize(new AppSettings { SetTimerResolutionAtStartup = 5000 }) }, user);
        Assert.Contains("Access denied", JsonSerializer.Deserialize<CommandResult>(legacy.Payload)!.Error);

        Assert.Equal(0, h.Stored.SetTimerResolutionAtStartup);
        Assert.Null(h.Timer.Ours);
    }

    [Fact]
    public void AStandardUser_StillSavesEverythingElse_WithTheTimerSentBackUnchanged()
    {
        var h = new ThinHarness();
        h.SendAsAdmin(CommandTypes.SetTimerResolution, argument: new TimerResolutionArgument { Resolution = 5000 });

        var settings = DtoMapper(h);
        settings.SmartTrimEnabled = true;
        Assert.True(h.Send(new ThinCaller(), CommandTypes.SaveSettings, argument: settings).Success);
        Assert.True(h.Stored.SmartTrimEnabled);
        Assert.Equal(5000u, h.Timer.Ours);
    }

    [Fact]
    public void TheSettingIsAppliedWhenTheEngineStarts()
    {
        var native = new FakeTimerNative();
        var timer = new TimerResolutionService(native: native);
        var applier = new SettingsApplier(Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsApplier>.Instance, new FakeProcessMonitor(),
            new Moq.Mock<ProGuard.Core.Services.ISmartTrimService>().Object, new Moq.Mock<ProGuard.Core.Services.IPowerManager>().Object,
            new Moq.Mock<ProGuard.Core.Services.IProBalanceEngine>().Object, timerResolution: timer);

        applier.Apply(new AppSettings { SetTimerResolutionAtStartup = 10000 });
        Assert.Equal(10000u, native.Ours);
    }

    private static EngineSettingsDto DtoMapper(ThinHarness h) => h.Data<EngineSettingsDto>(h.SendAsAdmin(CommandTypes.GetSettings));

    // ------------------------------------------------------------------ the System page

    private static SystemViewModel NewPage()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var toaster = new Toaster(dispatcher);
        var hub = new EngineHub(new EngineClient("ProGuard.Tests.NoEngine." + Guid.NewGuid().ToString("N")), dispatcher, toaster);
        var vm = new ShellViewModel(hub, toaster, new IconCache(dispatcher), new NoStartup()).System;
        vm.SaveDelay = TimeSpan.Zero;
        return vm;
    }

    private sealed class NoStartup : IStartupRegistration
    {
        public bool IsEnabled => false;
        public void Enable() { }
        public void Disable() { }
    }

    private static TimerResolutionDto Timer(int requested = 0, int held = 0) => new()
    {
        Current = 10000, Minimum = 156250, Maximum = 5000, Requested = requested, Held = held,
        GlobalRequestsSupported = true, GlobalRequests = true,
    };

    [Fact]
    public void Page_ShowsCurrentMinimumAndMaximum_AndOffersStepsWithinTheRange()
    {
        var vm = NewPage();
        vm.Load(new EngineSettingsDto(), null, Timer(requested: 5000, held: 5000));

        Assert.Equal("1 MS", vm.TimerCurrentText);
        Assert.Equal("15.625 MS", vm.TimerMinimumText);
        Assert.Equal("0.5 MS", vm.TimerMaximumText);
        Assert.Equal(new[] { "DON'T ASK", "FINEST · 0.5 MS", "1 MS", "2 MS", "5 MS" }, vm.TimerOptions.Select(o => o.Label).ToArray());
        Assert.Equal(5000, vm.TimerOptions.Single(o => o.IsOn).Value);
        Assert.Equal("THE ENGINE HOLDS 0.5 MS", vm.TimerStatusText);
        Assert.True(vm.TimerGlobalSupported && vm.TimerGlobalRequests);
    }

    [Fact]
    public void Page_KeepsAStoredValueThatIsntAStep()
    {
        var vm = NewPage();
        vm.Load(new EngineSettingsDto(), null, Timer(requested: 7777, held: 7777));
        var on = vm.TimerOptions.Single(o => o.IsOn);
        Assert.Equal(7777, on.Value);
        Assert.Equal("SET · 0.778 MS", on.Label);
    }

    [Fact]
    public void Page_NeverChangesTheTimerWhenSavingOtherSettings()
    {
        var vm = NewPage();
        var saved = new List<EngineSettingsDto>();
        vm.Saver = dto => { saved.Add(dto); return Task.FromResult<EngineSettingsDto?>(dto); };
        vm.Load(new EngineSettingsDto { SetTimerResolutionAtStartup = 5000 }, null, Timer(requested: 5000, held: 5000));

        vm.SmartTrimEnabled = true;
        Assert.Equal(5000, vm.BuildSettings().SetTimerResolutionAtStartup);
    }
}
