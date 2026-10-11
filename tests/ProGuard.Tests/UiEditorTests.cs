using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>
/// The Rules and Watchdog editors without a window or an engine: every field reaching the DTO, validation messages,
/// loading and saving without losing anything, the text on rows and section headers, the standard-user markers and
/// the CPU Set picker.
/// </summary>
public class UiEditorTests
{
    private static ShellViewModel NewShell()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var toaster = new Toaster(dispatcher);
        var hub = new EngineHub(new EngineClient("ProGuard.Tests.NoEngine." + Guid.NewGuid().ToString("N")), dispatcher, toaster);
        return new ShellViewModel(hub, toaster, new IconCache(dispatcher));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    /// <summary>A rule that sets every field the DTO has, to values that are all valid on a 16-thread machine.</summary>
    private static ProcessRuleDto FullRule() => new()
    {
        Id = 7,
        ProcessName = "game.exe",
        PriorityClass = (int)ProcessPriorityClass.AboveNormal,
        AffinityMask = 0xFF,
        CpuLimitPercent = 60,
        MaxInstances = 3,
        PowerProfileGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        ExcludeFromProBalance = true,
        DisallowExecution = false,
        CpuLimitThresholdPercent = 45.5,
        CpuLimitOverThresholdSeconds = 12,
        CpuLimitCoresToRemove = 3,
        CpuLimitDurationSeconds = 90,
        MaxInstancesAllSessions = true,
        InstanceBalancerMode = BalancerMode.FixedCoresPerInstance,
        InstanceBalancerCoresPerInstance = 2,
        InstanceBalancerAvoidSmt = true,
        InstanceBalancerReservedCores = 2,
        InstanceBalancerMinCpuPercent = 7.5,
        StartupBoostPriorityClass = (int)ProcessPriorityClass.High,
        StartupBoostSeconds = 20,
        IoPriority = 1,
        MemoryPriority = 3,
        EfficiencyMode = false,
        DisablePriorityBoost = true,
        CpuSetIds = "256,258,260",
        GpuPriority = 3,
        GpuPreference = 2,
        ReapplyIfChanged = true,
        KeepRunning = true,
    };

    // ------------------------------------------------------------------ rules: building

    [Fact]
    public void RuleEditor_BuildsEveryFieldIntoTheDto()
    {
        var e = new RuleEditor(16) { ProcessName = " game " };
        e.Priority = e.Priorities.Single(c => c.Value == ProcessPriorityClass.AboveNormal);
        e.AffinityEnabled = true;
        e.CorePicker.SetMask(0b1111_0000);
        e.ExcludeFromProBalance = true;
        e.KeepRunning = true;

        e.LimitEnabled = true;
        e.Limit = 35;
        e.LimiterThreshold = "40";
        e.LimiterSeconds = "10";
        e.LimiterCores = "2";
        e.LimiterDuration = "45";
        e.CpuSetIds = "256,257";

        e.MaxInstances = "2";
        e.MaxInstancesAllSessions = true;
        e.Balancer = e.BalancerModes.Single(m => m.Value == BalancerMode.FixedCoresPerInstance);
        e.BalancerCores = "4";
        e.BalancerAvoidSmt = true;
        e.BalancerReserved = "2";
        e.BalancerMinCpu = "5";

        e.BoostPriority = e.BoostPriorities.Single(c => c.Value == ProcessPriorityClass.High);
        e.BoostSeconds = "30";

        e.IoPriority = e.IoPriorities.Single(c => c.Value == 0);
        e.MemoryPriority = e.MemoryPriorities.Single(c => c.Value == 4);
        e.EfficiencyMode = e.EfficiencyModes.Single(c => c.Value == true);
        e.ThreadBoost = e.ThreadBoosts.Single(c => c.Value == true);
        e.GpuPriority = e.GpuPriorities.Single(c => c.Value == 1);
        e.GpuPreference = e.GpuPreferences.Single(c => c.Value == 2);
        e.ReapplyIfChanged = true;

        Assert.True(e.TryBuild(out var dto, out var error), error);

        Assert.Equal(0, dto.Id);
        Assert.Equal("game.exe", dto.ProcessName);
        Assert.Equal((int)ProcessPriorityClass.AboveNormal, dto.PriorityClass);
        Assert.Equal(0b1111_0000L, dto.AffinityMask);
        Assert.True(dto.ExcludeFromProBalance);
        Assert.False(dto.DisallowExecution);
        Assert.True(dto.KeepRunning);

        Assert.Equal(35, dto.CpuLimitPercent);
        Assert.Equal(40, dto.CpuLimitThresholdPercent);
        Assert.Equal(10, dto.CpuLimitOverThresholdSeconds);
        Assert.Equal(2, dto.CpuLimitCoresToRemove);
        Assert.Equal(45, dto.CpuLimitDurationSeconds);
        Assert.Equal("256,257", dto.CpuSetIds);

        Assert.Equal(2, dto.MaxInstances);
        Assert.True(dto.MaxInstancesAllSessions);
        Assert.Equal(BalancerMode.FixedCoresPerInstance, dto.InstanceBalancerMode);
        Assert.Equal(4, dto.InstanceBalancerCoresPerInstance);
        Assert.True(dto.InstanceBalancerAvoidSmt);
        Assert.Equal(2, dto.InstanceBalancerReservedCores);
        Assert.Equal(5, dto.InstanceBalancerMinCpuPercent);

        Assert.Equal((int)ProcessPriorityClass.High, dto.StartupBoostPriorityClass);
        Assert.Equal(30, dto.StartupBoostSeconds);

        Assert.Equal(0, dto.IoPriority);
        Assert.Equal(4, dto.MemoryPriority);
        Assert.True(dto.EfficiencyMode);
        Assert.True(dto.DisablePriorityBoost);
        Assert.Equal(1, dto.GpuPriority);
        Assert.Equal(2, dto.GpuPreference);
        Assert.True(dto.ReapplyIfChanged);
    }

    [Fact]
    public void RuleEditor_AFreshEditorSendsNoSettingsAtAll()
    {
        var e = new RuleEditor(8) { ProcessName = "x" };
        Assert.True(e.TryBuild(out var dto, out _));
        Assert.Equal("x.exe", dto.ProcessName);
        Assert.Equal(Json(new ProcessRuleDto { ProcessName = "x.exe" }), Json(dto));
    }

    [Fact]
    public void RuleEditor_EmptyBoxesMeanTheEnginesDefaults()
    {
        var e = new RuleEditor(8) { ProcessName = "x", LimiterThreshold = "30" };
        e.Balancer = e.BalancerModes.Single(m => m.Value == BalancerMode.EqualCoresPerInstance);
        e.BoostPriority = e.BoostPriorities.Single(c => c.Value == ProcessPriorityClass.High);

        Assert.True(e.TryBuild(out var dto, out _));
        Assert.Equal(30, dto.CpuLimitThresholdPercent);
        Assert.Null(dto.CpuLimitOverThresholdSeconds);
        Assert.Null(dto.CpuLimitCoresToRemove);
        Assert.Null(dto.CpuLimitDurationSeconds);
        Assert.Null(dto.InstanceBalancerCoresPerInstance);
        Assert.Null(dto.InstanceBalancerReservedCores);
        Assert.Null(dto.InstanceBalancerMinCpuPercent);
        Assert.Null(dto.StartupBoostSeconds);
    }

