using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>
/// The System, Overview and Processes pages and the tray's notification logic, driven with DTOs and snapshots (and, for
/// commands, a small fake engine on a private pipe) instead of a window or the real engine or registry.
/// </summary>
public class UiSystemTests
{
    // ------------------------------------------------------------------ test doubles

    private sealed class FakeStartup : IStartupRegistration
    {
        public bool IsEnabled { get; set; }
        public int Enables { get; private set; }
        public int Disables { get; private set; }
        public Exception? Fail { get; set; }

        public void Enable()
        {
            if (Fail != null) throw Fail;
            IsEnabled = true;
            Enables++;
        }

        public void Disable()
        {
            if (Fail != null) throw Fail;
            IsEnabled = false;
            Disables++;
        }
    }

    /// <summary>Answers commands on a private pipe and records what it was sent.</summary>
    private sealed class FakeEngine : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public string PipeName { get; } = "ProGuard.Tests.FakeEngine." + Guid.NewGuid().ToString("N");
        public List<CommandMessage> Commands { get; } = new();
        public Func<CommandMessage, CommandResult> Handler { get; set; } = _ => CommandResult.Ok();

        public FakeEngine()
        {
            var first = NewServer();
            _loop = Task.Run(() => RunAsync(first));
        }

        private NamedPipeServerStream NewServer() =>
            new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        public CommandMessage[] Received()
        {
            lock (Commands) return Commands.ToArray();
        }

