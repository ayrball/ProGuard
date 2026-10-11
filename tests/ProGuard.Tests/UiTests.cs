using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>
/// The new UI's own logic, without a window or an engine: the process table's ordering rules, the editors'
/// validation and the icon recolouring.
/// </summary>
public class UiTests
{
    // A shell wired to an engine that will never answer. Nothing here waits on it: snapshots are fed in directly.
    private static ShellViewModel NewShell()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var toaster = new Toaster(dispatcher);
        var hub = new EngineHub(new EngineClient("ProGuard.Tests.NoEngine." + Guid.NewGuid().ToString("N")), dispatcher, toaster);
        return new ShellViewModel(hub, toaster, new IconCache(dispatcher));
    }

    private static ProcessSnapshot P(int pid, string name, double cpu = 0, long mb = 10, bool system = false) => new()
    {
        Pid = pid, Name = name, CpuPercent = cpu, WorkingSetBytes = mb * 1024 * 1024, IsSystem = system,
        Priority = ProcessPriorityClass.Normal, AffinityMask = -1
    };

    private static EngineSnapshot Snap(params ProcessSnapshot[] processes) => new()
    {
        LogicalProcessors = 16,
        Processes = processes.ToList()
    };

    private static List<string> Names(ProcessesViewModel vm) => vm.Rows.Select(r => r.Name).ToList();

    // ------------------------------------------------------------------ process table

    [Fact]
    public void Processes_SortByCpu_BusiestFirst_ThenMemory()
    {
        var vm = NewShell().Processes;
        vm.Apply(Snap(P(1, "idle"), P(2, "busy", cpu: 9), P(3, "warm", cpu: 2), P(4, "big", mb: 900)));

        Assert.Equal(new[] { "busy.exe", "warm.exe", "big.exe", "idle.exe" }, Names(vm));
    }

    [Fact]
    public void Processes_FilterAndSearch_NarrowTheList()
    {
        var vm = NewShell().Processes;
        vm.Apply(Snap(P(10, "chrome", cpu: 1), P(20, "svchost", system: true), P(30, "code", mb: 800)));

        vm.Filter = ProcessFilter.Apps;
        Assert.Equal(new[] { "chrome.exe", "code.exe" }, Names(vm));

        vm.Filter = ProcessFilter.System;
        Assert.Equal(new[] { "svchost.exe" }, Names(vm));

        vm.Filter = ProcessFilter.Heavy;
        Assert.Equal(new[] { "code.exe" }, Names(vm));

        vm.Filter = ProcessFilter.All;
        vm.Search = "CHR";
        Assert.Equal(new[] { "chrome.exe" }, Names(vm));

        vm.Search = "2"; // PIDs match by prefix
        Assert.Equal(new[] { "svchost.exe" }, Names(vm));
    }

    [Fact]
    public void Processes_RowsArePatchedInPlace_NotRecreated()
    {
        // Selection, scroll position and the context menu all depend on a row staying the same object.
        var vm = NewShell().Processes;
        vm.Apply(Snap(P(1, "a", cpu: 1)));
        var row = vm.Rows.Single();

        vm.Apply(Snap(P(1, "a", cpu: 5, mb: 99)));

        Assert.Same(row, vm.Rows.Single());
        Assert.Equal("5.0", row.CpuText);
        Assert.Equal("99 MB", row.MemoryText);
    }

    [Fact]
    public void Processes_FrozenOrder_HoldsStill_UntilReleased()
    {
        var vm = NewShell().Processes;
        vm.Apply(Snap(P(1, "first", cpu: 9), P(2, "second", cpu: 5)));
        Assert.Equal(new[] { "first.exe", "second.exe" }, Names(vm));

        // Pointer over the table: values change, rows don't move; a newcomer waits at the bottom.
        vm.FreezeOrder = true;
        vm.Apply(Snap(P(1, "first", cpu: 1), P(2, "second", cpu: 8), P(3, "newcomer", cpu: 20)));
        Assert.Equal(new[] { "first.exe", "second.exe", "newcomer.exe" }, Names(vm));
        Assert.Equal("1.0", vm.Rows[0].CpuText);

        // Pointer leaves: the real order is restored at once.
        vm.FreezeOrder = false;
        Assert.Equal(new[] { "newcomer.exe", "second.exe", "first.exe" }, Names(vm));
    }

    [Fact]
    public void Processes_FrozenOrder_StillDropsExitedProcesses_AndUserSortStillApplies()
    {
        var vm = NewShell().Processes;
        vm.Apply(Snap(P(1, "b", cpu: 9), P(2, "a", cpu: 5), P(3, "c", cpu: 1)));
        vm.FreezeOrder = true;

        vm.Apply(Snap(P(1, "b", cpu: 9), P(3, "c", cpu: 1))); // "a" exited
        Assert.Equal(new[] { "b.exe", "c.exe" }, Names(vm));

        vm.Sort = ProcessSort.Name; // an explicit choice by the user wins over the freeze
        Assert.Equal(new[] { "b.exe", "c.exe" }, Names(vm));
        vm.Apply(Snap(P(1, "b"), P(3, "c"), P(4, "a")));
        vm.FreezeOrder = false;
        Assert.Equal(new[] { "a.exe", "b.exe", "c.exe" }, Names(vm));
    }

    [Fact]
    public void Processes_StateTagsFollowTheSnapshot()
    {
        var vm = NewShell().Processes;
        var s = P(7, "discord");
        s.CpuLimitPercent = 10;
        s.IsRestrained = true;
        s.AffinityMask = 0x0F;
        vm.Apply(Snap(s));

        var row = vm.Rows.Single();
        Assert.Equal("LIM 10%", row.LimitText);
        Assert.True(row.IsRestrained);
        Assert.Equal("4/16", row.AffinityText);

        s.CpuLimitPercent = null;
        vm.Apply(Snap(s));
        Assert.Equal(string.Empty, row.LimitText);
    }

    // ------------------------------------------------------------------ formatting

    [Theory]
    [InlineData(-1L, 16, "ALL")]
    [InlineData(0xFFFFL, 16, "ALL")]
    [InlineData(0x0FL, 16, "4/16")]
    [InlineData(0x1L, 8, "1/8")]
    public void Affinity_IsSummarisedAsAllowedOverTotal(long mask, int logical, string expected) =>
        Assert.Equal(expected, Fmt.Affinity(mask, logical));

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(10L * 1024 * 1024, "10 MB")]
    [InlineData(3L * 1024 * 1024 * 1024 / 2, "1.5 GB")]
    public void Bytes_AreShortAndReadable(long bytes, string expected) => Assert.Equal(expected, Fmt.Bytes(bytes));

    // ------------------------------------------------------------------ editors

    [Fact]
    public void RuleEditor_OnlySendsWhatIsSwitchedOn()
    {
        var editor = new RuleEditor(8) { ProcessName = " game.exe ", Priority = new Choice<ProcessPriorityClass?>("HIGH", ProcessPriorityClass.High) };
        editor.Limit = 30;
        foreach (var core in editor.Cores) core.IsOn = core.Index < 2;

        var dto = editor.ToDto();
        Assert.Equal("game.exe", dto.ProcessName);
        Assert.Equal((int)ProcessPriorityClass.High, dto.PriorityClass);
        Assert.Null(dto.CpuLimitPercent);   // cap switch is off
        Assert.Null(dto.AffinityMask);      // cores switch is off

        editor.LimitEnabled = true;
        editor.AffinityEnabled = true;
        dto = editor.ToDto();
        Assert.Equal(30, dto.CpuLimitPercent);
        Assert.Equal(0b11L, dto.AffinityMask);
    }

    [Fact]
    public void RuleEditor_RoundTripsAnExistingRule()
    {
        var original = new ProcessRuleDto { Id = 5, ProcessName = "obs64", PriorityClass = (int)ProcessPriorityClass.BelowNormal, CpuLimitPercent = 40, AffinityMask = 0b1010, ExcludeFromProBalance = true };
        var dto = new RuleEditor(4, original).ToDto();

        Assert.Equal(5, dto.Id);
        Assert.Equal("obs64.exe", dto.ProcessName);
        Assert.Equal((int)ProcessPriorityClass.BelowNormal, dto.PriorityClass);
        Assert.Equal(40, dto.CpuLimitPercent);
        Assert.Equal(0b1010L, dto.AffinityMask);
        Assert.True(dto.ExcludeFromProBalance);
    }

    [Fact]
    public void RuleEditor_SendsBackFieldsItDoesNotShow()
    {
        var original = new ProcessRuleDto
        {
            Id = 9, ProcessName = "game.exe", IoPriority = 1, MemoryPriority = 3, EfficiencyMode = false, KeepRunning = true,
            CpuLimitThresholdPercent = 40, InstanceBalancerMode = BalancerMode.FixedCoresPerInstance, StartupBoostPriorityClass = 0x80
        };
        var dto = new RuleEditor(8, original).ToDto();

        Assert.Equal(1, dto.IoPriority);
        Assert.Equal(3, dto.MemoryPriority);
        Assert.False(dto.EfficiencyMode);
        Assert.True(dto.KeepRunning);
        Assert.Equal(40, dto.CpuLimitThresholdPercent);
        Assert.Equal(BalancerMode.FixedCoresPerInstance, dto.InstanceBalancerMode);
        Assert.Equal(0x80, dto.StartupBoostPriorityClass);
    }

    [Fact]
    public void WatchdogEditor_SendsBackFieldsItDoesNotShow()
    {
        var original = new WatchdogRuleDto
        {
            Id = 3, TargetProcessName = "obs64.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 90,
            ActionType = WatchdogResponse.SetAffinity, ActionArgument = "0x3", ActionDurationSeconds = 120, CooldownSeconds = 15,
            MemoryMetric = MemoryMetric.PrivateBytes
        };

        Assert.True(new WatchdogEditor(original).TryBuild(out var dto, out _));
        Assert.Equal("0x3", dto.ActionArgument);
        Assert.Equal(120, dto.ActionDurationSeconds);
        Assert.Equal(15, dto.CooldownSeconds);
        Assert.Equal(MemoryMetric.PrivateBytes, dto.MemoryMetric);
    }

    [Theory]
    [InlineData("", "80", false)]
    [InlineData("chrome.exe", "abc", false)]
    [InlineData("chrome.exe", "0", false)]
    [InlineData("chrome.exe", "150", false)] // CPU can't exceed 100%
    [InlineData("chrome.exe", "80", true)]
    public void WatchdogEditor_ValidatesBeforeSaving(string name, string threshold, bool valid)
    {
        var editor = new WatchdogEditor { ProcessName = name };
        editor.Threshold = threshold;

        Assert.Equal(valid, editor.TryBuild(out var dto, out var error));
        if (valid) Assert.Equal(80, dto.ThresholdValue);
        else Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void WatchdogEditor_ChangingTheTrigger_OffersASensibleThreshold()
    {
        var editor = new WatchdogEditor();
        editor.Trigger = editor.Triggers.Single(t => t.Value == WatchdogTrigger.RamAbove);
        Assert.Equal("2048", editor.Threshold);
        Assert.Equal("MB", editor.Unit);
    }

    // ------------------------------------------------------------------ icons

    [Fact]
    public void Duotone_KeepsTransparency_AndUsesOnlyTheTwoTones()
    {
        // 2x1: one transparent pixel, one opaque pure-white pixel (straight alpha).
        var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 0, 255, 255, 255, 255 }, 8);
        var result = IconCache.Duotone(source);

        var pixels = new byte[8];
        result.CopyPixels(pixels, 8, 0);
        Assert.Equal(0, pixels[3]);                                     // transparent stays transparent
        Assert.Equal(new byte[] { 0x20, 0xFF, 0xDF, 0xFF }, pixels[4..]); // white maps to volt (#DFFF20 as BGRA)
        Assert.True(result.IsFrozen);                                   // safe to hand across threads
    }
}