    [Fact]
    public void RuleEditor_ClearingTheLimiterThreshold_SwitchesTheLimiterOff()
    {
        var e = new RuleEditor(16, FullRule());
        Assert.True(e.LimiterOn);

        e.LimiterThreshold = "";
        Assert.False(e.LimiterOn);
        Assert.True(e.TryBuild(out var dto, out _));
        Assert.Null(dto.CpuLimitThresholdPercent);
        Assert.Null(dto.CpuLimitOverThresholdSeconds);
        Assert.Null(dto.CpuLimitCoresToRemove);
        Assert.Null(dto.CpuLimitDurationSeconds);
    }

    [Fact]
    public void RuleEditor_CpuSetStates_LeaveClearAndPick()
    {
        var e = new RuleEditor(8) { ProcessName = "x" };
        Assert.Null(e.ToDto().CpuSetIds);
        Assert.Equal("LEAVE ALONE", e.CpuSetsText);

        e.ClearCpuSetsCommand.Execute(null);
        Assert.Equal(string.Empty, e.ToDto().CpuSetIds);
        Assert.Contains("CLEARED", e.CpuSetsText);

        e.CpuSetIds = "1,2";
        Assert.Equal("1,2", e.ToDto().CpuSetIds);
        Assert.Contains("2 CPU SETS", e.CpuSetsText);

        e.LeaveCpuSetsCommand.Execute(null);
        Assert.Null(e.ToDto().CpuSetIds);
    }

    [Fact]
    public void RuleEditor_HiddenBalancerSettings_AreOnlyWrittenWhileTheBalancerIsOn()
    {
        var original = FullRule();
        var e = new RuleEditor(16, original);

        // Switch the balancer to EQUAL: cores-per-instance is hidden, so what was stored stays.
        e.Balancer = e.BalancerModes.Single(m => m.Value == BalancerMode.EqualCoresPerInstance);
        e.BalancerCores = "9";
        Assert.True(e.TryBuild(out var dto, out _));
        Assert.Equal(2, dto.InstanceBalancerCoresPerInstance);

        // Off: nothing about the balancer is validated or touched.
        e.Balancer = e.BalancerModes[0];
        e.BalancerReserved = "garbage";
        Assert.True(e.TryBuild(out dto, out _));
        Assert.Null(dto.InstanceBalancerMode);
        Assert.Equal(2, dto.InstanceBalancerReservedCores);
    }

    // ------------------------------------------------------------------ rules: round trip

    [Fact]
    public void RuleEditor_RoundTripsAFullRuleWithoutLoss()
    {
        var original = FullRule();
        var before = Json(original);

        var e = new RuleEditor(16, original);
        Assert.True(e.TryBuild(out var dto, out var error), error);

        Assert.Equal(before, Json(dto));
        Assert.Equal(before, Json(original)); // the editor never touches the object it was given
    }

    [Fact]
    public void RuleEditor_RoundTripsOddStoredValues()
    {
        // A wildcard and a full path stay as typed (no ".exe" is bolted on), Realtime stays Realtime, an empty CPU Set
        // list stays "clear", and affinity bits for cores this machine doesn't show are kept.
        var wildcard = new ProcessRuleDto { Id = 1, ProcessName = "chrome*", PriorityClass = (int)ProcessPriorityClass.RealTime, CpuSetIds = "", AffinityMask = 0x1_0000_0003 };
        var e = new RuleEditor(8, wildcard);
        Assert.Equal("chrome*", e.ProcessName);
        Assert.True(e.TryBuild(out var dto, out var error), error);
        Assert.Equal(Json(wildcard), Json(dto));

        var path = new ProcessRuleDto { Id = 2, ProcessName = @"C:\Games\*\game.exe" };
        Assert.Equal(@"C:\Games\*\game.exe", new RuleEditor(8, path).ToDto().ProcessName);
        Assert.Equal("obs64.exe", new RuleEditor(8, new ProcessRuleDto { ProcessName = "obs64" }).ToDto().ProcessName);
    }

    [Fact]
    public void RuleEditor_ChangingOneFieldChangesOnlyThatField()
    {
        var original = FullRule();
        var e = new RuleEditor(16, original) { KeepRunning = false };

        Assert.True(e.TryBuild(out var dto, out _));
        var expected = FullRule();
        expected.KeepRunning = false;
        Assert.Equal(Json(expected), Json(dto));
    }

    // ------------------------------------------------------------------ rules: validation

    public static IEnumerable<object[]> RuleMistakes() => new (string Label, Action<RuleEditor> Break, string Message)[]
    {
        ("no name", e => e.ProcessName = "  ", "Name the program"),
        ("affinity on, no cores", e => { e.AffinityEnabled = true; e.CorePicker.SetMask(0); }, "at least one core"),
        ("limiter threshold over 100", e => e.LimiterThreshold = "150", "CPU Limiter threshold"),
        ("limiter threshold not a number", e => e.LimiterThreshold = "lots", "CPU Limiter threshold"),
        ("limiter threshold zero", e => e.LimiterThreshold = "0", "CPU Limiter threshold"),
        ("limiter negative seconds", e => { e.LimiterThreshold = "40"; e.LimiterSeconds = "-1"; }, "\"over for\""),
        ("limiter zero cores", e => { e.LimiterThreshold = "40"; e.LimiterCores = "0"; }, "remove 1 to 7 cores"),
        ("limiter every core", e => { e.LimiterThreshold = "40"; e.LimiterCores = "8"; }, "remove 1 to 7 cores"),
        ("limiter zero duration", e => { e.LimiterThreshold = "40"; e.LimiterDuration = "0"; }, "duration"),
        ("cpu sets garbage", e => e.CpuSetIds = "1,two", "CPU Sets"),
        ("max instances zero", e => e.MaxInstances = "0", "Max instances"),
        ("max instances text", e => e.MaxInstances = "two", "Max instances"),
        ("balancer cores too many", e => { e.Balancer = e.BalancerModes[2]; e.BalancerCores = "9"; }, "Cores per instance"),
        ("balancer reserves everything", e => { e.Balancer = e.BalancerModes[1]; e.BalancerReserved = "8"; }, "Reserved cores"),
        ("balancer min cpu over 100", e => { e.Balancer = e.BalancerModes[1]; e.BalancerMinCpu = "101"; }, "minimum CPU"),
        ("boost zero seconds", e => { e.BoostPriority = e.BoostPriorities[5]; e.BoostSeconds = "0"; }, "1 to 3600"),
        ("boost too long", e => { e.BoostPriority = e.BoostPriorities[5]; e.BoostSeconds = "3601"; }, "1 to 3600"),
        ("block and keep running", e => { e.DisallowExecution = true; e.KeepRunning = true; }, "block a program and keep it running"),
    }.Select(c => new object[] { c.Label, c.Break, c.Message });

