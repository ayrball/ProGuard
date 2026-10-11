using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Moq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ActivityKind = ProGuard.Shared.Contracts.ActivityKind; // System.Diagnostics has one too

namespace ProGuard.Tests;

/// <summary>Each command the thin client added: what it does, what it refuses, and what it hands back.</summary>
public class ThinClientCommandTests
{
    private const int Pid = 4321;

    // ---------- Process actions ----------

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void SetEfficiencyMode_WritesEcoQoSThroughTheNativeWrapper(string argument, bool enabled)
    {
        var h = new ThinHarness();
        h.Process(Pid, "game");

        Assert.True(h.SendAsAdmin(CommandTypes.SetEfficiencyMode, Pid, argument).Success);

        Assert.Equal((true, enabled), h.Tuning.Eco[Pid]);
        Assert.Contains(h.Manual.GetRecent(5), e => e.Kind == ActivityKind.Manual && e.Pid == Pid && e.ProcessName == "game");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("maybe")]
    public void SetEfficiencyMode_RejectsAMissingOrBadArgument(string? argument)
    {
        var h = new ThinHarness();
        var result = h.SendAsAdmin(CommandTypes.SetEfficiencyMode, Pid, argument);

        Assert.False(result.Success);
        Assert.Empty(h.Tuning.Writes);
    }