        private async Task RunAsync(NamedPipeServerStream server)
        {
            var utf8 = new UTF8Encoding(false);
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await server.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(server, utf8, false, 4096, leaveOpen: true);
                    using var writer = new StreamWriter(server, utf8, 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        var command = JsonSerializer.Deserialize<CommandMessage>(line, EngineClient.Json)!;
                        lock (Commands) Commands.Add(command);
                        var reply = new IpcMessage { Type = MessageType.CommandResult, Payload = JsonSerializer.Serialize(Handler(command)) };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(reply));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { /* the client went away */ }

                await server.DisposeAsync();
                server = NewServer();
            }
            await server.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }

    private static ShellViewModel NewShell(IStartupRegistration? startup = null, string? pipe = null)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var toaster = new Toaster(dispatcher);
        var hub = new EngineHub(new EngineClient(pipe ?? "ProGuard.Tests.NoEngine." + Guid.NewGuid().ToString("N")), dispatcher, toaster);
        return new ShellViewModel(hub, toaster, new IconCache(dispatcher), startup ?? new FakeStartup());
    }

    private static readonly Guid HighPerformance = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid PowerSaver = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");

    private static PowerStateDto Power(bool performance = false, bool automatic = false, string owner = "User") => new()
    {
        Plans =
        {
            new PowerPlanDto { Id = Balanced, Name = "Balanced" },
            new PowerPlanDto { Id = HighPerformance, Name = "High performance" },
            new PowerPlanDto { Id = PowerSaver, Name = "Power saver" },
        },
        ActivePlanId = Balanced, PerformanceMode = performance, PerformanceModeAutomatic = automatic, Owner = owner
    };

    private static EngineSnapshot Snap(int logical = 8, bool admin = true) => new() { LogicalProcessors = logical, CallerIsAdministrator = admin };

    /// <summary>A page loaded with 8 logical processors and fast saving, with every save collected instead of sent.</summary>
    private static (SystemViewModel Vm, List<EngineSettingsDto> Saved, FakeStartup Startup) NewSystem(EngineSettingsDto? settings = null, PowerStateDto? power = null)
    {
        var startup = new FakeStartup();
        var vm = NewShell(startup).System;
        var saved = new List<EngineSettingsDto>();
        vm.Saver = dto =>
        {
            saved.Add(dto);
            return Task.FromResult<EngineSettingsDto?>(dto);
        };
        vm.SaveDelay = TimeSpan.Zero;
        vm.OnSnapshot(Snap(8));
        vm.Load(settings ?? new EngineSettingsDto(), power ?? Power());
        return (vm, saved, startup);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>Every setting different from its default, with awkward values: odd units, mixed case GUIDs, a missing plan.</summary>
    private static EngineSettingsDto OddSettings() => new()
    {
        ProBalanceEnabled = false, ProBalanceSensitivity = 11, ProBalanceCpuThreshold = 33.3, ProBalanceRecoveryThreshold = 7.5,
        ProBalanceRestrainByAffinity = true, ProBalanceRestraintAffinity = 0x0AB0_0000_0000_00F0L,
        ProBalanceDropOneRandomCore = true, ProBalanceLowerIoPriority = true, ProBalanceUseEfficiencyMode = true,
        ProBalanceDoNotLowerPriorityClass = true, ProBalanceDisableWhenSysIdle = true, ProBalanceDisableIfSysIdleMs = 12345,
        ForegroundBoostEnabled = false,
        SmartTrimEnabled = true, SmartTrimIntervalSeconds = 95, SmartTrimMemoryThresholdMB = 197, SmartTrimMinimumRamLoadPercent = 71,
        SmartTrimExclusions = "chrome.exe, code.exe;obs64.exe", SmartTrimLogNoAction = true,
        StandbyPurgeEnabled = true, StandbyPurgeFreeRamThresholdMB = 1500, StandbyPurgeOnlyInPerformanceMode = false, FileCachePurgeEnabled = true,
        IdleSaverEnabled = true, IdleTimeoutMinutes = 7, IdlePowerPlanGuid = PowerSaver.ToString().ToUpperInvariant(),
        PerformanceModePlanGuid = "11111111-2222-3333-4444-555555555555",
        AutoPerformanceModeProcesses = "game*.exe;C:\\Games\\a, b\\x.exe", AutoPerformanceModeExclusions = "launcher.exe",
        AutoPerformanceModeDetectGames = true, PerformanceModeEngageForSteam = true,
        LoadSwitcherEnabled = true, LoadSwitcherLowThreshold = 12.5, LoadSwitcherHighThreshold = 61, LoadSwitcherAveragingWindowSamples = 9,
        LoadSwitcherMinimumTimeInPlanMs = 7777, LoadSwitcherLowPlanGuid = PowerSaver.ToString("N"), LoadSwitcherHighPlanGuid = HighPerformance.ToString("B"),
        LoadSwitcherDeferLowPlan = false,
        StartWithPowerPlanGuid = Balanced.ToString(),
        StartWithWindows = true, MinimizeToTray = false, Theme = "Dark", PollingIntervalMs = 1500
    };

    // ------------------------------------------------------------------ System page: load and save

    [Fact]
    public void System_LoadThenSave_ChangesNothing_EvenForAwkwardValues()
    {
        var stored = OddSettings();
        var (vm, _, startup) = NewSystem(stored);
        startup.IsEnabled = false; // Windows doesn't start us, though the engine's setting says it should

        // Anything the page did not touch comes back byte for byte: 95 s is not a whole number of minutes, sensitivity 11
        // is outside the 10 steps, the plan GUIDs are in other spellings, bits above the 8 cores shown are set...
        Assert.Equal(Json(stored), Json(vm.BuildSettings()));
    }

    [Fact]
    public void System_ShowsEverySettingItLoaded()
    {
        var (vm, _, startup) = NewSystem(OddSettings());

        Assert.False(vm.ProBalanceEnabled);
        Assert.Equal(10, vm.SensitivityLevel);
        Assert.Equal("33.3", vm.TriggerPercent.Text);
        Assert.Equal("7.5", vm.RecoveryPercent.Text);
        Assert.True(vm.RestrainByAffinity);
        Assert.Equal(new[] { 4, 5, 6, 7 }, vm.RestraintCores.Where(c => c.IsOn).Select(c => c.Index).ToArray());
        Assert.Equal(8, vm.RestraintCores.Count);
        Assert.True(vm.DropOneRandomCore && vm.LowerIoPriority && vm.UseEfficiencyMode && vm.DoNotLowerPriorityClass && vm.PauseWhenIdle);
        Assert.Equal("12345", vm.IdlePauseMs.Text);
        Assert.False(vm.ForegroundBoostEnabled);

        Assert.True(vm.SmartTrimEnabled);
        Assert.Equal("1.58", vm.TrimInterval.Text);          // seconds shown as minutes
        Assert.Equal("197", vm.TrimThresholdMb.Text);
        Assert.Equal("71", vm.TrimMinRamLoad.Text);
        Assert.Equal("chrome.exe" + Environment.NewLine + "code.exe" + Environment.NewLine + "obs64.exe", vm.TrimExclusions.Text);
        Assert.True(vm.TrimLogNoAction && vm.StandbyPurgeEnabled && vm.FileCachePurgeEnabled);
        Assert.False(vm.StandbyOnlyInPerformanceMode);
        Assert.Equal("1500", vm.StandbyFreeRamMb.Text);

        Assert.True(vm.IdleSaverEnabled);
        Assert.Equal("7", vm.IdleTimeout.Text);
        Assert.Equal("POWER SAVER", vm.IdlePlan.Options.Single(o => o.IsOn).Label);

        Assert.True(vm.DetectGames && vm.EngageForSteam);
        Assert.StartsWith("MISSING PLAN", vm.PerformancePlan.Options.Single(o => o.IsOn).Label); // a plan the machine no longer has stays listed
        Assert.Equal("game*.exe" + Environment.NewLine + "C:\\Games\\a, b\\x.exe", vm.AutoProcesses.Text); // a comma inside a path isn't a separator here
        Assert.Equal("launcher.exe", vm.AutoExclusions.Text);

        Assert.True(vm.LoadSwitcherEnabled);
        Assert.Equal("12.5", vm.LowLoad.Text);
        Assert.Equal("61", vm.HighLoad.Text);
        Assert.Equal("9", vm.AveragingSamples.Text);
        Assert.Equal("7777", vm.MinimumTimeInPlanMs.Text);
        Assert.Equal("POWER SAVER", vm.LowPlan.Options.Single(o => o.IsOn).Label);
        Assert.Equal("HIGH PERFORMANCE", vm.HighPlan.Options.Single(o => o.IsOn).Label);
        Assert.False(vm.DeferLowPlan);
        Assert.Equal("BALANCED", vm.StartWithPlan.Options.Single(o => o.IsOn).Label);

        Assert.False(vm.StartWithWindows); // the fake registry says no
        Assert.False(vm.MinimizeToTray);
        Assert.Equal("1500", vm.PollingInterval.Text);
        Assert.Equal(3, vm.Plans.Count);
        Assert.Equal("SET BY YOU", vm.PlanOwnerText);
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public async Task System_EditingEachSection_SavesOnlyThoseValues()
    {
        var stored = OddSettings();
        var (vm, saved, _) = NewSystem(stored);

        vm.ProBalanceEnabled = true;
        vm.SetSensitivityCommand.Execute(3);
        vm.TriggerPercent.Text = "40";
        vm.RecoveryPercent.Text = "12";
        vm.RestrainByAffinity = false;
        vm.RestraintCores[0].IsOn = true; // cores 0 and 4-7
        vm.DropOneRandomCore = false;
        vm.LowerIoPriority = false;
        vm.UseEfficiencyMode = false;
        vm.DoNotLowerPriorityClass = false;
        vm.PauseWhenIdle = false;
        vm.IdlePauseMs.Text = "60000";
        vm.ForegroundBoostEnabled = true;

        vm.SmartTrimEnabled = false;
        vm.TrimInterval.Text = "30";
        vm.TrimThresholdMb.Text = "512";
        vm.TrimMinRamLoad.Text = "80";
        vm.TrimExclusions.Text = "a.exe" + Environment.NewLine + "b.exe";
        vm.TrimLogNoAction = false;
        vm.StandbyPurgeEnabled = false;
        vm.StandbyFreeRamMb.Text = "2048";
        vm.StandbyOnlyInPerformanceMode = true;
        vm.FileCachePurgeEnabled = false;

        vm.IdleSaverEnabled = false;
        vm.IdleTimeout.Text = "20";
        vm.IdlePlan.PickCommand.Execute(vm.IdlePlan.Options.Single(o => o.Label == "BALANCED"));

        vm.PerformancePlan.PickCommand.Execute(vm.PerformancePlan.Options[0]); // back to the default (null)
        vm.AutoProcesses.Text = "x.exe" + Environment.NewLine + "y*.exe";
        vm.AutoExclusions.Text = string.Empty;
        vm.DetectGames = false;
        vm.EngageForSteam = false;

        vm.LoadSwitcherEnabled = false;
        vm.LowLoad.Text = "5";
        vm.HighLoad.Text = "80";
        vm.AveragingSamples.Text = "3";
        vm.MinimumTimeInPlanMs.Text = "1000";
        vm.LowPlan.PickCommand.Execute(vm.LowPlan.Options.Single(o => o.Label == "HIGH PERFORMANCE"));
        vm.HighPlan.PickCommand.Execute(vm.HighPlan.Options[0]);
        vm.DeferLowPlan = true;

        vm.StartWithPlan.PickCommand.Execute(vm.StartWithPlan.Options[0]);
        vm.MinimizeToTray = true;
        vm.PollingInterval.Text = "2000";
        await vm.PendingSave;

        var s = saved[^1];
        Assert.True(s.ProBalanceEnabled);
        Assert.Equal(3, s.ProBalanceSensitivity);
        Assert.Equal(40, s.ProBalanceCpuThreshold);
        Assert.Equal(12, s.ProBalanceRecoveryThreshold);
        Assert.False(s.ProBalanceRestrainByAffinity);
        Assert.Equal(0x0AB0_0000_0000_00F1L, s.ProBalanceRestraintAffinity); // bits for cores that aren't shown survive
        Assert.False(s.ProBalanceDropOneRandomCore || s.ProBalanceLowerIoPriority || s.ProBalanceUseEfficiencyMode || s.ProBalanceDoNotLowerPriorityClass || s.ProBalanceDisableWhenSysIdle);
        Assert.Equal(60000, s.ProBalanceDisableIfSysIdleMs);
        Assert.True(s.ForegroundBoostEnabled);

        Assert.False(s.SmartTrimEnabled);
        Assert.Equal(1800, s.SmartTrimIntervalSeconds);
        Assert.Equal(512, s.SmartTrimMemoryThresholdMB);
        Assert.Equal(80, s.SmartTrimMinimumRamLoadPercent);
        Assert.Equal("a.exe;b.exe", s.SmartTrimExclusions);
        Assert.False(s.SmartTrimLogNoAction);
        Assert.False(s.StandbyPurgeEnabled);
        Assert.Equal(2048, s.StandbyPurgeFreeRamThresholdMB);
        Assert.True(s.StandbyPurgeOnlyInPerformanceMode);
        Assert.False(s.FileCachePurgeEnabled);

        Assert.False(s.IdleSaverEnabled);
        Assert.Equal(20, s.IdleTimeoutMinutes);
        Assert.Equal(Balanced.ToString(), s.IdlePowerPlanGuid);

        Assert.Null(s.PerformanceModePlanGuid);
        Assert.Equal("x.exe;y*.exe", s.AutoPerformanceModeProcesses);
        Assert.Null(s.AutoPerformanceModeExclusions);
        Assert.False(s.AutoPerformanceModeDetectGames);
        Assert.False(s.PerformanceModeEngageForSteam);

        Assert.False(s.LoadSwitcherEnabled);
        Assert.Equal(5, s.LoadSwitcherLowThreshold);
        Assert.Equal(80, s.LoadSwitcherHighThreshold);
        Assert.Equal(3, s.LoadSwitcherAveragingWindowSamples);
        Assert.Equal(1000, s.LoadSwitcherMinimumTimeInPlanMs);
        Assert.Equal(HighPerformance.ToString(), s.LoadSwitcherLowPlanGuid);
        Assert.Null(s.LoadSwitcherHighPlanGuid);
        Assert.True(s.LoadSwitcherDeferLowPlan);

        Assert.Null(s.StartWithPowerPlanGuid);
        Assert.True(s.MinimizeToTray);
        Assert.Equal(2000, s.PollingIntervalMs);

        // ...and the one setting with no control is exactly as the engine sent it.
        Assert.Equal("Dark", s.Theme);
        Assert.True(s.StartWithWindows);
    }

    [Fact]
    public async Task System_ChangingOneSetting_LeavesTheOthersAsTheyWere()
    {
        var stored = OddSettings();
        var (vm, saved, _) = NewSystem(stored);

        vm.FileCachePurgeEnabled = false;
        await vm.PendingSave;

        var expected = DtoCopyOf(stored);
        expected.FileCachePurgeEnabled = false;
        Assert.Equal(Json(expected), Json(saved[^1]));
    }

    private static EngineSettingsDto DtoCopyOf(EngineSettingsDto dto) => JsonSerializer.Deserialize<EngineSettingsDto>(Json(dto))!;

    [Fact]
    public async Task System_RapidTyping_IsSavedOnce_WithTheFinalValue()
    {
        var (vm, saved, _) = NewSystem();
        vm.SaveDelay = TimeSpan.FromMilliseconds(80);

        vm.TrimThresholdMb.Text = "5";
        vm.TrimThresholdMb.Text = "51";
        vm.TrimThresholdMb.Text = "512";
        await vm.PendingSave;

        Assert.Single(saved);
        Assert.Equal(512, saved[0].SmartTrimMemoryThresholdMB);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("1.5")] // whole numbers only
    public async Task System_NumbersOutOfRange_AreFlagged_AndNeverSaved(string text)
    {
        var stored = new EngineSettingsDto { SmartTrimMemoryThresholdMB = 196 };
        var (vm, saved, _) = NewSystem(stored);

        vm.TrimThresholdMb.Text = text;
        vm.FileCachePurgeEnabled = true; // some other save happens meanwhile
        await vm.PendingSave;

        Assert.False(vm.TrimThresholdMb.IsValid);
        Assert.Equal(196, saved[^1].SmartTrimMemoryThresholdMB); // the engine's own value, not the junk
        Assert.True(saved[^1].FileCachePurgeEnabled);
    }

    // ------------------------------------------------------------------ load switcher

    [Fact]
    public async Task LoadSwitcher_HighMustBeAboveLow_UntilThenNeitherIsSaved()
    {
        var stored = new EngineSettingsDto { LoadSwitcherLowThreshold = 10, LoadSwitcherHighThreshold = 50 };
        var (vm, saved, _) = NewSystem(stored);
        Assert.Equal(string.Empty, vm.LoadSwitcherError);

        vm.HighLoad.Text = "8"; // below the low threshold
        Assert.NotEqual(string.Empty, vm.LoadSwitcherError);
        vm.LoadSwitcherEnabled = true;
        await vm.PendingSave;
        Assert.Equal(10, saved[^1].LoadSwitcherLowThreshold);
        Assert.Equal(50, saved[^1].LoadSwitcherHighThreshold);
        Assert.True(saved[^1].LoadSwitcherEnabled); // other load switcher settings still go through

        vm.HighLoad.Text = "10"; // equal is not above either
        Assert.NotEqual(string.Empty, vm.LoadSwitcherError);

        vm.LowLoad.Text = "5"; // 5 < 10: fixed from the other side
        Assert.Equal(string.Empty, vm.LoadSwitcherError);
        await vm.PendingSave;
        Assert.Equal(5, saved[^1].LoadSwitcherLowThreshold);
        Assert.Equal(10, saved[^1].LoadSwitcherHighThreshold);
    }

    [Fact]
    public async Task LoadSwitcher_AnUnreadableThreshold_CannotSlipPastTheCheck()
    {
        var stored = new EngineSettingsDto { LoadSwitcherLowThreshold = 10, LoadSwitcherHighThreshold = 50 };
        var (vm, saved, _) = NewSystem(stored);

        vm.LowLoad.Text = "abc"; // not a number: the stored 10 stays in force
        vm.HighLoad.Text = "5";  // which would be below it
        Assert.NotEqual(string.Empty, vm.LoadSwitcherError);
        vm.DeferLowPlan = false;
        await vm.PendingSave;

        Assert.Equal(10, saved[^1].LoadSwitcherLowThreshold);
        Assert.Equal(50, saved[^1].LoadSwitcherHighThreshold);
    }

    [Theory]
    [InlineData("101")]
    [InlineData("-1")]
    public void LoadSwitcher_ThresholdsAreAPercentage(string text)
    {
        var (vm, _, _) = NewSystem();
        vm.HighLoad.Text = text;
        Assert.False(vm.HighLoad.IsValid);
    }

    // ------------------------------------------------------------------ plan pickers

    [Fact]
    public void PlanPickers_NullStaysNullUntilThePlanIsPicked()
    {
        var (vm, saved, _) = NewSystem(new EngineSettingsDto()); // every plan null: the documented defaults

        Assert.Equal("DEFAULT · POWER SAVER", vm.IdlePlan.Options.Single(o => o.IsOn).Label);
        Assert.Equal("DEFAULT · HIGH PERFORMANCE", vm.PerformancePlan.Options.Single(o => o.IsOn).Label);
        Assert.Equal("DEFAULT · BALANCED", vm.LowPlan.Options.Single(o => o.IsOn).Label);
        Assert.Equal("DEFAULT · HIGH PERFORMANCE", vm.HighPlan.Options.Single(o => o.IsOn).Label);
        Assert.Equal("LEAVE IT", vm.StartWithPlan.Options.Single(o => o.IsOn).Label);
        Assert.Equal(new[] { "DEFAULT · POWER SAVER", "BALANCED", "HIGH PERFORMANCE", "POWER SAVER" }, vm.IdlePlan.Options.Select(o => o.Label).ToArray());

        vm.FileCachePurgeEnabled = true;
        var s = saved[^1];
        Assert.Null(s.IdlePowerPlanGuid);
        Assert.Null(s.PerformanceModePlanGuid);
        Assert.Null(s.LoadSwitcherLowPlanGuid);
        Assert.Null(s.LoadSwitcherHighPlanGuid);
        Assert.Null(s.StartWithPowerPlanGuid);
    }

    // ------------------------------------------------------------------ start with Windows

    [Fact]
    public async Task StartWithWindows_WritesAndRemovesTheStartupEntry_ThroughTheAbstraction()
    {
        var (vm, saved, startup) = NewSystem(new EngineSettingsDto { StartWithWindows = false });
        Assert.False(vm.StartWithWindows);

        vm.StartWithWindows = true;
        await vm.PendingSave;
        Assert.True(startup.IsEnabled);
        Assert.Equal(1, startup.Enables);
        Assert.True(saved[^1].StartWithWindows);

        vm.StartWithWindows = false;
        await vm.PendingSave;
        Assert.False(startup.IsEnabled);
        Assert.Equal(1, startup.Disables);
        Assert.False(saved[^1].StartWithWindows);
    }

    [Fact]
    public void StartWithWindows_ShowsWhatWindowsWillActuallyDo_AndLoadingNeverTouchesIt()
    {
        var startup = new FakeStartup { IsEnabled = true };
        var vm = NewShell(startup).System;
        vm.OnSnapshot(Snap());
        vm.Load(new EngineSettingsDto { StartWithWindows = false }, Power());

        Assert.True(vm.StartWithWindows);
        Assert.Equal(0, startup.Enables + startup.Disables);
    }

    [Fact]
    public async Task StartWithWindows_WhenWindowsRefuses_TheSwitchGoesBack_AndNothingIsSaved()
    {
        var (vm, saved, startup) = NewSystem(new EngineSettingsDto { StartWithWindows = false });
        startup.Fail = new UnauthorizedAccessException("blocked by policy");

        vm.StartWithWindows = true;
        await vm.PendingSave;

        Assert.False(vm.StartWithWindows);
        Assert.False(startup.IsEnabled);
        Assert.Empty(saved);
    }

    [Fact]
    public void RunKeyStartup_StartsTheExeInTheTray_WithTheSpacesQuoted()
    {
        Assert.Equal("\"C:\\Program Files\\ProGuard\\ProGuard.UI.exe\" --tray", RunKeyStartup.CommandFor("C:\\Program Files\\ProGuard\\ProGuard.UI.exe"));
    }

    // ------------------------------------------------------------------ commands through a fake engine

    [Fact]
    public async Task System_PurgeNow_SendsPurgeMemory_AndShowsTheEnginesSummary()
    {
        await using var engine = new FakeEngine();
        engine.Handler = _ => CommandResult.Ok(JsonSerializer.Serialize(new MemoryPurgeResultDto { ProcessesTrimmed = 4, Summary = "Trimmed 4 processes and freed 312 MB." }));
        var vm = NewShell(pipe: engine.PipeName).System;

        await vm.PurgeNowCommand.ExecuteAsync(null);

        Assert.Equal(CommandTypes.PurgeMemory, Assert.Single(engine.Received()).CommandType);
        Assert.Equal("Trimmed 4 processes and freed 312 MB.", vm.PurgeSummary);
    }

    [Fact]
    public async Task System_PurgeNow_ListsWhatWentWrong()
    {
        await using var engine = new FakeEngine();
        engine.Handler = _ => CommandResult.Ok(JsonSerializer.Serialize(new MemoryPurgeResultDto { Summary = "Trimmed 1 process.", Errors = { "Standby list: access denied" } }));
        var vm = NewShell(pipe: engine.PipeName).System;

        await vm.PurgeNowCommand.ExecuteAsync(null);

        Assert.Equal("Trimmed 1 process. (1 problem: Standby list: access denied)", vm.PurgeSummary);
    }

    [Fact]
    public void System_PurgeNow_IsForAdministratorsOnly()
    {
        var vm = NewShell().System;

        vm.OnSnapshot(Snap(admin: false));
        Assert.False(vm.IsAdministrator);
        Assert.False(vm.PurgeNowCommand.CanExecute(null));

        vm.OnSnapshot(Snap(admin: true));
        Assert.True(vm.PurgeNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task System_PerformanceMode_UsesTheEnableAndDisableCommands_AndShowsAutomatic()
    {
        await using var engine = new FakeEngine();
        var state = Power(performance: true, automatic: true, owner: "PerformanceMode");
        engine.Handler = c => c.CommandType == CommandTypes.GetPower ? CommandResult.Ok(JsonSerializer.Serialize(state)) : CommandResult.Ok();
        var vm = NewShell(pipe: engine.PipeName).System;
        vm.OnSnapshot(Snap());
        vm.Load(new EngineSettingsDto(), state);

        Assert.True(vm.PerformanceMode);
        Assert.True(vm.PerformanceModeAutomatic);
        Assert.Equal("SET BY PERFORMANCE MODE", vm.PlanOwnerText);

        state = Power(performance: false);
        vm.PerformanceMode = false;
        await WaitForAsync(() => engine.Received().Any(c => c.CommandType == CommandTypes.DisablePerformanceMode));
        await WaitForAsync(() => !vm.PerformanceModeAutomatic);

        state = Power(performance: true);
        vm.PerformanceMode = true;
        await WaitForAsync(() => engine.Received().Any(c => c.CommandType == CommandTypes.EnablePerformanceMode));
        await WaitForAsync(() => vm.PerformanceMode);
        Assert.False(vm.PerformanceModeAutomatic); // on, but because the user asked
    }

    [Fact]
    public async Task System_ActivatingAPlan_SendsItsGuid()
    {
        await using var engine = new FakeEngine();
        var vm = NewShell(pipe: engine.PipeName).System;
        vm.OnSnapshot(Snap());
        vm.Load(new EngineSettingsDto(), Power());

        await vm.ActivatePlanCommand.ExecuteAsync(vm.Plans.Single(p => p.Name == "POWER SAVER"));

        var command = Assert.Single(engine.Received());
        Assert.Equal(CommandTypes.SetPowerPlan, command.CommandType);
        Assert.Equal(PowerSaver.ToString(), command.Argument);
        Assert.Equal("SET BY YOU", vm.PlanOwnerText);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting");
            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------------ Overview

    [Fact]
    public void Overview_ShowsResponsiveness_PerCoreLoad_ProBalance_PerformanceMode_AndThePlan()
    {
        var overview = NewShell().Overview;
        overview.Apply(new EngineSnapshot
        {
            LogicalProcessors = 4, CpuPercent = 41.6, MemoryPercent = 63.2,
            ResponsivenessPercent = 87.4, CoreCpuPercent = new List<double> { 90, 10, 0, 100 },
            ProBalanceEnabled = true, ProBalancePaused = false, ProBalanceRestrained = 2, ProBalanceRestraintsTotal = 31,
            PerformanceModeActive = true, PowerPlanOwner = "PerformanceMode", ActivePowerPlanName = "High performance",
            CallerIsAdministrator = true
        });

        Assert.Equal(87.4, overview.Responsiveness);
        Assert.Equal("87%", overview.ResponsivenessText);
        Assert.Equal(new[] { 90.0, 10, 0, 100 }, overview.CoreLoads.ToArray());
        Assert.Equal("ARMED", overview.ProBalanceState);
        Assert.Equal(31, overview.RestraintsTotal);
        Assert.Equal("2 NOW · 31 TOTAL", overview.RestraintsText);
        Assert.True(overview.PerformanceModeActive);
        Assert.Equal("ON", overview.PerformanceModeText);
        Assert.Equal("HIGH PERFORMANCE", overview.ActivePlan);
        Assert.Equal("SET BY PERFORMANCE MODE", overview.PlanOwner);
        Assert.Equal(42, overview.Cpu);
        Assert.True(overview.IsElevated);
    }

    [Fact]
    public void Overview_ProBalanceShowsPausedAndOff_AndNoResponsivenessYetIsADash()
    {
        var overview = NewShell().Overview;

        overview.Apply(new EngineSnapshot { ProBalanceEnabled = true, ProBalancePaused = true, ResponsivenessPercent = null });
        Assert.Equal("PAUSED · IDLE", overview.ProBalanceState);
        Assert.True(overview.ProBalancePaused);
        Assert.Equal("—", overview.ResponsivenessText);
        Assert.Equal("OFF", overview.PerformanceModeText);

        overview.Apply(new EngineSnapshot { ProBalanceEnabled = false, ProBalancePaused = true });
        Assert.Equal("OFF", overview.ProBalanceState); // "paused" means nothing while it is off
        Assert.False(overview.ProBalancePaused);
    }

    [Fact]
    public void Overview_TheAdminBannerFollowsTheCaller()
    {
        var overview = NewShell().Overview;
        overview.Apply(new EngineSnapshot { CallerIsAdministrator = false });
        Assert.False(overview.IsElevated);
        overview.Apply(new EngineSnapshot { CallerIsAdministrator = true });
        Assert.True(overview.IsElevated);
    }

    // ------------------------------------------------------------------ Processes: inspector and actions

    private static ProcessSnapshot Detailed(int pid = 42) => new()
    {
        Pid = pid, Name = "game", UserName = "PC\\tee", SessionId = 2, PrivateBytes = 750L * 1024 * 1024, ThreadCount = 61,
        Priority = ProcessPriorityClass.Normal, IoPriority = 1, MemoryPriority = 4, AffinityMask = 0x0F, IsAffinityLimited = true,
        WorkingSetBytes = 900L * 1024 * 1024
    };

    [Fact]
    public void Processes_InspectorShowsIoMemoryPriority_UserSessionPrivateBytesAndThreads()
    {
        var vm = NewShell().Processes;
        var s = Detailed();
        vm.Apply(new EngineSnapshot { LogicalProcessors = 16, Processes = { s } });

        var row = vm.Rows.Single();
        Assert.Equal("LOW", row.IoPriorityText);
        Assert.Equal("BELOW NORMAL", row.MemoryPriorityText);
        Assert.Equal("PC\\tee", row.UserText);
        Assert.Equal(2, row.SessionId);
        Assert.Equal("750 MB", row.PrivateText);
        Assert.Equal(61, row.ThreadCount);

        // Unknown values read as a dash rather than a wrong number.
        s.IoPriority = null;
        s.MemoryPriority = null;
        s.UserName = null;
        vm.Apply(new EngineSnapshot { LogicalProcessors = 16, Processes = { s } });
        Assert.Same(row, vm.Rows.Single());
        Assert.Equal("—", row.IoPriorityText);
        Assert.Equal("—", row.MemoryPriorityText);
        Assert.Equal("—", row.UserText);
    }

    [Fact]
    public void Processes_AffinityLimitedTagFollowsTheSnapshot_AlongsideTheOtherTags()
    {
        var vm = NewShell().Processes;
        var s = Detailed();
        s.IsRestrained = true;
        s.CpuLimitPercent = 20;
        vm.Apply(new EngineSnapshot { LogicalProcessors = 16, Processes = { s } });

        var row = vm.Rows.Single();
        Assert.True(row.IsAffinityLimited);
        Assert.True(row.IsRestrained);
        Assert.Equal("LIM 20%", row.LimitText);

        s.IsAffinityLimited = false;
        vm.Apply(new EngineSnapshot { LogicalProcessors = 16, Processes = { s } });
        Assert.False(row.IsAffinityLimited);
        Assert.True(row.IsRestrained); // independent of the others
    }

    [Fact]
    public async Task Processes_IoAndMemoryPriorityActions_SendTheirLevels()
    {
        await using var engine = new FakeEngine();
        var vm = NewShell(pipe: engine.PipeName).Processes;
        vm.Apply(new EngineSnapshot { LogicalProcessors = 8, CallerIsAdministrator = true, Processes = { Detailed(42) } });
        vm.Selected = vm.Rows.Single();

        await vm.SetIoPriorityCommand.ExecuteAsync("3");
        await vm.SetIoPriorityCommand.ExecuteAsync("0");
        await vm.SetMemoryPriorityCommand.ExecuteAsync("1");
        await vm.SetMemoryPriorityCommand.ExecuteAsync("5");
        await vm.SetIoPriorityCommand.ExecuteAsync("4");     // out of range: ignored
        await vm.SetMemoryPriorityCommand.ExecuteAsync("0"); // out of range: ignored
        await vm.SetMemoryPriorityCommand.ExecuteAsync("x"); // not a number: ignored

        Assert.Equal(
            new[] { (CommandTypes.SetIoPriority, 42, "3"), (CommandTypes.SetIoPriority, 42, "0"), (CommandTypes.SetMemoryPriority, 42, "1"), (CommandTypes.SetMemoryPriority, 42, "5") },
            engine.Received().Select(c => (c.CommandType, c.TargetPid ?? -1, c.Argument ?? "")).ToArray());
    }

    [Fact]
    public async Task Processes_HighIoPriority_IsNotSentWithoutAdministratorRights()
    {
        await using var engine = new FakeEngine();
        var vm = NewShell(pipe: engine.PipeName).Processes;
        vm.Apply(new EngineSnapshot { LogicalProcessors = 8, CallerIsAdministrator = false, Processes = { Detailed(7) } });
        vm.Selected = vm.Rows.Single();

        await vm.SetIoPriorityCommand.ExecuteAsync("3");
        Assert.Empty(engine.Received());
        Assert.False(vm.IsAdministrator);

        await vm.SetIoPriorityCommand.ExecuteAsync("1"); // lower levels are fine for anyone
        Assert.Equal("1", Assert.Single(engine.Received()).Argument);
    }

    // ------------------------------------------------------------------ watchdog notifications

    private sealed class FakeFeed
    {
        public List<long?> Asked { get; } = new();
        public Queue<WatchdogEventList?> Replies { get; } = new();
        public List<WatchdogEventDto> Notified { get; } = new();

        public WatchdogNotificationFeed Create() => new(
            (query, _) =>
            {
                Asked.Add(query.After);
                return Task.FromResult(Replies.Count > 0 ? Replies.Dequeue() : null);
            },
            Notified.Add);
    }

    private static WatchdogEventDto Event(long sequence, bool notify, string title = "t") =>
        new() { Sequence = sequence, Notify = notify, Title = title, Message = "m" + sequence };

    [Fact]
    public async Task Feed_TheFirstPoll_AsksWithNoCursor_AndNeverReplaysTheBacklog()
    {
        var f = new FakeFeed();
        var feed = f.Create();
        // Even if an engine were to send events for a null cursor, the first poll only sets the baseline.
        f.Replies.Enqueue(new WatchdogEventList { Latest = 12, Events = { Event(11, true), Event(12, true) } });

        await feed.PollOnceAsync();

        Assert.Equal(new long?[] { null }, f.Asked);
        Assert.Equal(12, feed.Cursor);
        Assert.Empty(f.Notified);
    }

    [Fact]
    public async Task Feed_LaterPolls_AskAfterTheCursor_AndNotifyOnlyForEventsThatAskedForIt_OldestFirst()
    {
        var f = new FakeFeed();
        var feed = f.Create();
        f.Replies.Enqueue(new WatchdogEventList { Latest = 5 });
        f.Replies.Enqueue(new WatchdogEventList { Latest = 9, Events = { Event(9, true, "nine"), Event(7, false), Event(6, true, "six"), Event(8, false) } });
        f.Replies.Enqueue(new WatchdogEventList { Latest = 9 });

        await feed.PollOnceAsync();
        await feed.PollOnceAsync();
        await feed.PollOnceAsync();

        Assert.Equal(new long?[] { null, 5, 9 }, f.Asked);
        Assert.Equal(new[] { "six", "nine" }, f.Notified.Select(e => e.Title).ToArray());
        Assert.Equal(9, feed.Cursor);
    }

    [Fact]
    public async Task Feed_AnEventIsNotifiedOnce_EvenIfTheEngineSendsItAgain()
    {
        var f = new FakeFeed();
        var feed = f.Create();
        f.Replies.Enqueue(new WatchdogEventList { Latest = 1 });
        f.Replies.Enqueue(new WatchdogEventList { Latest = 2, Events = { Event(2, true) } });
        f.Replies.Enqueue(new WatchdogEventList { Latest = 2, Events = { Event(2, true) } });

        await feed.PollOnceAsync();
        await feed.PollOnceAsync();
        await feed.PollOnceAsync();

        Assert.Single(f.Notified);
    }

    [Fact]
    public async Task Feed_WhenTheEngineIsUnreachable_KeepsItsPlace()
    {
        var f = new FakeFeed();
        var feed = f.Create();

        f.Replies.Enqueue(null);                                      // engine not up yet: still no baseline
        await feed.PollOnceAsync();
        Assert.Null(feed.Cursor);

        f.Replies.Enqueue(new WatchdogEventList { Latest = 3 });
        await feed.PollOnceAsync();
        Assert.Equal(3, feed.Cursor);

        f.Replies.Enqueue(null);                                      // engine away
        await feed.PollOnceAsync();
        f.Replies.Enqueue(new WatchdogEventList { Latest = 4, Events = { Event(4, true) } });
        await feed.PollOnceAsync();

        Assert.Equal(new long?[] { null, null, 3, 3 }, f.Asked);
        Assert.Single(f.Notified);
    }

    [Fact]
    public async Task Feed_AnEngineRestart_ResetsTheCursor_SoNewEventsAreNotMissed()
    {
        var f = new FakeFeed();
        var feed = f.Create();
        f.Replies.Enqueue(new WatchdogEventList { Latest = 50 });
        // The restarted engine counts from 1 again: its Latest (2) is below our cursor (50).
        f.Replies.Enqueue(new WatchdogEventList { Latest = 2 });
        f.Replies.Enqueue(new WatchdogEventList { Latest = 2, Events = { Event(1, false), Event(2, true, "after restart") } });

        await feed.PollOnceAsync();
        await feed.PollOnceAsync();

        Assert.Equal(new long?[] { null, 50, 0 }, f.Asked);
        Assert.Equal(new[] { "after restart" }, f.Notified.Select(e => e.Title).ToArray());
        Assert.Equal(2, feed.Cursor);
    }

    [Fact]
    public async Task Feed_ABrokenNotificationDoesNotStopTheRest()
    {
        var shown = new List<long>();
        var replies = new Queue<WatchdogEventList?>();
        replies.Enqueue(new WatchdogEventList { Latest = 0 });
        replies.Enqueue(new WatchdogEventList { Latest = 2, Events = { Event(1, true), Event(2, true) } });
        var feed = new WatchdogNotificationFeed(
            (_, _) => Task.FromResult(replies.Dequeue()),
            e => { if (e.Sequence == 1) throw new InvalidOperationException("balloon failed"); shown.Add(e.Sequence); });

        await feed.PollOnceAsync();
        await feed.PollOnceAsync();

        Assert.Equal(new long[] { 2 }, shown);
        Assert.Equal(2, feed.Cursor);
    }

    // ------------------------------------------------------------------ the views themselves

    [Fact]
    public void Views_AllLoadAndLayOut()
    {
        // Resource and binding-syntax mistakes in XAML only show when a view is created, so create every one.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null) new ProGuard.UI.App().InitializeComponent();

                var shell = NewShell();
                shell.System.OnSnapshot(Snap());
                shell.System.Load(OddSettings(), Power());
                shell.Overview.Apply(new EngineSnapshot { CoreCpuPercent = new List<double> { 10, 90 }, ResponsivenessPercent = 50 });
                shell.Processes.Apply(new EngineSnapshot { LogicalProcessors = 8, Processes = { Detailed() } });
                shell.Processes.Selected = shell.Processes.Rows.Single();

                var views = new (FrameworkElement View, object Context)[]
                {
                    (new ProGuard.UI.Views.SystemView(), shell.System),
                    (new ProGuard.UI.Views.OverviewView(), shell.Overview),
                    (new ProGuard.UI.Views.ProcessesView(), shell.Processes),
                    (new ProGuard.UI.Views.ActivityView(), shell.Activity),
                };
                foreach (var (view, context) in views)
                {
                    view.DataContext = context;
                    view.Measure(new Size(1000, 700));
                    view.Arrange(new Rect(0, 0, 1000, 700));
                    view.UpdateLayout();
                    Assert.True(view.ActualWidth > 0, view.GetType().Name);
                }

                var window = new ProGuard.UI.MainWindow { DataContext = shell };
                Assert.NotNull(window);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