    [Theory]
    [MemberData(nameof(RuleMistakes))]
    public void RuleEditor_RejectsMistakesWithAClearMessage(string label, Action<RuleEditor> breakIt, string fragment)
    {
        var e = new RuleEditor(8) { ProcessName = "game.exe" };
        breakIt(e);

        Assert.False(e.TryBuild(out _, out var error), label);
        Assert.Contains(fragment, error);
    }

    [Fact]
    public void RuleEditor_FlagsStoredValuesOutsideTheirRange_InsteadOfSilentlyFixingThem()
    {
        var bad = new ProcessRuleDto { ProcessName = "x.exe", IoPriority = 9 };
        var e = new RuleEditor(8, bad);
        Assert.Contains("OUT OF RANGE", e.IoPriority.Label);
        Assert.False(e.TryBuild(out var dto, out var error));
        Assert.Contains("I/O priority", error);
        Assert.Equal(9, dto.IoPriority);

        Assert.Contains("Memory priority", Error(new ProcessRuleDto { ProcessName = "x.exe", MemoryPriority = 0 }));
        Assert.Contains("GPU priority", Error(new ProcessRuleDto { ProcessName = "x.exe", GpuPriority = 7 }));
        Assert.Contains("GPU preference", Error(new ProcessRuleDto { ProcessName = "x.exe", GpuPreference = 3 }));
        Assert.Contains("priority class", Error(new ProcessRuleDto { ProcessName = "x.exe", PriorityClass = 12345 }));

        static string? Error(ProcessRuleDto rule)
        {
            new RuleEditor(8, rule).TryBuild(out _, out var message);
            return message;
        }
    }

    [Fact]
    public void RuleEditor_ToDto_StillFillsInWhatItCanWhenSomethingIsWrong()
    {
        var e = new RuleEditor(8) { ProcessName = "x", LimiterThreshold = "40", LimiterCores = "99", MaxInstances = "3" };
        var dto = e.ToDto();
        Assert.Equal(40, dto.CpuLimitThresholdPercent);
        Assert.Null(dto.CpuLimitCoresToRemove);
        Assert.Equal(3, dto.MaxInstances);
    }

    // ------------------------------------------------------------------ rules: row text and section summaries

    [Fact]
    public void RuleRow_SummarisesTheNewFields()
    {
        string[] Effects(ProcessRuleDto d) => new RuleRow(d, 16).Effects.ToArray();

        Assert.Contains("LIMITER ≥40% →-2 CORES 30s", Effects(new() { CpuLimitThresholdPercent = 40, CpuLimitCoresToRemove = 2, CpuLimitDurationSeconds = 30 }));
        Assert.Contains("LIMITER ≥40% →-1 CORE 30s", Effects(new() { CpuLimitThresholdPercent = 40 })); // engine defaults
        Assert.Contains("MAX 2", Effects(new() { MaxInstances = 2 }));
        Assert.Contains("MAX 2 (ALL SESSIONS)", Effects(new() { MaxInstances = 2, MaxInstancesAllSessions = true }));
        Assert.Contains("BALANCE EQUAL", Effects(new() { InstanceBalancerMode = BalancerMode.EqualCoresPerInstance }));
        Assert.Contains("BALANCE FIXED 3", Effects(new() { InstanceBalancerMode = BalancerMode.FixedCoresPerInstance, InstanceBalancerCoresPerInstance = 3 }));
        Assert.Contains("BOOST HIGH 10s", Effects(new() { StartupBoostPriorityClass = (int)ProcessPriorityClass.High, StartupBoostSeconds = 10 }));
        Assert.Contains("BOOST HIGH 5m", Effects(new() { StartupBoostPriorityClass = (int)ProcessPriorityClass.High, StartupBoostSeconds = 300 }));
        Assert.Contains("I/O LOW", Effects(new() { IoPriority = 1 }));
        Assert.Contains("I/O VERY LOW", Effects(new() { IoPriority = 0 }));
        Assert.Contains("MEMORY MEDIUM", Effects(new() { MemoryPriority = 3 }));
        Assert.Contains("ECO ON", Effects(new() { EfficiencyMode = true }));
        Assert.Contains("ECO OFF", Effects(new() { EfficiencyMode = false }));
        Assert.Contains("THREAD BOOST OFF", Effects(new() { DisablePriorityBoost = true }));
        Assert.Contains("GPU HIGH", Effects(new() { GpuPriority = 4 }));
        Assert.Contains("GPU PREF HIGH PERF", Effects(new() { GpuPreference = 2 }));
        Assert.Contains("CPU SETS 3", Effects(new() { CpuSetIds = "1,2,3" }));
        Assert.Contains("CPU SETS CLEARED", Effects(new() { CpuSetIds = "" }));
        Assert.Contains("KEEP RUNNING", Effects(new() { KeepRunning = true }));
        Assert.Contains("BLOCKED", Effects(new() { DisallowExecution = true }));
        Assert.Contains("CAP 25%", Effects(new() { CpuLimitPercent = 25 }));
        Assert.Contains("PRIORITY LOW", Effects(new() { PriorityClass = (int)ProcessPriorityClass.BelowNormal }));
        Assert.Contains("CORES 4/16", Effects(new() { AffinityMask = 0xF }));
        Assert.Contains("PROBALANCE OFF", Effects(new() { ExcludeFromProBalance = true }));
        Assert.Equal(new[] { "NO EFFECT" }, Effects(new()));
    }

    [Fact]
    public void RuleRow_ShowsPatternsAndPathsAsStored()
    {
        Assert.Equal("CHROME.EXE", new RuleRow(new() { ProcessName = "chrome" }, 8).Name);
        Assert.Equal("CHROME*", new RuleRow(new() { ProcessName = "chrome*" }, 8).Name);
    }

