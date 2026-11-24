using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;

namespace ProGuard.Tests;

/// <summary>Rules and automatic features never act on a process Windows can't do without, and never raise to Realtime on their own.</summary>
public class SafetyGuardTests
{
    private static ProcessOrchestrator Orchestrator(FakeProcessMonitor monitor, List<ProcessRule> rules, ICpuLimiter limiter,
        IRuleProcessAccess? access, PersistentRuleService? persistent = null) =>
        new(NullLogger<ProcessOrchestrator>.Instance, monitor, Mock.Of<IResponsivenessGuardEngine>(), limiter,
            Mock.Of<IMemoryTrimService>(), Mock.Of<IPowerManager>(), Mock.Of<IWatchdogService>(), FakeRules.Store(rules).Object,
            persistentRules: persistent, access: access);

    private static Process StartChild() =>
        Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;

    [Fact]
    public void Orchestrator_LeavesAProcessWindowsMarksCriticalAlone()
    {
        using var child = StartChild();
        try
        {
            var monitor = new FakeProcessMonitor();
            var limiter = new Mock<ICpuLimiter>();
            var access = new FakeRuleProcessAccess();
            var rules = new List<ProcessRule> { new() { ProcessName = "ping.exe", CpuLimitPercent = 50, DisallowExecution = false } };
            var orchestrator = Orchestrator(monitor, rules, limiter.Object, access);
            orchestrator.Start();

            access.Critical.Add(child.Id);
            monitor.Start(child.Id, "ping");
            limiter.Verify(l => l.AddLimit(It.IsAny<int>(), It.IsAny<double>()), Times.Never);

            // The same process, no longer critical: the rule applies (so the check above isn't vacuous).
            access.Critical.Clear();
            monitor.Start(child.Id, "ping");
            limiter.Verify(l => l.AddLimit(child.Id, 50), Times.Once);
            orchestrator.Stop();
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Orchestrator_NeverEndsACriticalProcessADisallowRuleNames()
    {
        using var child = StartChild();
        try
        {
            var monitor = new FakeProcessMonitor();
            var access = new FakeRuleProcessAccess();
            access.Critical.Add(child.Id);
            var rules = new List<ProcessRule> { new() { ProcessName = "ping.exe", DisallowExecution = true } };
            var orchestrator = Orchestrator(monitor, rules, Mock.Of<ICpuLimiter>(), access);
            orchestrator.Start();

            monitor.Start(child.Id, "ping");
            Assert.False(child.WaitForExit(500));
            orchestrator.Stop();
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Theory]
    [InlineData("csrss")]
    [InlineData("lsass")]
    [InlineData("svchost")]
    [InlineData("ProGuard.UI")]
    public void Orchestrator_LeavesCriticalNamesAlone_EvenForAnOlderStoredRule(string name)
    {
        var monitor = new FakeProcessMonitor();
        var limiter = new Mock<ICpuLimiter>();
        var applier = new Mock<IProcessRuleApplier>();
        var persistent = new PersistentRuleService(NullLogger<PersistentRuleService>.Instance, new[] { applier.Object }, monitor, FakeRules.Store(new()).Object);
        var rules = new List<ProcessRule> { new() { ProcessName = name + ".exe", CpuLimitPercent = 10, IoPriority = 0 } };
        var orchestrator = Orchestrator(monitor, rules, limiter.Object, new FakeRuleProcessAccess(), persistent);
        orchestrator.Start();

        monitor.Start(Environment.ProcessId + 100000, name);

        limiter.Verify(l => l.AddLimit(It.IsAny<int>(), It.IsAny<double>()), Times.Never);
        applier.Verify(a => a.Apply(It.IsAny<ProcessRule>(), It.IsAny<ProcessTarget>(), It.IsAny<bool>()), Times.Never);
        orchestrator.Stop();
    }

    [Fact]
    public void PersistentRules_NeverApplyToACriticalName()
    {
        var applier = new Mock<IProcessRuleApplier>();
        var persistent = new PersistentRuleService(NullLogger<PersistentRuleService>.Instance, new[] { applier.Object },
            new FakeProcessMonitor(), FakeRules.Store(new()).Object);

        Assert.Empty(persistent.Apply(new ProcessRule { ProcessName = "lsass.exe", IoPriority = 0 }, new ProcessTarget(800, "lsass", null)));
        applier.Verify(a => a.Apply(It.IsAny<ProcessRule>(), It.IsAny<ProcessTarget>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void CriticalTargetCheck_AllowsOnlyRulesThatLeaveTheProcessAlone()
    {
        Assert.Null(RuleAuthorization.CheckCriticalTarget(new ProcessRule { ProcessName = "dwm.exe", ExcludeFromResponsivenessGuard = true }));
        Assert.Null(RuleAuthorization.CheckCriticalTarget(new ProcessRule { ProcessName = "notepad.exe", DisallowExecution = true }));
        Assert.NotNull(RuleAuthorization.CheckCriticalTarget(new ProcessRule { ProcessName = "dwm.exe", GpuPriority = 1 }));
        Assert.NotNull(RuleAuthorization.CheckCriticalTarget(new ProcessRule { ProcessName = "SVCHOST.EXE", CpuSetIds = "256" }));
        Assert.NotNull(RuleAuthorization.CheckCriticalTarget(new WatchdogRule { TargetProcessName = "csrss.exe", ActionType = WatchdogAction.Terminate }));
        Assert.Null(RuleAuthorization.CheckCriticalTarget(new WatchdogRule { TargetProcessName = "csrss.exe", ActionType = WatchdogAction.Notify }));
    }

    // ---------- Realtime: never from automatic features ----------

    [Fact]
    public void StartupBoost_RaisesToHighAtMost()
    {
        var access = new FakeRuleProcessAccess();
        var clock = new ManualTimeProvider();
        var boost = new StartupBoostService(NullLogger<StartupBoostService>.Instance, access, clock);
        access.Priority[1] = ProcessPriorityClass.Normal;
        access.Start[1] = clock.Now.UtcDateTime;

        Assert.True(boost.TryBoost(new ProcessInfo { Pid = 1, Name = "app" },
            new ProcessRule { ProcessName = "app.exe", StartupBoostPriorityClass = (int)ProcessPriorityClass.RealTime, StartupBoostSeconds = 5 }));
        Assert.Equal(ProcessPriorityClass.High, access.Priority[1]);
    }

    [Fact]
    public void StartupBoost_LeavesARealtimeProcessAlone()
    {
        var access = new FakeRuleProcessAccess();
        var clock = new ManualTimeProvider();
        var boost = new StartupBoostService(NullLogger<StartupBoostService>.Instance, access, clock);
        access.Priority[1] = ProcessPriorityClass.RealTime;
        access.Start[1] = clock.Now.UtcDateTime;

        Assert.False(boost.TryBoost(new ProcessInfo { Pid = 1, Name = "app" },
            new ProcessRule { ProcessName = "app.exe", StartupBoostPriorityClass = (int)ProcessPriorityClass.High, StartupBoostSeconds = 5 }));
        Assert.Equal(ProcessPriorityClass.RealTime, access.Priority[1]);
    }

    [Fact]
    public void ResponsivenessGuard_NeverRestrainsARealtimeProcess()
    {
        using var h = new ResponsivenessGuardHarness();
        h.Engine.RestrainOnlyNormalPriority = false;
        h.Add(100, cpu: 50, priority: ProcessPriorityClass.RealTime);
        h.Add(101, cpu: 50, priority: ProcessPriorityClass.High);

        h.At(0);
        h.At(1000);

        Assert.Equal(ProcessPriorityClass.RealTime, h.PriorityOf(100));
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(101));
    }
}
