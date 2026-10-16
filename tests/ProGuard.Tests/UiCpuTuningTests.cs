using System.Windows;
using System.Windows.Threading;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>ADVANCED · CPU TUNING, driven with DTOs and snapshots; what it would send is collected instead of sent.</summary>
public class UiCpuTuningTests
{
    private static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid High = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private static ShellViewModel NewShell()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var toaster = new Toaster(dispatcher);
        var hub = new EngineHub(new EngineClient("ProGuard.Tests.NoEngine." + Guid.NewGuid().ToString("N")), dispatcher, toaster);
        return new ShellViewModel(hub, toaster, new IconCache(dispatcher));
    }

    /// <summary>A plan as the engine reports it: every catalog setting, 30 % plugged in and 20 % on battery unless a choice.</summary>
    private static CpuTuningDto Tuning(Guid? plan = null, bool hybrid = true, bool active = true, bool originals = false) => new()
    {
        PlanId = plan ?? Balanced,
        PlanName = plan == High ? "High performance" : "Balanced",
        IsActivePlan = active,
        IsHybrid = hybrid,
        HasOriginals = originals,
        OriginalsTakenUtc = originals ? new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc) : null,
        Plans = { new PowerPlanDto { Id = Balanced, Name = "Balanced" }, new PowerPlanDto { Id = High, Name = "High performance" } },
        Settings = CpuTuningCatalog.Settings.Select(s => new CpuTuningSettingDto
        {
            Key = s.Key, Id = s.Id, Group = s.Group, Name = s.Name, Description = s.Description, EfficiencyClass = s.EfficiencyClass,
            Kind = s.Kind, Min = s.Min, Max = s.Max, Unit = s.Unit, Choices = s.Choices?.ToList() ?? new(),
            Available = s.Key != "DISTRIBUTEUTIL",
            UnavailableReason = s.Key != "DISTRIBUTEUTIL" ? null : "Windows on this PC doesn't have this setting.",
            Hidden = !s.Key.StartsWith("PROCTHROTTLE", StringComparison.Ordinal),
            Ac = s.Kind == CpuTuningKind.Choice ? 1u : 30u, Dc = s.Kind == CpuTuningKind.Choice ? 0u : 20u,
            DefaultAc = s.Kind == CpuTuningKind.Choice ? 1u : 10u, DefaultDc = s.Kind == CpuTuningKind.Choice ? 1u : 5u,
        }).ToList(),
    };

    private static (CpuTuningViewModel Vm, ShellViewModel Shell, List<CpuTuningArgument> Sent) New(CpuTuningDto? tuning = null, bool admin = true)
    {
        var shell = NewShell();
        var vm = shell.Advanced.CpuTuning;
        var sent = new List<CpuTuningArgument>();
        vm.SaveDelay = TimeSpan.Zero;
        // Like the engine: applies the values and answers with the plan as it is afterwards.
        var plans = new Dictionary<Guid, CpuTuningDto>();
        CpuTuningDto Plan(Guid id) => plans.TryGetValue(id, out var p) ? p : plans[id] = Tuning(id);
        vm.Sender = argument =>
        {
            sent.Add(argument);
            var plan = Plan(argument.PlanId);
            foreach (var value in argument.Values)
            {
                var setting = plan.Settings.Single(s => s.Key == value.Key);
                setting.Ac = value.Ac ?? setting.Ac;
                setting.Dc = value.Dc ?? setting.Dc;
            }
            plan.HasOriginals = true;
            plan.OriginalsTakenUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            return Task.FromResult<CpuTuningDto?>(plan);
        };
        vm.Loader = plan => Task.FromResult<CpuTuningDto?>(Plan(plan ?? Balanced));
        vm.OnSnapshot(new EngineSnapshot { CallerIsAdministrator = admin });
        vm.Load(tuning ?? Tuning());
        return (vm, shell, sent);
    }

    private static CpuTuningRow Row(CpuTuningViewModel vm, string key) => vm.Groups.SelectMany(g => g.Rows).Single(r => r.Key == key);

    [Fact]
    public void TheAdvancedPage_HasACpuTuningSection()
    {
        var shell = NewShell();
        Assert.Contains(shell.Pages, p => p.Number == "07" && p.Title == "ADVANCED" && p.Page == shell.Advanced);
        Assert.Contains(shell.Advanced.Sections, s => s.Label == "CPU TUNING" && s.Page == shell.Advanced.CpuTuning);
    }

    [Fact]
    public void Load_ShowsEverySettingInItsGroup_WithItsExplanationAndReferences()
    {
        var (vm, _, _) = New();

        Assert.Equal(CpuTuningCatalog.Groups, vm.Groups.Select(g => g.Title));
        Assert.Equal(CpuTuningCatalog.Settings.Count, vm.Groups.Sum(g => g.Rows.Count));
        Assert.All(vm.Groups.SelectMany(g => g.Rows), r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));

        var minCores = Row(vm, "CPMINCORES");
        Assert.Equal(30, minCores.Value);
        Assert.Equal("30 %", minCores.ValueText);
        Assert.Equal("WINDOWS DEFAULT 10 %", minCores.Reference);
        Assert.Equal("E-CORES", minCores.ClassLabel);
        Assert.Equal("P-CORES", Row(vm, "CPMINCORES1").ClassLabel);
        Assert.Equal(string.Empty, Row(vm, "CPCONCURRENCY").ClassLabel); // no twin: covers every core
        Assert.True(minCores.HiddenInWindows);

        var boost = Row(vm, "PERFBOOSTMODE");
        Assert.True(boost.IsChoice);
        Assert.False(boost.ShowsValue);
        Assert.Equal(new[] { "ENABLED" }, boost.Choices.Where(c => c.IsOn).Select(c => c.Label));

        var missing = Row(vm, "DISTRIBUTEUTIL");
        Assert.False(missing.IsAvailable);
        Assert.Contains("DOESN'T HAVE", missing.UnavailableReason);

        Assert.Equal(new[] { "BALANCED", "HIGH PERFORMANCE" }, vm.Plans.Select(p => p.Label));
        Assert.True(vm.Plans.Single(p => p.Value == Balanced).IsOn);
        Assert.Equal("PROGUARD HASN'T CHANGED THIS PLAN", vm.OriginalsNote);
    }

    [Fact]
    public void OnACpuWithOneKindOfCore_TheClass1TwinsAreLeftOut_AndNothingIsTagged()
    {
        var (vm, _, _) = New(Tuning(hybrid: false));
        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Rows), r => r.Setting.EfficiencyClass == 1);
        Assert.All(vm.Groups.SelectMany(g => g.Rows), r => Assert.Equal(string.Empty, r.ClassLabel));
    }

    [Fact]
    public void ASliderChange_SendsThatSettingForThePowerSourceOnScreen()
    {
        var (vm, _, sent) = New();

        Row(vm, "CPMINCORES").Value = 64.4; // snaps to the setting's step
        Assert.Equal(64, Row(vm, "CPMINCORES").Value);

        var argument = Assert.Single(sent);
        Assert.Equal(Balanced, argument.PlanId);
        var value = Assert.Single(argument.Values);
        Assert.Equal(("CPMINCORES", (uint?)64, (uint?)null), (value.Key, value.Ac, value.Dc));

        vm.UsePowerSourceCommand.Execute("DC");
        Assert.True(vm.IsDc);
        Assert.Equal(20, Row(vm, "CPMINCORES").Value);
        Row(vm, "PERFBOOSTMODE").PickCommand.Execute(Row(vm, "PERFBOOSTMODE").Choices.First(c => c.Value == 2));

        Assert.Equal(("PERFBOOSTMODE", (uint?)null, (uint?)2), (sent[1].Values[0].Key, sent[1].Values[0].Ac, sent[1].Values[0].Dc));
    }

    [Fact]
    public async Task ChangesMadeTogether_GoOutInOneRequest()
    {
        var (vm, _, sent) = New();
        vm.SaveDelay = TimeSpan.FromMilliseconds(200);

        Row(vm, "CPMINCORES").Value = 40;
        Row(vm, "CPMINCORES").Value = 50;
        Row(vm, "CPMAXCORES").Value = 90;
        await vm.PendingSave;

        var argument = Assert.Single(sent);
        Assert.Equal(new[] { ("CPMINCORES", 50u), ("CPMAXCORES", 90u) }, argument.Values.Select(v => (v.Key, v.Ac!.Value)));
    }

    [Fact]
    public async Task APreset_AsksFirst_ThenSendsIt()
    {
        var (vm, shell, sent) = New();

        vm.ApplyPresetCommand.Execute(CpuTuningPresets.DisableParking);
        var dialog = Assert.IsType<ConfirmDialogViewModel>(shell.Dialog);
        Assert.Contains("BALANCED", dialog.Message);
        Assert.Contains("every user", dialog.Message);
        Assert.Empty(sent);

        await dialog.ConfirmCommand.ExecuteAsync(null);

        var argument = Assert.Single(sent);
        Assert.Equal(CpuTuningPresets.DisableParking, argument.Preset);
        Assert.True(vm.HasOriginals);
        Assert.StartsWith("PROGUARD KEPT THIS PLAN'S VALUES FROM", vm.OriginalsNote);
    }

    [Fact]
    public async Task Revert_AsksFirst_ThenSendsIt()
    {
        var (vm, shell, sent) = New(Tuning(originals: true));
        Assert.True(vm.HasOriginals);

        vm.RevertCommand.Execute(null);
        await Assert.IsType<ConfirmDialogViewModel>(shell.Dialog).ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(CpuTuningPresets.Revert, Assert.Single(sent).Preset);
    }

    [Fact]
    public async Task PickingAnotherPlan_ShowsItsValues()
    {
        var (vm, _, _) = New();
        await vm.PickPlanCommand.ExecuteAsync(vm.Plans.Single(p => p.Value == High));
        Assert.Equal(High, vm.PlanId);
        Assert.Equal("HIGH PERFORMANCE", vm.PlanName);
        Assert.True(vm.Plans.Single(p => p.Value == High).IsOn);
    }

    [Fact]
    public void TheWarning_NamesTheWholeMachineAndThePlan()
    {
        var (vm, _, _) = New();
        Assert.Contains("WHOLE MACHINE", vm.Warning);
        Assert.Contains("BALANCED PLAN", vm.Warning);
        Assert.Contains("AT ONCE", vm.Warning);

        vm.Load(Tuning(High, active: false));
        Assert.Contains("ISN'T THE ACTIVE PLAN", vm.Warning);

        vm.OnSnapshot(new EngineSnapshot { CallerIsAdministrator = false });
        Assert.StartsWith("READ ONLY", vm.Warning);
        Assert.False(vm.IsAdministrator);
    }

    [Fact]
    public void WhileProBalanceHoldsCoresUnparked_TheRowSaysSo()
    {
        var tuning = Tuning();
        tuning.ParkingOverrideActive = true;
        var minCores = tuning.Settings.Single(s => s.Key == "CPMINCORES");
        minCores.LiveAc = 100;
        minCores.LiveDc = 100;

        var (vm, _, _) = New(tuning);

        Assert.True(vm.ParkingOverrideActive);
        Assert.Equal(30, Row(vm, "CPMINCORES").Value); // the user's value
        Assert.Equal("PROBALANCE HOLDS 100 % WHILE IT RESTRAINS", Row(vm, "CPMINCORES").LiveNote);
        Assert.Equal(string.Empty, Row(vm, "CPMAXCORES").LiveNote);
    }

    [Fact]
    public void TheLiveBars_GroupPAndECores_AndCountTheUnparked()
    {
        var (vm, _, _) = New();
        // Two P-core threads (class 1) and two E-cores (class 0).
        vm.SetCores(new List<CpuSetDto>
        {
            new() { LogicalProcessorIndex = 0, EfficiencyClass = 1 }, new() { LogicalProcessorIndex = 1, EfficiencyClass = 1 },
            new() { LogicalProcessorIndex = 2, EfficiencyClass = 0 }, new() { LogicalProcessorIndex = 3, EfficiencyClass = 0 },
        });

        vm.OnSnapshot(new EngineSnapshot
        {
            CallerIsAdministrator = true,
            CoreCpuPercent = new List<double> { 50, 10, 0, 5 },
            CoreParked = new List<bool> { false, false, true, false },
        });

        Assert.Equal(new[] { "P-CORES", "E-CORES" }, vm.CoreGroups.Select(g => g.Label));
        Assert.Equal("2 OF 2 UNPARKED", vm.CoreGroups[0].Summary);
        Assert.Equal("1 OF 2 UNPARKED", vm.CoreGroups[1].Summary);
        Assert.Equal(new[] { true, false }, vm.CoreGroups[1].Parked);
        Assert.Equal(new[] { 0.0, 5.0 }, vm.CoreGroups[1].Loads);
    }

    [Fact]
    public void WithoutCpuSets_OrParkingState_TheBarsStillShow()
    {
        var (vm, _, _) = New();
        vm.OnSnapshot(new EngineSnapshot { CallerIsAdministrator = true, CoreCpuPercent = new List<double> { 10, 20 } });

        var group = Assert.Single(vm.CoreGroups);
        Assert.Equal("ALL CORES", group.Label);
        Assert.Equal("PARKING STATE UNAVAILABLE", group.Summary);
    }

    [Fact]
    public void TheViews_LoadAndLayOut()
    {
        Exception? failure = null;
        UiTestApp.Run(() =>
        {
            try
            {
                var (vm, shell, _) = New();
                vm.OnSnapshot(new EngineSnapshot { CallerIsAdministrator = true, CoreCpuPercent = new List<double> { 10, 90 }, CoreParked = new List<bool> { false, true } });
                foreach (var (view, context) in new (FrameworkElement, object)[]
                         {
                             (new ProGuard.UI.Views.CpuTuningView(), vm),
                             (new ProGuard.UI.Views.AdvancedView(), shell.Advanced),
                         })
                {
                    view.DataContext = context;
                    view.Measure(new Size(1100, 800));
                    view.Arrange(new Rect(0, 0, 1100, 800));
                    view.UpdateLayout();
                    Assert.True(view.ActualWidth > 0, view.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        Assert.Null(failure);
    }
}