    [Fact]
    public void RuleEditor_SectionSummaries_TrackTheEditor()
    {
        var e = new RuleEditor(16) { ProcessName = "x" };
        Assert.Equal("NOTHING SET", e.BasicsSummary);
        Assert.Equal("NO LIMITS", e.CpuSummary);
        Assert.Equal("NO LIMITS", e.InstancesSummary);
        Assert.Equal("OFF", e.BoostSummary);
        Assert.Equal("UNCHANGED", e.AdvancedSummary);

        e.KeepRunning = true;
        e.LimiterThreshold = "40";
        e.LimiterCores = "2";
        e.MaxInstances = "2";
        e.Balancer = e.BalancerModes[1];
        e.BoostPriority = e.BoostPriorities.Single(c => c.Value == ProcessPriorityClass.High);
        e.BoostSeconds = "10";
        e.IoPriority = e.IoPriorities.Single(c => c.Value == 1);
        e.EfficiencyMode = e.EfficiencyModes.Single(c => c.Value == true);

        Assert.Equal("KEEP RUNNING", e.BasicsSummary);
        Assert.Equal("LIMITER ≥40% →-2 CORES 30s", e.CpuSummary);
        Assert.Equal("MAX 2 · BALANCE EQUAL", e.InstancesSummary);
        Assert.Equal("BOOST HIGH 10s", e.BoostSummary);
        Assert.Equal("I/O LOW · ECO ON", e.AdvancedSummary);

        e.CorePicker.SetMask(0xF);
        e.AffinityEnabled = true;
        Assert.Equal("KEEP RUNNING · CORES 4/16", e.BasicsSummary);
    }

    // ------------------------------------------------------------------ standard users

    [Fact]
    public void Rules_StayEditableForStandardUsers_ButRealtimeIsMarkedAdminOnly()
    {
        var shell = NewShell();
        shell.Hub.Snapshot = new EngineSnapshot { LogicalProcessors = 8, CallerIsAdministrator = false };
        var vm = shell.Rules;
        Assert.True(vm.IsRestricted);

        // Everything still opens and saves.
        Assert.True(vm.NewCommand.CanExecute(null));
        vm.NewCommand.Execute(null);
        Assert.NotNull(vm.Editor);
        Assert.True(vm.Editor!.IsRestricted);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Contains("REFUSES", vm.Editor.RestrictionHint);
        Assert.Contains("REALTIME", vm.Editor.RestrictionHint);

        var realtime = vm.Editor.Priorities.Single(c => c.Value == ProcessPriorityClass.RealTime);
        Assert.True(realtime.AdminOnly);
        Assert.True(vm.Editor.BoostPriorities.Single(c => c.Value == ProcessPriorityClass.RealTime).AdminOnly);
        Assert.DoesNotContain(vm.Editor.Priorities.Where(c => c.Value != ProcessPriorityClass.RealTime), c => c.AdminOnly);

        // An administrator sees no restriction, and the editor on screen follows.
        shell.Hub.Snapshot = new EngineSnapshot { LogicalProcessors = 8, CallerIsAdministrator = true };
        Assert.False(vm.IsRestricted);
        Assert.False(vm.Editor.IsRestricted);
    }

    [Fact]
    public void Rules_AreNotRestricted_UntilTheEngineHasSaidWhoIsAsking()
    {
        var shell = NewShell();
        Assert.Null(shell.Hub.Snapshot);
        Assert.False(shell.Rules.IsRestricted);
        Assert.False(shell.Watchdog.IsRestricted);
    }

    // ------------------------------------------------------------------ CPU Set picker

    private static List<CpuSetDto> HybridSets() => Enumerable.Range(0, 8).Select(i => new CpuSetDto
    {
        Id = 256 + i,
        LogicalProcessorIndex = i,
        CoreIndex = i < 4 ? i / 2 : 2 + (i - 4),
        EfficiencyClass = i < 4 ? 1 : 0,
        IsParked = i == 6,
    }).ToList();

    [Fact]
    public void CpuSetPicker_WritesTheLitIdsInOrder()
    {
        string? result = "unset";
        var shell = NewShell();
        var vm = new CpuSetDialogViewModel(shell, "GAME.EXE", HybridSets().AsEnumerable().Reverse(), null, ids => result = ids);

        Assert.Equal(8, vm.Sets.Count);
        Assert.Equal(Enumerable.Range(256, 8), vm.Sets.Select(s => s.Id));
        Assert.Equal(string.Empty, vm.Ids);

        vm.Sets[5].IsOn = true;
        vm.Sets[1].IsOn = true;
        Assert.Equal("257,261", vm.Ids);

        vm.ApplyCommand.Execute(null);
        Assert.Equal("257,261", result);
    }

    [Fact]
    public void CpuSetPicker_AnEmptyChoiceClears_AndLeaveAloneReturnsNull()
    {
        string? result = "unset";
        var vm = new CpuSetDialogViewModel(NewShell(), "X", HybridSets(), "256", ids => result = ids);
        Assert.True(vm.CanLeaveAlone);
        Assert.Contains("256", vm.Summary);

        vm.NoneCommand.Execute(null);
        Assert.Contains("CLEARS", vm.Summary);
        vm.ApplyCommand.Execute(null);
        Assert.Equal(string.Empty, result);

        vm.LeaveAloneCommand.Execute(null);
        Assert.Null(result);

        Assert.False(new CpuSetDialogViewModel(NewShell(), "X", HybridSets(), null, _ => { }).CanLeaveAlone);
    }

    [Fact]
    public void CpuSetPicker_MarksPAndECores_WhenTheClassesDiffer_AndParkedSets()
    {
        var vm = new CpuSetDialogViewModel(NewShell(), "X", HybridSets(), null, _ => { });
        Assert.True(vm.IsHybrid);
        Assert.Equal("CORE 0 · P-CORE", vm.Sets[0].Detail);
        Assert.Equal("CORE 2 · E-CORE", vm.Sets[4].Detail);
        Assert.Equal("ID 262 · PARKED", vm.Sets[6].Footer);
        Assert.Equal("ID 256", vm.Sets[0].Footer);

        vm.FastCommand.Execute(null);
        Assert.Equal("256,257,258,259", vm.Ids);
        vm.SlowCommand.Execute(null);
        Assert.Equal("260,261,262,263", vm.Ids);
        vm.AllCommand.Execute(null);
        Assert.Equal(8, vm.Sets.Count(s => s.IsOn));
    }

    [Fact]
    public void CpuSetPicker_DoesNotMarkCoresWhenAllAreAlike()
    {
        var sets = HybridSets();
        foreach (var s in sets) s.EfficiencyClass = 0;
        var vm = new CpuSetDialogViewModel(NewShell(), "X", sets, null, _ => { });
        Assert.False(vm.IsHybrid);
        Assert.Equal("CORE 0", vm.Sets[0].Detail);
    }

    [Fact]
    public void CpuSetPicker_SavedIdsThatAreNotOnThisMachine_AreReportedAndDropped()
    {
        var vm = new CpuSetDialogViewModel(NewShell(), "X", HybridSets(), "256,999,1000", _ => { });
        Assert.Equal("256", vm.Ids);
        Assert.Contains("2 SAVED IDS NOT ON THIS MACHINE", vm.Summary);
    }

    [Theory]
    [InlineData("1,2,3", true, 3)]
    [InlineData("1; 2 3", true, 3)]
    [InlineData("5,5,5", true, 1)]
    [InlineData("", true, 0)]
    [InlineData("1,x", false, 1)]
    [InlineData("-1", false, 0)]
    public void CpuSetPicker_ParsesStoredIds(string text, bool ok, int count)
    {
        Assert.Equal(ok, CpuSetDialogViewModel.TryParseIds(text, out var ids));
        Assert.Equal(count, ids.Count);
    }