    [Fact]
    public void ProcessActions_NeedATargetPid()
    {
        var h = new ThinHarness();
        foreach (var command in new[] { CommandTypes.SetEfficiencyMode, CommandTypes.SetIoPriority, CommandTypes.SetMemoryPriority, CommandTypes.TerminateProcessTree })
            Assert.Contains("needs a TargetPid", h.SendAsAdmin(command, null, "1").Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SetIoPriority_AcceptsZeroToThree(int priority)
    {
        var h = new ThinHarness();
        Assert.True(h.SendAsAdmin(CommandTypes.SetIoPriority, Pid, priority).Success);
        Assert.Equal(priority, h.Tuning.Io[Pid]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4")]
    [InlineData("high")]
    public void SetIoPriority_RejectsOutOfRangeValues(string argument)
    {
        var h = new ThinHarness();
        Assert.False(h.SendAsAdmin(CommandTypes.SetIoPriority, Pid, argument).Success);
        Assert.Empty(h.Tuning.Writes);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(6, false)]
    public void SetMemoryPriority_AcceptsOneToFive(int priority, bool valid)
    {
        var h = new ThinHarness();
        var result = h.SendAsAdmin(CommandTypes.SetMemoryPriority, Pid, priority);

        Assert.Equal(valid, result.Success);
        if (valid) Assert.Equal(priority, h.Tuning.Memory[Pid]);
        else Assert.Empty(h.Tuning.Writes);
    }

    [Fact]
    public void ANativeFailure_IsReportedNotThrown()
    {
        var h = new ThinHarness();
        h.Tuning.Failures["IoPriority"] = new System.ComponentModel.Win32Exception("Access is denied");

        var result = h.SendAsAdmin(CommandTypes.SetIoPriority, Pid, 1);

        Assert.False(result.Success);
        Assert.Contains("Access is denied", result.Error);
    }

    // ---------- Terminate tree ----------

    private static ThinHarness TreeHarness(out ProcessInfo root)
    {
        var h = new ThinHarness();
        var start = DateTime.UtcNow.AddMinutes(-10);
        root = h.Process(100, "launcher", parent: 1, start: start);
        h.Process(101, "child", parent: 100, start: start.AddMinutes(1));
        h.Process(102, "grandchild", parent: 101, start: start.AddMinutes(2));
        h.Process(103, "sibling", parent: 100, start: start.AddMinutes(3));
        h.Process(200, "unrelated", parent: 1, start: start);
        return h;
    }

    [Fact]
    public void TerminateProcessTree_EndsDescendantsBeforeTheirParents_AndReportsTheCount()
    {
        var h = TreeHarness(out _);
        var order = new List<int>();
        h.Controller.Setup(c => c.Terminate(It.IsAny<int>())).Callback<int>(order.Add);

        var result = h.SendAsAdmin(CommandTypes.TerminateProcessTree, 100);

        Assert.True(result.Success, result.Error);
        Assert.Equal("4", result.Data);
        Assert.Equal(100, order[^1]);
        Assert.True(order.IndexOf(102) < order.IndexOf(101), "a grandchild must go before its parent");
        Assert.Equal(new[] { 100, 101, 102, 103 }, order.OrderBy(p => p));
        h.Controller.Verify(c => c.Terminate(200), Times.Never);
    }

    [Fact]
    public void TerminateProcessTree_IgnoresAChildThatStartedBeforeItsSupposedParent()
    {
        // Parent ids outlive parents and Windows reuses them: a process older than its "parent" is not its child.
        var h = TreeHarness(out var root);
        h.Monitor.GetProcess(103)!.StartTime = root.StartTime.AddMinutes(-5);

        Assert.Equal("3", h.SendAsAdmin(CommandTypes.TerminateProcessTree, 100).Data);
        h.Controller.Verify(c => c.Terminate(103), Times.Never);
    }

    [Fact]
    public void TerminateProcessTree_ForAStandardUser_EndsOnlyWhatTheyCouldEndThemselves()
    {
        var h = TreeHarness(out _);
        var caller = new ThinCaller();
        caller.OpenablePids.UnionWith(new[] { 100, 101 }); // not 102, not 103

        var result = h.Send(caller, CommandTypes.TerminateProcessTree, 100);

        Assert.True(result.Success, result.Error);
        Assert.Equal("2", result.Data);
        h.Controller.Verify(c => c.Terminate(100), Times.Once);
        h.Controller.Verify(c => c.Terminate(101), Times.Once);
        h.Controller.Verify(c => c.Terminate(102), Times.Never);
        h.Controller.Verify(c => c.Terminate(103), Times.Never);
    }

    [Fact]
    public void TerminateProcessTree_ChecksEveryDescendantForTheRightToTerminateIt()
    {
        var h = TreeHarness(out _);
        var seen = new List<(int, ProcessAccessFlags)>();
        var caller = new Mock<IIpcCaller>();
        caller.SetupGet(c => c.Name).Returns(@"TEST\standard");
        caller.Setup(c => c.CanOpenProcess(It.IsAny<int>(), It.IsAny<ProcessAccessFlags>())).Returns(true);
        caller.Setup(c => c.FilterOpenable(It.IsAny<IReadOnlyList<(int Pid, DateTime StartTimeUtc)>>(), It.IsAny<ProcessAccessFlags>()))
            .Returns((IReadOnlyList<(int Pid, DateTime StartTimeUtc)> p, ProcessAccessFlags a) => { seen.AddRange(p.Select(x => (x.Pid, a))); return p.Select(x => x.Pid).ToHashSet(); });

        Assert.True(h.Send(caller.Object, CommandTypes.TerminateProcessTree, 100).Success);

        Assert.Equal(new[] { 101, 102, 103 }, seen.Select(s => s.Item1).OrderBy(p => p));
        Assert.All(seen, s => Assert.Equal(ProcessAccessFlags.Terminate, s.Item2));
    }

    [Fact]
    public void TerminateProcessTree_NeverEndsTheSystemProcessOrTheEngine()
    {
        var h = TreeHarness(out _);
        Assert.False(h.SendAsAdmin(CommandTypes.TerminateProcessTree, 4).Success);
        Assert.False(h.SendAsAdmin(CommandTypes.TerminateProcessTree, Environment.ProcessId).Success);

        // ...nor a protected process or the engine inside someone's tree.
        h.Process(110, "csrss", parent: 100, start: DateTime.UtcNow);
        h.Process(111, "innocent", parent: 100, start: DateTime.UtcNow);
        h.Process(Environment.ProcessId, "ProGuard.Engine", parent: 100, start: DateTime.UtcNow);
        h.Process(4, "System", parent: 100, start: DateTime.UtcNow);
        Assert.True(h.SendAsAdmin(CommandTypes.TerminateProcessTree, 100).Success);

        h.Controller.Verify(c => c.Terminate(110), Times.Never);
        h.Controller.Verify(c => c.Terminate(4), Times.Never);
        h.Controller.Verify(c => c.Terminate(Environment.ProcessId), Times.Never);
        h.Controller.Verify(c => c.Terminate(111), Times.Once);
    }

    [Fact]
    public void TerminateProcessTree_FailingToEndTheRoot_IsAnError_ButAGoneDescendantIsNot()
    {
        var h = TreeHarness(out _);
        h.Controller.Setup(c => c.Terminate(102)).Throws(new ArgumentException("gone"));
        Assert.Equal("3", h.SendAsAdmin(CommandTypes.TerminateProcessTree, 100).Data);

        h.Controller.Setup(c => c.Terminate(100)).Throws(new InvalidOperationException("Access is denied"));
        var result = h.SendAsAdmin(CommandTypes.TerminateProcessTree, 100);
        Assert.False(result.Success);
        Assert.Contains("Access is denied", result.Error);
    }

    // ---------- Memory ----------

    [Fact]
    public void TrimWorkingSet_ReturnsTheBytesReclaimed()
    {
        var h = new ThinHarness();
        h.SmartTrim.Setup(t => t.TrimProcess(Pid)).Returns(new MemoryPurgeReport { ProcessesTrimmed = 1, WorkingSetBytesReclaimed = 123_456 });
        h.SmartTrim.Setup(t => t.TrimMemory()).Returns(new MemoryPurgeReport { ProcessesTrimmed = 3, WorkingSetBytesReclaimed = 9_000_000 });

        Assert.Equal("123456", h.SendAsAdmin(CommandTypes.TrimWorkingSet, Pid).Data);
        Assert.Equal("9000000", h.SendAsAdmin(CommandTypes.TrimWorkingSet).Data);
        Assert.Equal(2, h.Manual.GetRecent(10).Count(e => e.Kind == ActivityKind.Memory));
    }

    [Fact]
    public void TrimWorkingSet_OfAProcessWindowsWontTrim_FailsWithTheReason()
    {
        var h = new ThinHarness();
        var report = new MemoryPurgeReport();
        report.Errors.Add("OpenProcess failed (Access is denied)");
        h.SmartTrim.Setup(t => t.TrimProcess(Pid)).Returns(report);

        var result = h.SendAsAdmin(CommandTypes.TrimWorkingSet, Pid);

        Assert.False(result.Success);
        Assert.Contains("Access is denied", result.Error);
    }

    [Fact]
    public void PurgeMemory_ReturnsTheReportAsAMemoryPurgeResultDtoWithItsSummary()
    {
        var h = new ThinHarness();
        var report = new MemoryPurgeReport
        {
            ProcessesTrimmed = 2, WorkingSetBytesReclaimed = 200L << 20,
            StandbyPurged = true, StandbyBytesReclaimed = 1000L << 20,
        };
        report.Errors.Add("file cache: privilege not held");
        h.SmartTrim.Setup(t => t.PurgeNow()).Returns(report);

        var dto = h.Data<MemoryPurgeResultDto>(h.SendAsAdmin(CommandTypes.PurgeMemory));

        Assert.Equal(2, dto.ProcessesTrimmed);
        Assert.Equal(200L << 20, dto.WorkingSetBytesReclaimed);
        Assert.True(dto.StandbyPurged);
        Assert.Equal(1000L << 20, dto.StandbyBytesReclaimed);
        Assert.Equal(new[] { "file cache: privilege not held" }, dto.Errors);
        Assert.Equal(report.Summary(), dto.Summary);
    }

    [Fact]
    public void PurgeMemory_ThatDidNothingButFail_IsAFailureCarryingTheReport()
    {
        var h = new ThinHarness();
        var report = new MemoryPurgeReport();
        report.Errors.Add("standby: privilege not held");
        h.SmartTrim.Setup(t => t.PurgeNow()).Returns(report);

        var result = h.SendAsAdmin(CommandTypes.PurgeMemory);

        Assert.False(result.Success);
        Assert.Contains("privilege not held", result.Error);
        Assert.NotNull(result.Data);
    }

    // ---------- Process rules ----------

    private static ProcessRuleDto Rule(string name = "game.exe") => new() { ProcessName = name, PriorityClass = (int)ProcessPriorityClass.High };

    [Fact]
    public void GetProcessRules_ListsTheStoredRulesByName()
    {
        var h = new ThinHarness();
        h.Rules.Add(new ProcessRule { Id = 2, ProcessName = "zeta.exe", IoPriority = 1 });
        h.Rules.Add(new ProcessRule { Id = 1, ProcessName = "alpha.exe", CpuLimitPercent = 30 });

        var rules = h.Data<List<ProcessRuleDto>>(h.SendAsAdmin(CommandTypes.GetProcessRules));

        Assert.Equal(new[] { "alpha.exe", "zeta.exe" }, rules.Select(r => r.ProcessName));
        Assert.Equal(30, rules[0].CpuLimitPercent);
        Assert.Equal(1, rules[1].IoPriority);
    }

    [Fact]
    public void SaveProcessRule_StoresANewRuleAndReturnsItWithItsId()
    {
        var h = new ThinHarness();
        var dto = Rule();
        dto.AffinityMask = 0xF;
        dto.InstanceBalancerMode = BalancerMode.FixedCoresPerInstance;
        dto.StartupBoostPriorityClass = (int)ProcessPriorityClass.High;
        dto.StartupBoostSeconds = 30;

        var saved = h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, dto));

        Assert.NotEqual(0, saved.Id);
        var stored = Assert.Single(h.Rules);
        Assert.Equal(saved.Id, stored.Id);
        Assert.Equal(0xF, stored.AffinityMask);
        Assert.Equal(InstanceBalancerMode.FixedCoresPerInstance, stored.InstanceBalancerMode);
        Assert.Equal(30, stored.StartupBoostSeconds);
    }

    [Fact]
    public void SaveProcessRule_UpdatesInPlaceAndKeepsTheId()
    {
        var h = new ThinHarness();
        var first = h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, Rule()));

        first.PriorityClass = (int)ProcessPriorityClass.BelowNormal;
        first.ProcessName = "renamed.exe";
        var second = h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, first));

        Assert.Equal(first.Id, second.Id);
        var stored = Assert.Single(h.Rules);
        Assert.Equal("renamed.exe", stored.ProcessName);
        Assert.Equal((int)ProcessPriorityClass.BelowNormal, stored.PriorityClass);
    }

    [Fact]
    public void SaveProcessRule_RejectsADuplicateNameOnADifferentId()
    {
        var h = new ThinHarness();
        h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, Rule("game.exe")));
        var other = h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, Rule("other.exe")));

        // A new rule with a taken name (".exe" and case don't matter)...
        var clash = h.SendAsAdmin(CommandTypes.SaveProcessRule, null, Rule("GAME"));
        Assert.False(clash.Success);
        Assert.Contains("already a rule", clash.Error);

        // ...and renaming a rule onto another's name.
        other.ProcessName = "game";
        Assert.False(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, other).Success);
        Assert.Equal(2, h.Rules.Count);
        Assert.Contains(h.Rules, r => r.ProcessName == "other.exe");
    }

    [Fact]
    public void SaveProcessRule_ForAnIdThatNoLongerExists_Fails()
    {
        var h = new ThinHarness();
        var dto = Rule();
        dto.Id = 777;

        var result = h.SendAsAdmin(CommandTypes.SaveProcessRule, null, dto);

        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.Error);
        Assert.Empty(h.Rules);
    }

    public static IEnumerable<object[]> InvalidProcessRules() => new[]
    {
        new object[] { "name", new Action<ProcessRuleDto>(r => r.ProcessName = "  ") },
        new object[] { "priority class", new Action<ProcessRuleDto>(r => r.PriorityClass = 12345) },
        new object[] { "processor", new Action<ProcessRuleDto>(r => r.AffinityMask = 0) },
        new object[] { "CPU limit", new Action<ProcessRuleDto>(r => r.CpuLimitPercent = 0) },
        new object[] { "CPU limit", new Action<ProcessRuleDto>(r => r.CpuLimitPercent = 100) },
        new object[] { "instance limit", new Action<ProcessRuleDto>(r => r.MaxInstances = 0) },
        new object[] { "power plan", new Action<ProcessRuleDto>(r => r.PowerProfileGuid = "nope") },
        new object[] { "I/O priority", new Action<ProcessRuleDto>(r => r.IoPriority = 4) },
        new object[] { "memory priority", new Action<ProcessRuleDto>(r => r.MemoryPriority = 0) },
        new object[] { "GPU priority", new Action<ProcessRuleDto>(r => r.GpuPriority = 5) },
        new object[] { "GPU preference", new Action<ProcessRuleDto>(r => r.GpuPreference = 3) },
        new object[] { "CPU Set", new Action<ProcessRuleDto>(r => r.CpuSetIds = "1,x") },
        new object[] { "threshold", new Action<ProcessRuleDto>(r => r.CpuLimitThresholdPercent = 0) },
        new object[] { "threshold", new Action<ProcessRuleDto>(r => r.CpuLimitThresholdPercent = 101) },
        new object[] { "time over", new Action<ProcessRuleDto>(r => r.CpuLimitOverThresholdSeconds = 0) },
        new object[] { "remove at least", new Action<ProcessRuleDto>(r => r.CpuLimitCoresToRemove = 0) },
        new object[] { "duration", new Action<ProcessRuleDto>(r => r.CpuLimitDurationSeconds = 0) },
        new object[] { "Balancer mode", new Action<ProcessRuleDto>(r => r.InstanceBalancerMode = (BalancerMode)9) },
        new object[] { "at least 1 core", new Action<ProcessRuleDto>(r => r.InstanceBalancerCoresPerInstance = 0) },
        new object[] { "Reserved cores", new Action<ProcessRuleDto>(r => r.InstanceBalancerReservedCores = -1) },
        new object[] { "minimum CPU", new Action<ProcessRuleDto>(r => r.InstanceBalancerMinCpuPercent = 101) },
        new object[] { "startup boost priority", new Action<ProcessRuleDto>(r => r.StartupBoostPriorityClass = 7) },
        new object[] { "startup boost must last", new Action<ProcessRuleDto>(r => r.StartupBoostSeconds = 0) },
        new object[] { "startup boost must last", new Action<ProcessRuleDto>(r => r.StartupBoostSeconds = 3601) },
    };

    [Theory]
    [MemberData(nameof(InvalidProcessRules))]
    public void SaveProcessRule_RejectsInvalidValues(string expectedInError, Action<ProcessRuleDto> spoil)
    {
        var h = new ThinHarness();
        var dto = Rule();
        spoil(dto);

        var result = h.SendAsAdmin(CommandTypes.SaveProcessRule, null, dto);

        Assert.False(result.Success);
        Assert.Contains(expectedInError, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Rules);
    }

    [Fact]
    public void SaveProcessRule_WithoutAnArgument_IsAnError()
    {
        var h = new ThinHarness();
        Assert.False(h.SendAsAdmin(CommandTypes.SaveProcessRule).Success);
        Assert.False(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, "{not json").Success);
    }

    [Fact]
    public void SavingARule_AppliesItToTheProcessesAlreadyRunning()
    {
        var h = new ThinHarness();
        h.Process(10, "game");
        h.Process(11, "Game");
        h.Process(12, "other");
        var dto = Rule("game.exe");
        dto.AffinityMask = 0x3;
        dto.CpuLimitPercent = 40;
        dto.IoPriority = 0;
        dto.ExcludeFromProBalance = true;

        Assert.True(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, dto).Success);

        foreach (var pid in new[] { 10, 11 })
        {
            h.Access.Verify(a => a.SetPriorityClass(pid, ProcessPriorityClass.High), Times.Once);
            h.Access.Verify(a => a.SetAffinityMask(pid, 0x3), Times.Once);
            h.CpuLimiter.Verify(c => c.AddLimit(pid, 40), Times.Once);
            h.ProBalance.Verify(p => p.ReleaseRestraint(pid), Times.Once);
        }
        h.Access.Verify(a => a.SetPriorityClass(12, It.IsAny<ProcessPriorityClass>()), Times.Never);
        Assert.Contains(h.Manual.GetRecent(20), e => e.Kind == ActivityKind.Rule && e.Pid == 10);
    }

    [Fact]
    public void RemovingTheHardCap_ReleasesTheJobObjectCapOfRunningInstances()
    {
        var h = new ThinHarness();
        h.Process(10, "game");
        var dto = Rule();
        dto.CpuLimitPercent = 40;
        var saved = h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, dto));
        h.CpuLimiter.Invocations.Clear();

        saved.CpuLimitPercent = null;
        Assert.True(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, saved).Success);

        h.CpuLimiter.Verify(c => c.RemoveLimit(10), Times.Once);
        h.CpuLimiter.Verify(c => c.AddLimit(It.IsAny<int>(), It.IsAny<double>()), Times.Never);
    }

    [Fact]
    public void RenamingARuleWithACap_ReleasesTheCapFromTheOldNamesProcesses()
    {
        var h = new ThinHarness();
        h.Process(10, "old");
        h.Process(11, "new");
        var dto = Rule("old.exe");
        dto.CpuLimitPercent = 40;
        var saved = h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, dto));
        h.CpuLimiter.Invocations.Clear();

        saved.ProcessName = "new.exe";
        Assert.True(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, saved).Success);

        h.CpuLimiter.Verify(c => c.RemoveLimit(10), Times.Once);
        h.CpuLimiter.Verify(c => c.AddLimit(11, 40), Times.Once);
    }

    [Fact]
    public void DeletingARule_ReleasesItsCap_ButNotACapSetByHand()
    {
        var h = new ThinHarness();
        h.Process(10, "capped");
        h.Process(11, "manual");
        var capped = Rule("capped.exe");
        capped.CpuLimitPercent = 25;
        var withCap = h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, capped));
        var withoutCap = h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, Rule("manual.exe")));
        h.CpuLimiter.Invocations.Clear();

        Assert.True(h.SendAsAdmin(CommandTypes.DeleteProcessRule, null, withCap.Id).Success);
        Assert.True(h.SendAsAdmin(CommandTypes.DeleteProcessRule, null, withoutCap.Id).Success);

        h.CpuLimiter.Verify(c => c.RemoveLimit(10), Times.Once);
        h.CpuLimiter.Verify(c => c.RemoveLimit(11), Times.Never);
        Assert.Empty(h.Rules);
    }

    [Fact]
    public void DeleteProcessRule_ForAMissingRule_Fails()
    {
        var h = new ThinHarness();
        var result = h.SendAsAdmin(CommandTypes.DeleteProcessRule, null, 5);

        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.Error);
        Assert.False(h.SendAsAdmin(CommandTypes.DeleteProcessRule, null, "abc").Success);
    }

    [Fact]
    public void ADisallowRule_EndsRunningInstances_ButNeverCriticalOnes()
    {
        var h = new ThinHarness();
        h.Process(10, "victim");
        h.Process(11, "victim");
        h.Access.Setup(a => a.IsCritical(11)).Returns(true);

        var dto = new ProcessRuleDto { ProcessName = "victim.exe", DisallowExecution = true };
        Assert.True(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, dto).Success);

        h.Access.Verify(a => a.Terminate(10), Times.Once);
        h.Access.Verify(a => a.Terminate(11), Times.Never);

        h.Process(4, "System");
        Assert.True(h.SendAsAdmin(CommandTypes.SaveProcessRule, null, new ProcessRuleDto { ProcessName = "system", DisallowExecution = true }).Success);
        h.Access.Verify(a => a.Terminate(4), Times.Never);
    }

    [Fact]
    public void ALegacyRuleRequest_StillWorksWithTheDto()
    {
        var h = new ThinHarness();
        h.Rules.Add(new ProcessRule { Id = 3, ProcessName = "game.exe", PriorityClass = 64 });

        var list = h.Dispatcher.Dispatch(new CommandMessage { Type = MessageType.GetRules });
        Assert.Equal(MessageType.RuleList, list.Type);
        var dtos = JsonSerializer.Deserialize<List<ProcessRuleDto>>(list.Payload)!;
        Assert.Equal(64, Assert.Single(dtos).PriorityClass);

        dtos[0].IoPriority = 1;
        var reply = h.Dispatcher.Dispatch(new CommandMessage { Type = MessageType.SetRule, Payload = JsonSerializer.Serialize(dtos[0]) });
        Assert.True(JsonSerializer.Deserialize<CommandResult>(reply.Payload)!.Success);
        Assert.Equal(1, h.Rules.Single().IoPriority);
        Assert.Equal(3, h.Rules.Single().Id);
    }

    // ---------- Watchdog rules ----------

    private static WatchdogRuleDto Watch(WatchdogResponse action = WatchdogResponse.LogAlert, string? argument = null) => new()
    {
        TargetProcessName = "app.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 50, DurationSeconds = 10,
        ActionType = action, ActionArgument = argument,
    };

    [Fact]
    public void WatchdogRules_AreAddedListedUpdatedAndDeleted()
    {
        var h = new ThinHarness();

        var saved = h.Data<WatchdogRuleDto>(h.SendAsAdmin(CommandTypes.SaveWatchdogRule, null, Watch(WatchdogResponse.SetPriority, "BelowNormal")));
        Assert.NotEqual(0, saved.Id);
        Assert.Equal(WatchdogResponse.SetPriority, saved.ActionType);
        Assert.Equal(WatchdogAction.SetPriority, h.WatchdogRules.Single().ActionType);

        saved.ThresholdValue = 75;
        var updated = h.Data<WatchdogRuleDto>(h.SendAsAdmin(CommandTypes.SaveWatchdogRule, null, saved));
        Assert.Equal(saved.Id, updated.Id);
        Assert.Equal(75, h.WatchdogRules.Single().ThresholdValue);

        var listed = h.Data<List<WatchdogRuleDto>>(h.SendAsAdmin(CommandTypes.GetWatchdogRules));
        Assert.Equal(75, Assert.Single(listed).ThresholdValue);

        Assert.True(h.SendAsAdmin(CommandTypes.DeleteWatchdogRule, null, saved.Id).Success);
        Assert.Empty(h.WatchdogRules);
        Assert.False(h.SendAsAdmin(CommandTypes.DeleteWatchdogRule, null, saved.Id).Success);
    }

    [Fact]
    public void SaveWatchdogRule_ForAMissingId_Fails()
    {
        var h = new ThinHarness();
        var dto = Watch();
        dto.Id = 99;
        Assert.Contains("no longer exists", h.SendAsAdmin(CommandTypes.SaveWatchdogRule, null, dto).Error);
    }

    public static IEnumerable<object?[]> InvalidWatchdogRules() => new[]
    {
        new object?[] { "process name", new Action<WatchdogRuleDto>(r => r.TargetProcessName = "") },
        new object?[] { "condition", new Action<WatchdogRuleDto>(r => r.ConditionType = (WatchdogTrigger)9) },
        new object?[] { "action", new Action<WatchdogRuleDto>(r => r.ActionType = (WatchdogResponse)42) },
        new object?[] { "memory measure", new Action<WatchdogRuleDto>(r => r.MemoryMetric = (MemoryMetric)5) },
        new object?[] { "greater than zero", new Action<WatchdogRuleDto>(r => r.ThresholdValue = 0) },
        new object?[] { "below 100", new Action<WatchdogRuleDto>(r => r.ThresholdValue = 100) },
        new object?[] { "negative", new Action<WatchdogRuleDto>(r => r.DurationSeconds = -1) },
        new object?[] { "duration", new Action<WatchdogRuleDto>(r => r.ActionDurationSeconds = -1) },
        new object?[] { "cooldown", new Action<WatchdogRuleDto>(r => r.CooldownSeconds = -1) },
        new object?[] { "priority", new Action<WatchdogRuleDto>(r => { r.ActionType = WatchdogResponse.SetPriority; r.ActionArgument = "Turbo"; }) },
        new object?[] { "I/O priority", new Action<WatchdogRuleDto>(r => { r.ActionType = WatchdogResponse.SetIoPriority; r.ActionArgument = "4"; }) },
        new object?[] { "CPU mask", new Action<WatchdogRuleDto>(r => { r.ActionType = WatchdogResponse.SetAffinity; r.ActionArgument = "0"; }) },
        new object?[] { "CPU mask", new Action<WatchdogRuleDto>(r => { r.ActionType = WatchdogResponse.SetAffinity; r.ActionArgument = null; }) },
        new object?[] { "command line", new Action<WatchdogRuleDto>(r => { r.ActionType = WatchdogResponse.Launch; r.ActionArgument = "  "; }) },
        new object?[] { "continuous", new Action<WatchdogRuleDto>(r => { r.ActionType = WatchdogResponse.TrimWorkingSet; r.ActionArgument = "often"; }) },
    };

    [Theory]
    [MemberData(nameof(InvalidWatchdogRules))]
    public void SaveWatchdogRule_RejectsInvalidValues(string expectedInError, Action<WatchdogRuleDto> spoil)
    {
        var h = new ThinHarness();
        var dto = Watch();
        spoil(dto);

        var result = h.SendAsAdmin(CommandTypes.SaveWatchdogRule, null, dto);

        Assert.False(result.Success);
        Assert.Contains(expectedInError, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.WatchdogRules);
    }

    [Theory]
    [InlineData(WatchdogResponse.SetPriority, "Below Normal")]
    [InlineData(WatchdogResponse.SetPriority, null)]
    [InlineData(WatchdogResponse.SetIoPriority, "VeryLow")]
    [InlineData(WatchdogResponse.SetAffinity, "0x3")]
    [InlineData(WatchdogResponse.Launch, "\"C:\\Program Files\\tool.exe\" --flag")]
    [InlineData(WatchdogResponse.TrimWorkingSet, "continuous")]
    [InlineData(WatchdogResponse.TrimWorkingSet, null)]
    [InlineData(WatchdogResponse.Notify, "Chrome is hogging the CPU")]
    [InlineData(WatchdogResponse.Terminate, null)]
    [InlineData(WatchdogResponse.Restart, null)]
    public void SaveWatchdogRule_AcceptsArgumentsTheWatchdogCanRead(WatchdogResponse action, string? argument)
    {
        var h = new ThinHarness();
        Assert.True(h.SendAsAdmin(CommandTypes.SaveWatchdogRule, null, Watch(action, argument)).Success);
    }

    // ---------- Settings ----------

    [Fact]
    public void GetSettings_ReturnsTheStoredSettingsWithoutTheirKey()
    {
        var h = new ThinHarness();
        h.Stored.ProBalanceSensitivity = 8;
        h.Stored.Theme = "Dark";
        h.Stored.IdlePowerPlanGuid = Powrprof.Balanced.ToString();

        var dto = h.Data<EngineSettingsDto>(h.SendAsAdmin(CommandTypes.GetSettings));

        Assert.Equal(8, dto.ProBalanceSensitivity);
        Assert.Equal("Dark", dto.Theme);
        Assert.Equal(Powrprof.Balanced.ToString(), dto.IdlePowerPlanGuid);
        Assert.False(dto.ForegroundBoostEnabled);
    }

    [Fact]
    public void SaveSettings_StoresAppliesAndReturnsWhatIsStored_KeepingTheRowId()
    {
        var h = new ThinHarness();
        var dto = h.Data<EngineSettingsDto>(h.SendAsAdmin(CommandTypes.GetSettings));
        dto.SmartTrimEnabled = true;
        dto.SmartTrimMemoryThresholdMB = 512;
        dto.PollingIntervalMs = 2500;
        dto.ForegroundBoostEnabled = true;
        dto.SmartTrimExclusions = "keep.exe";

        var stored = h.Data<EngineSettingsDto>(h.SendAsAdmin(CommandTypes.SaveSettings, null, dto));

        Assert.Equal(1, h.Stored.Id);
        Assert.True(h.Stored.SmartTrimEnabled);
        Assert.Equal(512, h.Stored.SmartTrimMemoryThresholdMB);
        Assert.Equal("keep.exe", stored.SmartTrimExclusions);
        Assert.True(stored.ForegroundBoostEnabled);
        h.SmartTrim.Verify(t => t.Configure(true, It.IsAny<TimeSpan>(), 512L * 1024 * 1024), Times.Once);
        h.Monitor.GetType(); // the fake ignores the interval; the applier test covers it
        Assert.True(h.ForegroundBoost.IsEnabled);
        Assert.Contains(h.Manual.GetRecent(5), e => e.Message.Contains("settings"));
    }

    [Fact]
    public void SaveSettings_TurningForegroundBoostOff_StopsIt()
    {
        var h = new ThinHarness();
        var dto = h.Data<EngineSettingsDto>(h.SendAsAdmin(CommandTypes.GetSettings));
        dto.ForegroundBoostEnabled = true;
        h.SendAsAdmin(CommandTypes.SaveSettings, null, dto);
        Assert.True(h.ForegroundBoost.IsEnabled);

        dto.ForegroundBoostEnabled = false;
        h.SendAsAdmin(CommandTypes.SaveSettings, null, dto);
        Assert.False(h.ForegroundBoost.IsEnabled);
    }

    public static IEnumerable<object[]> InvalidSettings() => new[]
    {
        new object[] { new Action<EngineSettingsDto>(s => s.ProBalanceSensitivity = 0) },
        new object[] { new Action<EngineSettingsDto>(s => s.ProBalanceSensitivity = 11) },
        new object[] { new Action<EngineSettingsDto>(s => s.ProBalanceCpuThreshold = 101) },
        new object[] { new Action<EngineSettingsDto>(s => s.PollingIntervalMs = 10) },
        new object[] { new Action<EngineSettingsDto>(s => s.PollingIntervalMs = 3_600_000) },
        new object[] { new Action<EngineSettingsDto>(s => s.IdleTimeoutMinutes = 0) },
        new object[] { new Action<EngineSettingsDto>(s => s.IdlePowerPlanGuid = "not-a-guid") },
        new object[] { new Action<EngineSettingsDto>(s => s.StartWithPowerPlanGuid = "nope") },
        new object[] { new Action<EngineSettingsDto>(s => s.LoadSwitcherHighThreshold = -1) },
        new object[] { new Action<EngineSettingsDto>(s => s.Theme = "") },
        new object[] { new Action<EngineSettingsDto>(s => s.SmartTrimMinimumRamLoadPercent = 150) },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void SaveSettings_RejectsInvalidValuesWithoutStoringAnything(Action<EngineSettingsDto> spoil)
    {
        var h = new ThinHarness();
        var dto = h.Data<EngineSettingsDto>(h.SendAsAdmin(CommandTypes.GetSettings));
        spoil(dto);

        Assert.False(h.SendAsAdmin(CommandTypes.SaveSettings, null, dto).Success);
        h.Store.Verify(s => s.UpdateSettings(It.IsAny<AppSettings>()), Times.Never);
    }

    // ---------- Power ----------

    [Fact]
    public void GetPower_ListsEveryInstalledPlanAndTheState()
    {
        var h = new ThinHarness();
        h.ActivePlan = Powrprof.HighPerformance;
        h.Power.SetupGet(p => p.IsPerformanceModeActive).Returns(true);
        h.Power.SetupGet(p => p.IsPerformanceModeAutomatic).Returns(true);
        h.Power.SetupGet(p => p.PlanOwnerName).Returns("PerformanceMode");

        var power = h.Data<PowerStateDto>(h.SendAsAdmin(CommandTypes.GetPower));

        Assert.Equal(h.Plans.Select(p => p.Item1), power.Plans.Select(p => p.Id));
        Assert.Equal("Power saver", power.Plans.Single(p => p.Id == Powrprof.PowerSaver).Name);
        Assert.Equal(Powrprof.HighPerformance, power.ActivePlanId);
        Assert.True(power.PerformanceMode);
        Assert.True(power.PerformanceModeAutomatic);
        Assert.Equal("PerformanceMode", power.Owner);
    }

    [Fact]
    public void SetPowerPlan_GoesThroughThePowerManagersArbitration()
    {
        var h = new ThinHarness();

        Assert.True(h.SendAsAdmin(CommandTypes.SetPowerPlan, null, Powrprof.PowerSaver.ToString()).Success);

        h.Power.Verify(p => p.SetActivePlan(Powrprof.PowerSaver), Times.Once);
        Assert.Equal(Powrprof.PowerSaver, h.ActivePlan);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111-2222-3333-4444-555555555555")] // well-formed, but not installed
    [InlineData(null)]
    public void SetPowerPlan_RejectsAnythingButAnInstalledPlan(string? argument)
    {
        var h = new ThinHarness();
        Assert.False(h.SendAsAdmin(CommandTypes.SetPowerPlan, null, argument).Success);
        h.Power.Verify(p => p.SetActivePlan(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void SetPowerPlan_WindowsRefusing_IsReported()
    {
        var h = new ThinHarness();
        h.Power.Setup(p => p.SetActivePlan(It.IsAny<Guid>())); // does nothing: the active plan stays Balanced

        var result = h.SendAsAdmin(CommandTypes.SetPowerPlan, null, Powrprof.HighPerformance.ToString());

        Assert.False(result.Success);
        Assert.Contains("wouldn't switch", result.Error);
    }

    // ---------- Activity ----------

    [Fact]
    public void GetActivity_MergesTheThreeSourcesNewestFirst_WithTheRightKinds()
    {
        var h = new ThinHarness();
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        h.Actions.Add(new ProBalanceAction { Timestamp = t0.AddMinutes(1), ProcessName = "chrome", Pid = 5, Reason = "High CPU usage (12.0%): priority BelowNormal" });
        h.Actions.Add(new ProBalanceAction { Timestamp = t0.AddMinutes(2), ProcessName = "chrome", Pid = 5, Reason = "Restored: CPU usage dropped to 1.0%", IsRestored = true });
        h.Actions.Add(new ProBalanceAction { Timestamp = t0.AddMinutes(3), ProcessName = "encoder", Pid = 6, Reason = "Constraining CPU affinity by 2 CPUs for 30000 ms." });
        h.Actions.Add(new ProBalanceAction { Timestamp = t0.AddMinutes(4), ProcessName = ProBalanceAction.PowerPlanEntryName, Reason = "Balanced -> High performance: Performance Mode turned on", IsRestored = true });
        var fired = h.WatchdogEvents.Add(WatchdogEventKinds.Fired, "Watchdog: app", "terminated app (9)", "app", 9, 3);
        var restored = h.WatchdogEvents.Add(WatchdogEventKinds.Restored, "Watchdog: app", "priority put back", "app", 9, 3);
        h.Manual.Add(ActivityKind.Manual, "game", 7, "Set priority to High.");

        var entries = h.Data<List<ActivityEntry>>(h.SendAsAdmin(CommandTypes.GetActivity, null, 50));

        Assert.Equal(7, entries.Count);
        Assert.Equal(entries.OrderByDescending(e => e.TimestampUtc).Select(e => e.Message), entries.Select(e => e.Message));
        Assert.Equal("Set priority to High.", entries[0].Message);
        Assert.Equal(ActivityKind.Manual, entries[0].Kind);

        ActivityEntry Find(string text) => entries.Single(e => e.Message.Contains(text));
        Assert.Equal(ActivityKind.Restrained, Find("High CPU usage").Kind);
        Assert.Equal(ActivityKind.Restored, Find("Restored: CPU usage").Kind);
        Assert.Equal(ActivityKind.Rule, Find("Constraining CPU affinity").Kind);
        Assert.Equal(ActivityKind.Power, Find("Performance Mode turned on").Kind);
        Assert.Equal(string.Empty, Find("Performance Mode turned on").ProcessName);
        Assert.Equal(ActivityKind.Watchdog, Find("terminated app").Kind);
        Assert.Equal(9, Find("terminated app").Pid);
        Assert.Equal(ActivityKind.Restored, Find("priority put back").Kind);
        Assert.NotNull(fired);
        Assert.NotNull(restored);
    }

    [Fact]
    public void GetActivity_HonoursTheCount_AndDefaultsWhenThereIsNone()
    {
        var h = new ThinHarness();
        for (var i = 0; i < 300; i++) h.Manual.Add(ActivityKind.Manual, "p", i, $"action {i}");

        var three = h.Data<List<ActivityEntry>>(h.SendAsAdmin(CommandTypes.GetActivity, null, 3));
        Assert.Equal(new[] { "action 299", "action 298", "action 297" }, three.Select(e => e.Message));

        Assert.Equal(ActivityFeed.DefaultCount, h.Data<List<ActivityEntry>>(h.SendAsAdmin(CommandTypes.GetActivity)).Count);
        Assert.False(h.SendAsAdmin(CommandTypes.GetActivity, null, 0).Success);
        Assert.False(h.SendAsAdmin(CommandTypes.GetActivity, null, "lots").Success);
    }

    [Fact]
    public void ActivityLog_KeepsOnlyTheNewestEntries()
    {
        var log = new ActivityLog(capacity: 3);
        for (var i = 1; i <= 5; i++) log.Add(ActivityKind.Manual, "p", i, $"m{i}");

        Assert.Equal(3, log.Count);
        Assert.Equal(new[] { "m5", "m4", "m3" }, log.GetRecent(10).Select(e => e.Message));
    }

    [Fact]
    public void ClearActionLog_DeletesTheLoggedActions()
    {
        var h = new ThinHarness();
        h.Actions.Add(new ProBalanceAction { Timestamp = DateTime.UtcNow, ProcessName = "x", Reason = "r" });

        Assert.True(h.SendAsAdmin(CommandTypes.ClearActionLog).Success);

        h.Store.Verify(s => s.ClearProBalanceActions(), Times.Once);
        Assert.Empty(h.Actions);
    }

    // ---------- CPU Sets ----------

    [Fact]
    public void GetCpuSets_MapsTheSystemsCpuSets()
    {
        var h = new ThinHarness();
        h.Tuning.SystemCpuSets.Clear();
        h.Tuning.SystemCpuSets.Add(new CpuSetInfo(256, 1, 4, 2, 0, 3, 5, true, false));

        var set = Assert.Single(h.Data<List<CpuSetDto>>(h.SendAsAdmin(CommandTypes.GetCpuSets)));

        Assert.Equal(256, set.Id);
        Assert.Equal(1, set.Group);
        Assert.Equal(4, set.LogicalProcessorIndex);
        Assert.Equal(2, set.CoreIndex);
        Assert.Equal(3, set.NumaNodeIndex);
        Assert.Equal(5, set.EfficiencyClass);
        Assert.True(set.IsParked);
    }

    [Fact]
    public void GetCpuSets_WhenWindowsFails_IsReportedNotThrown()
    {
        var h = new ThinHarness();
        var tuning = new Mock<ProGuard.Engine.Services.ProcessRules.IProcessTuningNative>();
        tuning.Setup(t => t.GetSystemCpuSets()).Throws(new System.ComponentModel.Win32Exception("The parameter is incorrect"));
        var feed = new ActivityFeed(h.Store.Object, h.Manual);
        var thin = new ThinClientCommands(Microsoft.Extensions.Logging.Abstractions.NullLogger<ThinClientCommands>.Instance, h.Snapshots,
            new ProcessActionService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessActionService>.Instance, tuning.Object, h.Monitor, h.Controller.Object),
            new RuleService(Microsoft.Extensions.Logging.Abstractions.NullLogger<RuleService>.Instance, h.Store.Object, h.Monitor, h.CpuLimiter.Object, h.ProBalance.Object, h.Access.Object, h.Manual),
            feed, h.Manual, h.Store.Object, h.Power.Object, h.Monitor, tuning.Object);
        var dispatcher = new IpcDispatcher(Microsoft.Extensions.Logging.Abstractions.NullLogger<IpcDispatcher>.Instance, h.Store.Object,
            new SettingsApplier(Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsApplier>.Instance, h.Monitor, h.SmartTrim.Object, h.Power.Object, h.ProBalance.Object),
            h.ProBalance.Object, h.Power.Object, h.SmartTrim.Object, h.CpuLimiter.Object, h.Controller.Object, h.Monitor, null, thin);

        var reply = dispatcher.Dispatch(new CommandMessage { CommandType = CommandTypes.GetCpuSets });
        var result = JsonSerializer.Deserialize<CommandResult>(reply.Payload)!;

        Assert.False(result.Success);
        Assert.Contains("parameter is incorrect", result.Error);
    }
}
