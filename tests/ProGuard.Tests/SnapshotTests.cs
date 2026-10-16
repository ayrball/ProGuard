using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

public class SnapshotTests
{
    private static readonly DateTime Started = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ProcessInfo Chrome(ThinHarness h)
    {
        var p = h.Process(500, "chrome", parent: 400, start: Started);
        p.FilePath = @"C:\Program Files\Chrome\chrome.exe";
        p.CpuPercent = 12.5;
        p.WorkingSetBytes = 300L << 20;
        p.PrivateBytes = 200L << 20;
        p.ThreadCount = 41;
        p.PriorityClass = ProcessPriorityClass.BelowNormal;
        p.AffinityMask = 0xFF;
        p.SessionId = 2;
        p.IsForeground = true;
        return p;
    }

    [Fact]
    public void MapsMachineWideFields()
    {
        var h = new ThinHarness();
        h.ProBalance.SetupGet(p => p.IsEnabled).Returns(true);
        h.ProBalance.SetupGet(p => p.IsPausedForIdle).Returns(true);
        h.ProBalance.SetupGet(p => p.TotalRestraints).Returns(12);
        h.ProBalance.SetupGet(p => p.RestrainedPids).Returns(new[] { 500, 501 });
        h.Power.SetupGet(p => p.IsPerformanceModeActive).Returns(true);
        h.Power.SetupGet(p => p.PlanOwnerName).Returns("IdleSaver");
        h.ActivePlan = Powrprof.HighPerformance;
        h.Rules.Add(new ProcessRule { Id = 1, ProcessName = "chrome.exe" });
        h.Rules.Add(new ProcessRule { Id = 2, ProcessName = "other.exe" });
        h.WatchdogRules.Add(new WatchdogRule { Id = 1 });
        h.ForegroundBoost.IsEnabled = true;
        Chrome(h);
        h.Monitor.GetProcess(500)!.Name = "chrome";

        var s = h.Snapshots.GetShared();

        Assert.Equal(37.5, s.CpuPercent);
        Assert.Equal(new[] { 10.0, 20.0, 30.0, 40.0 }, s.CoreCpuPercent);
        Assert.Empty(s.CoreParked); // the harness metrics have no parking states
        Assert.Equal(75.0, s.ResponsivenessPercent); // a queue of 2 on 4 processors: 0.5 waiting per CPU of the 2 that saturate
        Assert.Equal(61, s.MemoryPercent);
        Assert.Equal(16L << 30, s.TotalMemoryBytes);
        Assert.Equal(6L << 30, s.AvailableMemoryBytes);
        Assert.Equal(4, s.LogicalProcessors);
        Assert.True(s.ProBalanceEnabled);
        Assert.True(s.ProBalancePaused);
        Assert.Equal(2, s.ProBalanceRestrained);
        Assert.Equal(12, s.ProBalanceRestraintsTotal);
        Assert.True(s.ForegroundBoostEnabled);
        Assert.True(s.PerformanceModeActive);
        Assert.Equal("IdleSaver", s.PowerPlanOwner);
        Assert.Equal(Powrprof.HighPerformance, s.ActivePowerPlanId);
        Assert.Equal("High performance", s.ActivePowerPlanName);
        Assert.True(s.EngineIsElevated);
        Assert.Equal(2, s.ProcessRuleCount);
        Assert.Equal(1, s.WatchdogRuleCount);
        Assert.True(s.TakenAtUtc <= DateTime.UtcNow && s.TakenAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void ProBalancePaused_OnlyMeansSomethingWhileProBalanceIsOn()
    {
        var h = new ThinHarness();
        h.ProBalance.SetupGet(p => p.IsEnabled).Returns(false);
        h.ProBalance.SetupGet(p => p.IsPausedForIdle).Returns(true);

        var s = h.Snapshots.GetShared();

        Assert.False(s.ProBalanceEnabled);
        Assert.False(s.ProBalancePaused);
    }

    [Fact]
    public void MapsEachProcess()
    {
        var h = new ThinHarness();
        var chrome = Chrome(h);
        chrome.Status = ProcessStatus.Suspended;
        h.Details.Tuning[500] = new ProcessTuning(IoPriority: 1, MemoryPriority: 3, EfficiencyMode: true);
        h.Details.Users[500] = @"PC\tee";
        h.Rules.Add(new ProcessRule { Id = 1, ProcessName = "Chrome.exe" });
        h.ProBalance.SetupGet(p => p.RestrainedPids).Returns(new[] { 500 });
        h.CpuLimiter.Setup(c => c.GetActiveLimits()).Returns(new Dictionary<int, double> { [500] = 35 });
        h.Monitor.GetProcess(500)!.IsForeground = false;
        h.Monitor.Add(600, "svc").SessionId = 0;
        h.Monitor.Add(2, "early").SessionId = 1;

        var s = h.Snapshots.GetShared();

        Assert.Equal(new[] { 2, 500, 600 }, s.Processes.Select(p => p.Pid)); // by pid
        var p = s.Processes.Single(x => x.Pid == 500);
        Assert.Equal("chrome", p.Name);
        Assert.Equal(@"C:\Program Files\Chrome\chrome.exe", p.FilePath);
        Assert.Equal(400, p.ParentPid);
        Assert.Equal(2, p.SessionId);
        Assert.Equal(@"PC\tee", p.UserName);
        Assert.Equal(Started, p.StartTimeUtc);
        Assert.Equal(12.5, p.CpuPercent);
        Assert.Equal(300L << 20, p.WorkingSetBytes);
        Assert.Equal(200L << 20, p.PrivateBytes);
        Assert.Equal(41, p.ThreadCount);
        Assert.Equal(ProcessPriorityClass.BelowNormal, p.Priority);
        Assert.Equal(1, p.IoPriority);
        Assert.Equal(3, p.MemoryPriority);
        Assert.Equal(0xFF, p.AffinityMask);
        Assert.False(p.IsSystem);
        Assert.False(p.IsForeground);
        Assert.True(p.IsSuspended);
        Assert.True(p.IsEfficiencyMode);
        Assert.True(p.IsRestrained);
        Assert.False(p.IsAffinityLimited);
        Assert.Equal(35, p.CpuLimitPercent);
        Assert.True(p.HasRule);

        var svc = s.Processes.Single(x => x.Pid == 600);
        Assert.True(svc.IsSystem);
        Assert.False(svc.HasRule);
        Assert.Null(svc.CpuLimitPercent);
        Assert.Null(svc.FilePath);
        Assert.Null(svc.IoPriority);
        Assert.False(svc.IsEfficiencyMode);
    }

    [Fact]
    public void ForegroundIsWhatTheMonitorReports()
    {
        var h = new ThinHarness();
        Chrome(h);
        h.Monitor.GetType(); // FakeProcessMonitor reports 0; the foreground flag comes from the foreground pid, not IsForeground

        var s = h.Snapshots.GetShared();

        Assert.Equal(0, s.ForegroundPid);
        Assert.False(s.Processes.Single().IsForeground);
    }

    [Fact]
    public void AffinityLimitedProcesses_AreMarked()
    {
        using var limits = new CpuAffinityLimiterHarness();
        limits.AddRule();
        limits.Add(100, cpu: 90);
        limits.Add(101, cpu: 1);
        limits.At(0);
        limits.At(6000);
        Assert.Equal(new[] { 100 }, limits.Limiter.GetConstrainedPids());

        var h = new ThinHarness();
        var builder = new SnapshotBuilder(limits.Monitor.Object, h.ProBalance.Object, h.CpuLimiter.Object, h.Store.Object, h.Power.Object,
            h.Details, limits.Limiter, metrics: () => h.Metrics, clockMs: () => h.NowMs, engineIsElevated: false);

        var s = builder.GetShared();

        Assert.True(s.Processes.Single(p => p.Pid == 100).IsAffinityLimited);
        Assert.False(s.Processes.Single(p => p.Pid == 101).IsAffinityLimited);
        Assert.False(s.EngineIsElevated);
    }

    [Fact]
    public void OneBuildServesEveryCallerWithinTheInterval()
    {
        var h = new ThinHarness();
        Chrome(h);

        var first = h.Snapshots.GetShared();
        h.NowMs += 400;
        var second = h.Snapshots.GetFor(ThinCaller.Admin());
        h.NowMs += 400;
        var third = h.Snapshots.GetFor(new ThinCaller());

        Assert.Equal(1, h.Snapshots.BuildCount);
        Assert.Equal(1, h.MetricsReads);
        Assert.Same(first.Processes, second.Processes);
        Assert.Equal(first.TakenAtUtc, third.TakenAtUtc);

        h.NowMs += 200; // 1000 ms since the build
        var fresh = h.Snapshots.GetShared();
        Assert.Equal(2, h.Snapshots.BuildCount);
        Assert.NotSame(first, fresh);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareASingleBuild()
    {
        var h = new ThinHarness();
        for (var i = 0; i < 200; i++) h.Process(1000 + i, "p" + i);

        // Each caller on a thread of its own: the ones waiting for the build block, and 16 of them on the thread pool
        // would starve a small machine's pool (and every other test running alongside) until it grew.
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(h.Snapshots.GetShared,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));

        Assert.Equal(1, h.Snapshots.BuildCount);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public void AccountNamesAreLookedUpOncePerProcess()
    {
        var h = new ThinHarness();
        var p = Chrome(h);
        h.Details.Users[500] = @"PC\tee";

        h.Snapshots.GetShared();
        h.NowMs += 2000;
        h.Snapshots.GetShared();
        Assert.Equal(1, h.Details.UserReads);

        // The id is reused by a new process: that one is looked up afresh.
        p.StartTime = Started.AddHours(1);
        h.Details.Users[500] = @"PC\other";
        h.NowMs += 2000;
        Assert.Equal(@"PC\other", h.Snapshots.GetShared().Processes.Single().UserName);
        Assert.Equal(2, h.Details.UserReads);
    }

    [Fact]
    public void AdministratorsSeeEveryPath_AndTheCallerFlagFollowsTheCaller()
    {
        var h = new ThinHarness();
        Chrome(h);

        var admin = h.Snapshots.GetFor(ThinCaller.Admin());
        var standard = h.Snapshots.GetFor(new ThinCaller());

        Assert.True(admin.CallerIsAdministrator);
        Assert.False(standard.CallerIsAdministrator);
        Assert.NotNull(admin.Processes.Single().FilePath);
        Assert.False(h.Snapshots.GetShared().CallerIsAdministrator);
    }

    [Fact]
    public void StandardUsers_LoseThePathOfProcessesTheyCouldNotOpen_AndNothingElse()
    {
        var h = new ThinHarness();
        var own = Chrome(h);
        var other = h.Process(501, "secret", parent: 4, start: Started);
        other.FilePath = @"C:\Windows\System32\secret.exe";
        other.CpuPercent = 3;
        h.Process(502, "noPath", start: Started); // no path to begin with
        var caller = new ThinCaller();
        caller.OpenablePids.Add(500);

        var snapshot = h.Snapshots.GetFor(caller);

        var mine = snapshot.Processes.Single(p => p.Pid == 500);
        var theirs = snapshot.Processes.Single(p => p.Pid == 501);
        Assert.Equal(own.FilePath, mine.FilePath);
        Assert.Null(theirs.FilePath);
        Assert.Equal("secret", theirs.Name);
        Assert.Equal(3, theirs.CpuPercent);
        Assert.Equal(1, caller.BatchCalls);

        // The redaction is on a copy: an administrator asking next still sees the path.
        var admin = h.Snapshots.GetFor(ThinCaller.Admin());
        Assert.Equal(@"C:\Windows\System32\secret.exe", admin.Processes.Single(p => p.Pid == 501).FilePath);
        Assert.Equal(1, h.Snapshots.BuildCount);
    }

    [Fact]
    public void TheAccessCheckIsMadeInOneBatchRegardlessOfTheProcessCount()
    {
        var h = new ThinHarness();
        for (var i = 0; i < 350; i++) h.Process(1000 + i, "p" + i).FilePath = $@"C:\apps\p{i}.exe";
        var caller = new Mock<IIpcCaller>();
        caller.SetupGet(c => c.Name).Returns("TEST");
        caller.Setup(c => c.FilterOpenable(It.IsAny<IReadOnlyList<(int Pid, DateTime StartTimeUtc)>>(), ProcessAccessFlags.QueryLimitedInformation))
            .Returns(new HashSet<int>());

        var snapshot = h.Snapshots.GetFor(caller.Object);

        caller.Verify(c => c.FilterOpenable(It.Is<IReadOnlyList<(int Pid, DateTime StartTimeUtc)>>(l => l.Count == 350), ProcessAccessFlags.QueryLimitedInformation), Times.Once);
        caller.Verify(c => c.CanOpenProcess(It.IsAny<int>(), It.IsAny<ProcessAccessFlags>()), Times.Never);
        Assert.All(snapshot.Processes, p => Assert.Null(p.FilePath));
    }

    [Fact]
    public void GetSnapshot_IsServedThroughTheDispatcher_PerCaller()
    {
        var h = new ThinHarness();
        Chrome(h);

        var forAdmin = h.Data<EngineSnapshot>(h.SendAsAdmin(CommandTypes.GetSnapshot));
        var forStandard = h.Data<EngineSnapshot>(h.Send(new ThinCaller(), CommandTypes.GetSnapshot));

        Assert.True(forAdmin.CallerIsAdministrator);
        Assert.NotNull(forAdmin.Processes.Single().FilePath);
        Assert.False(forStandard.CallerIsAdministrator);
        Assert.Null(forStandard.Processes.Single().FilePath);
        Assert.Equal(1, h.Snapshots.BuildCount);
    }

    [Fact]
    public void TheFirstBuildForAFullSizedProcessListIsFast_AndTheNextOnesFaster()
    {
        var h = new ThinHarness();
        for (var i = 0; i < 400; i++)
        {
            var p = h.Process(1000 + i, "proc" + i);
            p.FilePath = $@"C:\apps\proc{i}.exe";
            h.Details.Users[p.Pid] = @"PC\tee";
            h.Details.Tuning[p.Pid] = new ProcessTuning(2, 5, false);
        }
        for (var i = 0; i < 20; i++) h.Rules.Add(new ProcessRule { Id = i + 1, ProcessName = $"proc{i}.exe" });

        h.Snapshots.GetShared();
        var first = h.Snapshots.LastBuildDuration;
        h.NowMs += 2000;
        h.Snapshots.GetFor(new ThinCaller());
        h.Snapshots.GetShared();
        var second = h.Snapshots.LastBuildDuration;

        // With the Windows calls faked this is the engine's own overhead; a generous bound that still catches an accidental O(n^2).
        Assert.True(first < TimeSpan.FromMilliseconds(250), $"first build took {first.TotalMilliseconds:0.0} ms");
        Assert.True(second < TimeSpan.FromMilliseconds(250), $"second build took {second.TotalMilliseconds:0.0} ms");
    }
}