    // ------------------------------------------------------------------ shared inputs

    [Fact]
    public void CorePicker_MaskKeepsCoresItHasNoButtonFor()
    {
        var picker = new CorePicker(4, 0b0101);
        Assert.Equal(0b0101L, picker.Mask);
        Assert.Equal(0b1_0101L, picker.MaskKeeping(0b1_0011));
        Assert.Equal("2 OF 4 THREADS", picker.Summary);

        picker.EveryOtherCommand.Execute(null);
        Assert.Equal(0b0101L, picker.Mask);
        picker.SecondHalfCommand.Execute(null);
        Assert.Equal(0b1100L, picker.Mask);
        picker.SetMask(0);
        Assert.Equal("PICK AT LEAST ONE", picker.Summary);
    }

    [Fact]
    public void DurationInput_ConvertsBetweenSecondsAndMinutes_AndFollowsItsPresets()
    {
        var presets = new[] { new Choice<int>("AT ONCE", 0), new Choice<int>("30s", 30), new Choice<int>("5m", 300) };
        var input = new DurationInput(presets, 300);
        Assert.Equal("5", input.Text);
        Assert.True(input.Unit.Value); // minutes
        Assert.Same(presets[2], input.Preset);

        Assert.True(input.TryGetSeconds(out var seconds));
        Assert.Equal(300, seconds);

        input.Unit = input.Units[0];   // SEC keeps the same length of time
        Assert.Equal("300", input.Text);
        Assert.Same(presets[2], input.Preset);

        input.Text = "30";
        Assert.Same(presets[1], input.Preset);
        input.Text = "31";
        Assert.Null(input.Preset);

        input.Preset = presets[0];
        Assert.Equal("0", input.Text);

        input.Text = "-4";
        Assert.False(input.TryGetSeconds(out _));
        input.Text = "soon";
        Assert.False(input.TryGetSeconds(out _));
        input.Unit = input.Units[1];
        input.Text = "1.5";
        Assert.True(input.TryGetSeconds(out seconds));
        Assert.Equal(90, seconds);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(30, "30s")]
    [InlineData(60, "60s")]
    [InlineData(119, "119s")]
    [InlineData(120, "2m")]
    [InlineData(300, "5m")]
    [InlineData(330, "5m 30s")]
    [InlineData(3600, "1h")]
    [InlineData(7200, "2h")]
    [InlineData(5400, "90m")]
    public void Durations_AreShownTersely(int seconds, string expected) => Assert.Equal(expected, RuleText.Duration(seconds));

    [Theory]
    [InlineData(0b1111L, "0-3")]
    [InlineData(0b1010_0111L, "0-2,5,7")]
    [InlineData(0x1L, "0")]
    [InlineData(0L, "NONE")]
    public void CoreRanges_AreWrittenAsRanges(long mask, string expected) => Assert.Equal(expected, RuleText.CoreRange(mask));

    // ------------------------------------------------------------------ watchdog: building

    private static WatchdogEditor Watch(Action<WatchdogEditor>? configure = null, WatchdogRuleDto? from = null)
    {
        var e = new WatchdogEditor(from, 16);
        if (from == null) e.ProcessName = "chrome.exe";
        configure?.Invoke(e);
        return e;
    }

    private static Choice<WatchdogResponse> Act(WatchdogEditor e, WatchdogResponse r) => e.Responses.Single(c => c.Value == r);
    private static Choice<WatchdogTrigger> When(WatchdogEditor e, WatchdogTrigger t) => e.Triggers.Single(c => c.Value == t);

    [Fact]
    public void WatchdogEditor_OffersAllNineActions_AndStartsOnTheHarmlessOne()
    {
        var e = new WatchdogEditor();
        Assert.Equal(Enum.GetValues<WatchdogResponse>().OrderBy(r => r), e.Responses.Select(r => r.Value).OrderBy(r => r));
        Assert.Equal(9, e.Responses.Count);
        Assert.Equal(WatchdogResponse.LogAlert, e.Response.Value);
        Assert.Equal("JUST LOG IT", e.Response.Label);
    }

    [Fact]
    public void WatchdogEditor_BuildsEveryCondition()
    {
        var e = Watch(w => { w.Trigger = When(w, WatchdogTrigger.CpuAbove); w.Threshold = "92.5"; });
        Assert.True(e.TryBuild(out var dto, out var error), error);
        Assert.Equal(WatchdogTrigger.CpuAbove, dto.ConditionType);
        Assert.Equal(92.5, dto.ThresholdValue);

        e = Watch(w => { w.Trigger = When(w, WatchdogTrigger.RamAbove); w.Threshold = "4096"; w.Metric = w.Metrics[1]; });
        Assert.True(e.TryBuild(out dto, out error), error);
        Assert.Equal(WatchdogTrigger.RamAbove, dto.ConditionType);
        Assert.Equal(4096, dto.ThresholdValue);
        Assert.Equal(MemoryMetric.PrivateBytes, dto.MemoryMetric);

        e = Watch(w => { w.Trigger = When(w, WatchdogTrigger.InstanceCountAbove); w.Threshold = "3"; });
        Assert.True(e.TryBuild(out dto, out error), error);
        Assert.Equal(WatchdogTrigger.InstanceCountAbove, dto.ConditionType);
        Assert.Equal(3, dto.ThresholdValue);

        // Running time is typed in seconds or minutes and stored in seconds.
        e = Watch(w => { w.Trigger = When(w, WatchdogTrigger.RunningLongerThan); w.RunTime.Unit = w.RunTime.Units[1]; w.RunTime.Text = "2"; });
        Assert.True(e.TryBuild(out dto, out error), error);
        Assert.Equal(WatchdogTrigger.RunningLongerThan, dto.ConditionType);
        Assert.Equal(120, dto.ThresholdValue);

        e = Watch(w => { w.Trigger = When(w, WatchdogTrigger.RunningLongerThan); w.RunTime.Unit = w.RunTime.Units[0]; w.RunTime.Text = "45"; });
        Assert.True(e.TryBuild(out dto, out _));
        Assert.Equal(45, dto.ThresholdValue);
    }

    [Fact]
    public void WatchdogEditor_BuildsEveryAction()
    {
        WatchdogRuleDto Build(WatchdogResponse action, Action<WatchdogEditor>? more = null)
        {
            var e = Watch(w => { w.Response = Act(w, action); more?.Invoke(w); });
            Assert.True(e.TryBuild(out var dto, out var error), $"{action}: {error}");
            Assert.Equal(action, dto.ActionType);
            return dto;
        }

        Assert.Null(Build(WatchdogResponse.Terminate).ActionArgument);
        Assert.Null(Build(WatchdogResponse.Restart).ActionArgument);
        Assert.Null(Build(WatchdogResponse.LogAlert).ActionArgument);

        var priority = Build(WatchdogResponse.SetPriority, w =>
        {
            w.PriorityArg = w.PriorityChoices.Single(c => c.Value == ProcessPriorityClass.BelowNormal);
            w.Restore.Unit = w.Restore.Units[1]; w.Restore.Text = "5";
        });
        Assert.Equal("BelowNormal", priority.ActionArgument);
        Assert.Equal(300, priority.ActionDurationSeconds);

        var io = Build(WatchdogResponse.SetIoPriority, w => { w.IoArg = w.IoChoices.Single(c => c.Value == 0); w.Restore.Text = "90"; });
        Assert.Equal("0", io.ActionArgument);
        Assert.Equal(90, io.ActionDurationSeconds);

        var cores = Build(WatchdogResponse.SetAffinity, w => { w.CorePicker.SetMask(0b1111); w.Restore.Text = "30"; });
        Assert.Equal("0xF", cores.ActionArgument);
        Assert.Equal(30, cores.ActionDurationSeconds);

        Assert.Equal("continuous", Build(WatchdogResponse.TrimWorkingSet, w => w.Trim = w.TrimChoices[1]).ActionArgument);
        Assert.Null(Build(WatchdogResponse.TrimWorkingSet, w => w.Trim = w.TrimChoices[0]).ActionArgument);

        Assert.Equal("\"C:\\Tools\\app.exe\" -quiet", Build(WatchdogResponse.Launch, w => w.CommandLine = "  \"C:\\Tools\\app.exe\" -quiet ").ActionArgument);

        Assert.Equal("Chrome is hogging memory", Build(WatchdogResponse.Notify, w => w.Message = " Chrome is hogging memory ").ActionArgument);
        Assert.Null(Build(WatchdogResponse.Notify, w => w.Message = "").ActionArgument);
    }

    [Fact]
    public void WatchdogEditor_BuildsSustainRestoreCooldownAndEnabled()
    {
        var e = Watch(w =>
        {
            w.Response = Act(w, WatchdogResponse.SetPriority);
            w.Sustain.Unit = w.Sustain.Units[1]; w.Sustain.Text = "2";
            w.Restore.Text = "10";
            w.Cooldown.Text = "0";
            w.Enabled = false;
        });
        Assert.True(e.TryBuild(out var dto, out var error), error);
        Assert.Equal(120, dto.DurationSeconds);
        Assert.Equal(10, dto.ActionDurationSeconds);
        Assert.Equal(0, dto.CooldownSeconds);
        Assert.False(dto.IsEnabled);
        Assert.Equal("chrome.exe", dto.TargetProcessName);
    }

    [Fact]
    public void WatchdogEditor_ChangingTheActionDropsWhatBelongedToTheOldOne()
    {
        var original = new WatchdogRuleDto
        {
            Id = 4, TargetProcessName = "x.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 90,
            ActionType = WatchdogResponse.SetAffinity, ActionArgument = "0x3", ActionDurationSeconds = 120
        };
        var e = Watch(w => w.Response = Act(w, WatchdogResponse.Terminate), original);
        Assert.True(e.TryBuild(out var dto, out _));
        Assert.Null(dto.ActionArgument);
        Assert.Equal(0, dto.ActionDurationSeconds);
    }

    // ------------------------------------------------------------------ watchdog: validation

    public static IEnumerable<object[]> WatchMistakes() => new (string Label, Action<WatchdogEditor> Break, string Message)[]
    {
        ("no name", e => e.ProcessName = "", "Name the program"),
        ("cpu not a number", e => e.Threshold = "abc", "threshold"),
        ("cpu zero", e => e.Threshold = "0", "threshold"),
        ("cpu over 100", e => e.Threshold = "101", "100%"),
        ("memory zero", e => { e.Trigger = When(e, WatchdogTrigger.RamAbove); e.Threshold = "0"; }, "megabytes"),
        ("instances fractional", e => { e.Trigger = When(e, WatchdogTrigger.InstanceCountAbove); e.Threshold = "1.5"; }, "whole number"),
        ("instances zero", e => { e.Trigger = When(e, WatchdogTrigger.InstanceCountAbove); e.Threshold = "0"; }, "whole number"),
        ("run time zero", e => { e.Trigger = When(e, WatchdogTrigger.RunningLongerThan); e.RunTime.Text = "0"; }, "Running longer than"),
        ("run time text", e => { e.Trigger = When(e, WatchdogTrigger.RunningLongerThan); e.RunTime.Text = "ages"; }, "Running longer than"),
        ("sustain negative", e => e.Sustain.Text = "-5", "How long"),
        ("restore negative", e => { e.Response = Act(e, WatchdogResponse.SetPriority); e.Restore.Text = "-1"; }, "restore after"),
        ("cooldown text", e => e.Cooldown.Text = "soon", "Cooldown"),
        ("no priority", e => { e.Response = Act(e, WatchdogResponse.SetPriority); e.PriorityArg = null; }, "priority to set"),
        ("no i/o priority", e => { e.Response = Act(e, WatchdogResponse.SetIoPriority); e.IoArg = null; }, "I/O priority"),
        ("no cores", e => { e.Response = Act(e, WatchdogResponse.SetAffinity); e.CorePicker.SetMask(0); }, "at least one core"),
        ("empty command", e => { e.Response = Act(e, WatchdogResponse.Launch); e.CommandLine = "  "; }, "command line"),
    }.Select(c => new object[] { c.Label, c.Break, c.Message });

    [Theory]
    [MemberData(nameof(WatchMistakes))]
    public void WatchdogEditor_RejectsMistakesWithAClearMessage(string label, Action<WatchdogEditor> breakIt, string fragment)
    {
        var e = Watch(breakIt);
        Assert.False(e.TryBuild(out _, out var error), label);
        Assert.Contains(fragment, error);
    }

    // ------------------------------------------------------------------ watchdog: round trip

    public static IEnumerable<object[]> EveryWatchdogRule() => new WatchdogRuleDto[]
    {
        new() { Id = 1, TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 85.5, DurationSeconds = 45, ActionType = WatchdogResponse.SetPriority, ActionArgument = "BelowNormal", ActionDurationSeconds = 300, CooldownSeconds = 90 },
        new() { Id = 2, TargetProcessName = "b.exe", ConditionType = WatchdogTrigger.RamAbove, ThresholdValue = 2048, MemoryMetric = MemoryMetric.PrivateBytes, DurationSeconds = 30, ActionType = WatchdogResponse.SetAffinity, ActionArgument = "0xF", ActionDurationSeconds = 300, CooldownSeconds = 60 },
        new() { Id = 3, TargetProcessName = "c.exe", ConditionType = WatchdogTrigger.RunningLongerThan, ThresholdValue = 7200, DurationSeconds = 0, ActionType = WatchdogResponse.Restart, CooldownSeconds = 0, IsEnabled = false },
        new() { Id = 4, TargetProcessName = "d.exe", ConditionType = WatchdogTrigger.InstanceCountAbove, ThresholdValue = 3, DurationSeconds = 10, ActionType = WatchdogResponse.Terminate, CooldownSeconds = 600 },
        new() { Id = 5, TargetProcessName = "e.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 50, ActionType = WatchdogResponse.SetIoPriority, ActionArgument = "0", ActionDurationSeconds = 45, CooldownSeconds = 15 },
        new() { Id = 6, TargetProcessName = "f.exe", ConditionType = WatchdogTrigger.RamAbove, ThresholdValue = 512, ActionType = WatchdogResponse.TrimWorkingSet, ActionArgument = "continuous" },
        new() { Id = 7, TargetProcessName = "g.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 95, ActionType = WatchdogResponse.Launch, ActionArgument = "\"C:\\Tools\\app.exe\" -quiet" },
        new() { Id = 8, TargetProcessName = "h*", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 95, ActionType = WatchdogResponse.Notify, ActionArgument = "Too busy" },
        new() { Id = 9, TargetProcessName = "i.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 95, ActionType = WatchdogResponse.LogAlert },
        new() { Id = 10, TargetProcessName = "j.exe", ConditionType = WatchdogTrigger.RamAbove, ThresholdValue = 100, ActionType = WatchdogResponse.SetPriority, ActionArgument = "RealTime", MemoryMetric = MemoryMetric.PrivateBytes },
    }.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(EveryWatchdogRule))]
    public void WatchdogEditor_RoundTripsEveryKindOfRuleWithoutLoss(WatchdogRuleDto original)
    {
        var before = Json(original);
        var e = new WatchdogEditor(original, 16);
        Assert.True(e.TryBuild(out var dto, out var error), error);
        Assert.Equal(before, Json(dto));
        Assert.Equal(before, Json(original));
    }

    [Fact]
    public void WatchdogEditor_AsksForAPriority_WhenTheStoredOneCouldNotBeRead()
    {
        var odd = new WatchdogRuleDto { Id = 1, TargetProcessName = "a.exe", ThresholdValue = 80, ActionType = WatchdogResponse.SetPriority, ActionArgument = "Turbo" };
        var e = new WatchdogEditor(odd, 8);
        Assert.Null(e.PriorityArg);
        Assert.False(e.TryBuild(out _, out var error));
        Assert.Contains("priority", error);
    }

    // ------------------------------------------------------------------ watchdog: sentences

    [Fact]
    public void WatchdogSentence_ReadsNaturallyForTheExample()
    {
        var rule = new WatchdogRuleDto
        {
            TargetProcessName = "chrome.exe", ConditionType = WatchdogTrigger.RamAbove, ThresholdValue = 2048, MemoryMetric = MemoryMetric.PrivateBytes,
            DurationSeconds = 30, ActionType = WatchdogResponse.SetAffinity, ActionArgument = "0xF", ActionDurationSeconds = 300, CooldownSeconds = 60
        };
        Assert.Equal("WHEN chrome.exe · MEMORY (PRIVATE) ABOVE 2048 MB · FOR 30s → SET CORES 0-3 FOR 5m · COOLDOWN 60s", WatchdogText.Sentence(rule));
    }

    public static IEnumerable<object[]> Sentences() => new (WatchdogRuleDto Rule, string Sentence)[]
    {
        (new() { TargetProcessName = "a", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 80, DurationSeconds = 0, ActionType = WatchdogResponse.Terminate, CooldownSeconds = 0 },
            "WHEN a.exe · CPU ABOVE 80% · AT ONCE → END IT · COOLDOWN UNTIL CLEAR"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.RamAbove, ThresholdValue = 512, DurationSeconds = 90, ActionType = WatchdogResponse.TrimWorkingSet, ActionArgument = "continuous", CooldownSeconds = 300 },
            "WHEN a.exe · MEMORY ABOVE 512 MB · FOR 90s → TRIM MEMORY CONTINUOUSLY · COOLDOWN 5m"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.RamAbove, ThresholdValue = 512, DurationSeconds = 10, ActionType = WatchdogResponse.TrimWorkingSet, CooldownSeconds = 60 },
            "WHEN a.exe · MEMORY ABOVE 512 MB · FOR 10s → TRIM MEMORY · COOLDOWN 60s"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.RunningLongerThan, ThresholdValue = 7200, DurationSeconds = 0, ActionType = WatchdogResponse.Restart, CooldownSeconds = 3600 },
            "WHEN a.exe · RUNNING LONGER THAN 2h · AT ONCE → RESTART IT · COOLDOWN 1h"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.InstanceCountAbove, ThresholdValue = 3, DurationSeconds = 5, ActionType = WatchdogResponse.LogAlert, CooldownSeconds = 60 },
            "WHEN a.exe · INSTANCES ABOVE 3 · FOR 5s → JUST LOG IT · COOLDOWN 60s"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 95.5, DurationSeconds = 30, ActionType = WatchdogResponse.SetPriority, ActionArgument = "Idle", ActionDurationSeconds = 600, CooldownSeconds = 60 },
            "WHEN a.exe · CPU ABOVE 95.5% · FOR 30s → SET PRIORITY IDLE FOR 10m · COOLDOWN 60s"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 90, DurationSeconds = 30, ActionType = WatchdogResponse.SetPriority, CooldownSeconds = 60 },
            "WHEN a.exe · CPU ABOVE 90% · FOR 30s → SET PRIORITY IDLE · COOLDOWN 60s"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 90, DurationSeconds = 30, ActionType = WatchdogResponse.SetIoPriority, ActionArgument = "1", ActionDurationSeconds = 90, CooldownSeconds = 60 },
            "WHEN a.exe · CPU ABOVE 90% · FOR 30s → SET I/O PRIORITY LOW FOR 90s · COOLDOWN 60s"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 90, DurationSeconds = 30, ActionType = WatchdogResponse.SetAffinity, ActionArgument = "0b", CooldownSeconds = 60 },
            "WHEN a.exe · CPU ABOVE 90% · FOR 30s → SET CORES ? · COOLDOWN 60s"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 90, DurationSeconds = 30, ActionType = WatchdogResponse.SetAffinity, ActionArgument = "0x55", CooldownSeconds = 60 },
            "WHEN a.exe · CPU ABOVE 90% · FOR 30s → SET CORES 0,2,4,6 · COOLDOWN 60s"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 90, DurationSeconds = 30, ActionType = WatchdogResponse.Launch, ActionArgument = "\"C:\\x.exe\" -a", CooldownSeconds = 60 },
            "WHEN a.exe · CPU ABOVE 90% · FOR 30s → LAUNCH \"C:\\x.exe\" -a · COOLDOWN 60s"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 90, DurationSeconds = 30, ActionType = WatchdogResponse.Notify, ActionArgument = "Too busy", CooldownSeconds = 60 },
            "WHEN a.exe · CPU ABOVE 90% · FOR 30s → NOTIFY ME: Too busy · COOLDOWN 60s"),
        (new() { TargetProcessName = "a.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 90, DurationSeconds = 30, ActionType = WatchdogResponse.Notify, CooldownSeconds = 60 },
            "WHEN a.exe · CPU ABOVE 90% · FOR 30s → NOTIFY ME · COOLDOWN 60s"),
    }.Select(c => new object[] { c.Rule, c.Sentence });

    [Theory]
    [MemberData(nameof(Sentences))]
    public void WatchdogSentence_ReadsNaturally(WatchdogRuleDto rule, string expected)
    {
        Assert.Equal(expected, WatchdogText.Sentence(rule));
        Assert.Equal(expected, new WatchdogRow(rule).Sentence);
    }

    [Fact]
    public void WatchdogSentence_LongCommandsAreShortened()
    {
        var rule = new WatchdogRuleDto { TargetProcessName = "a.exe", ActionType = WatchdogResponse.Launch, ActionArgument = new string('x', 200) };
        var text = WatchdogText.Action(rule);
        Assert.True(text.Length < 60);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void WatchdogEditor_PreviewFollowsTheEditor()
    {
        var e = Watch(w =>
        {
            w.Threshold = "70";
            w.Response = Act(w, WatchdogResponse.SetAffinity);
            w.CorePicker.SetMask(0b11);
        });
        Assert.Equal("WHEN chrome.exe · CPU ABOVE 70% · FOR 30s → SET CORES 0-1 · COOLDOWN 60s", e.Preview);
        e.Sustain.Text = "0";
        Assert.StartsWith("WHEN chrome.exe · CPU ABOVE 70% · AT ONCE", e.Preview);
    }

    [Fact]
    public void WatchdogEditor_ChangingTheTrigger_OffersASensibleStart()
    {
        var e = new WatchdogEditor();
        e.Trigger = When(e, WatchdogTrigger.RamAbove);
        Assert.Equal("2048", e.Threshold);
        Assert.Equal("MB", e.Unit);
        Assert.True(e.IsMemory);

        e.Trigger = When(e, WatchdogTrigger.RunningLongerThan);
        Assert.True(e.IsRunTime);
        Assert.False(e.IsNumber);
        Assert.True(e.RunTime.TryGetSeconds(out var seconds));
        Assert.Equal(3600, seconds);
    }

    [Fact]
    public void WatchdogEditor_ShowsOnlyTheInputsTheActionNeeds()
    {
        var e = new WatchdogEditor();
        void Check(WatchdogResponse r, bool restore)
        {
            e.Response = Act(e, r);
            Assert.Equal(restore, e.TakesRestore);
            Assert.Equal(r == WatchdogResponse.SetPriority, e.IsPriorityAction);
            Assert.Equal(r == WatchdogResponse.SetIoPriority, e.IsIoAction);
            Assert.Equal(r == WatchdogResponse.SetAffinity, e.IsAffinityAction);
            Assert.Equal(r == WatchdogResponse.Launch, e.IsLaunch);
            Assert.Equal(r == WatchdogResponse.Notify, e.IsNotify);
            Assert.Equal(r == WatchdogResponse.TrimWorkingSet, e.IsTrim);
        }

        Check(WatchdogResponse.SetPriority, true);
        Check(WatchdogResponse.SetIoPriority, true);
        Check(WatchdogResponse.SetAffinity, true);
        Check(WatchdogResponse.Launch, false);
        Check(WatchdogResponse.Notify, false);
        Check(WatchdogResponse.TrimWorkingSet, false);
        Check(WatchdogResponse.Terminate, false);
    }

    // ------------------------------------------------------------------ watchdog: standard users

    [Fact]
    public void Watchdog_StaysEditableForStandardUsers_ButRealtimeAndLaunchAreMarkedAdminOnly()
    {
        var shell = NewShell();
        shell.Hub.Snapshot = new EngineSnapshot { LogicalProcessors = 8, CallerIsAdministrator = false };
        var vm = shell.Watchdog;
        Assert.True(vm.IsRestricted);
        Assert.True(vm.NewCommand.CanExecute(null));
        vm.NewCommand.Execute(null);

        var editor = vm.Editor!;
        Assert.True(editor.IsRestricted);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Contains("LAUNCH", editor.RestrictionHint);
        Assert.Contains("REALTIME", editor.RestrictionHint);

        Assert.Equal(new[] { WatchdogResponse.Launch }, editor.Responses.Where(r => r.AdminOnly).Select(r => r.Value));
        Assert.Equal(new[] { ProcessPriorityClass.RealTime }, editor.PriorityChoices.Where(c => c.AdminOnly).Select(c => c.Value!.Value));

        shell.Hub.Snapshot = new EngineSnapshot { LogicalProcessors = 8, CallerIsAdministrator = true };
        Assert.False(vm.IsRestricted);
        Assert.False(editor.IsRestricted);
    }

    // ------------------------------------------------------------------ views

    /// <summary>Loads both pages and the dialog with real XAML, so a missing resource or bad binding fails here and not on screen.</summary>
    [Fact]
    public void Views_LoadWithTheVoltTheme_AndShowAFullyFilledEditor()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = System.Windows.Application.Current ?? new System.Windows.Application();
                if (!app.Resources.Contains("VoltBrush"))
                {
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/ProGuard.UI;component/Theme/Volt.xaml") });
                    app.Resources.Add("VisibleWhen", new ProGuard.UI.Converters.VisibleWhen());
                    app.Resources.Add("EqualsParameter", new ProGuard.UI.Converters.EqualsParameter());
                }

                var shell = NewShell();
                shell.Hub.Snapshot = new EngineSnapshot { LogicalProcessors = 16, CallerIsAdministrator = false };
                shell.Rules.Editor = new RuleEditor(16, FullRule());
                shell.Watchdog.Editor = new WatchdogEditor(new WatchdogRuleDto { Id = 1, TargetProcessName = "a.exe", ActionType = WatchdogResponse.SetAffinity, ActionArgument = "0xF" }, 16);

                var rules = new ProGuard.UI.Views.RulesView { DataContext = shell.Rules };
                var watchdog = new ProGuard.UI.Views.WatchdogView { DataContext = shell.Watchdog };
                var dialog = new ProGuard.UI.Views.CpuSetDialog { DataContext = new CpuSetDialogViewModel(shell, "X", HybridSets(), "256", _ => { }) };

                foreach (var view in new System.Windows.FrameworkElement[] { rules, watchdog, dialog })
                {
                    view.Measure(new System.Windows.Size(1400, 900));
                    view.Arrange(new System.Windows.Rect(0, 0, 1400, 900));
                    view.UpdateLayout();
                    Assert.True(view.ActualWidth > 0);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(failure == null, failure?.ToString());
    }
}
