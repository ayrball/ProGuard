using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;

namespace ProGuard.Tests;

/// <summary>In-memory processes for ProBalance: priorities, affinities, I/O priorities, efficiency mode, parents and
/// sessions without touching real PIDs.</summary>
internal sealed class FakeProcessAccess : IProcessPriorityAccess
{
    public Dictionary<int, ProcessPriorityClass> Priority { get; } = new();
    public Dictionary<int, int> Parent { get; } = new();
    public Dictionary<int, int> Session { get; } = new();
    public Dictionary<int, long> Affinity { get; } = new();
    public Dictionary<int, int> Io { get; } = new();
    public Dictionary<int, EfficiencyModeState> Eco { get; } = new();
    public int SetCalls { get; private set; }

    public ProcessPriorityClass? GetPriorityClass(int pid) => Priority.TryGetValue(pid, out var p) ? p : null;

    public bool SetPriorityClass(int pid, ProcessPriorityClass priorityClass)
    {
        if (!Priority.ContainsKey(pid)) return false;
        SetCalls++;
        Priority[pid] = priorityClass;
        return true;
    }

    public int? GetParentPid(int pid) => Parent.TryGetValue(pid, out var p) ? p : null;
    public int? GetSessionId(int pid) => Session.TryGetValue(pid, out var s) ? s : null;

    public long? GetAffinityMask(int pid) => Affinity.TryGetValue(pid, out var m) ? m : null;
    public bool SetAffinityMask(int pid, long mask) => Set(Affinity, pid, mask);
    public int? GetIoPriority(int pid) => Io.TryGetValue(pid, out var io) ? io : null;
    public bool SetIoPriority(int pid, int priority) => Set(Io, pid, priority);
    public EfficiencyModeState? GetEfficiencyMode(int pid) => Eco.TryGetValue(pid, out var e) ? e : null;
    public bool SetEfficiencyMode(int pid, EfficiencyModeState state) => Set(Eco, pid, state);

    private bool Set<T>(Dictionary<int, T> values, int pid, T value)
    {
        if (!values.ContainsKey(pid)) return false;
        SetCalls++;
        values[pid] = value;
        return true;
    }
}

/// <summary>Foreground known unless a test says otherwise; <see cref="Windowed"/> lists the programs that could be in use.</summary>
internal sealed class FakeForegroundFallback : IForegroundFallback
{
    public bool IsForegroundKnown { get; set; } = true;
    public HashSet<int> Windowed { get; } = new();
    public int Asked { get; private set; }

    public bool MightBeForeground(int pid, string? imagePath)
    {
        Asked++;
        return Windowed.Contains(pid);
    }
}

internal sealed class FakeIdleSource : IUserIdleSource
{
    public TimeSpan? Idle { get; set; }
    public TimeSpan? GetIdleTime() => Idle;
}

/// <summary>
/// Drives a <see cref="ProBalanceEngine"/> on a fake clock. <see cref="At"/> moves the clock to an absolute time
/// and runs one evaluation, the way the 1 s poll would.
/// </summary>
internal sealed class ProBalanceHarness : IDisposable
{
    public long Now { get; set; }
    public double SystemCpu { get; set; } = 50;
    public int ForegroundPid { get; set; }
    public List<ProcessInfo> Processes { get; } = new();
    public FakeProcessAccess Access { get; } = new();
    public FakeIdleSource IdleSource { get; } = new();
    public FakeForegroundFallback Fallback { get; } = new();
    public Mock<IRuleStore> Store { get; } = new();
    public Mock<IProcessMonitor> Monitor { get; } = new();
    public ProBalanceEngine Engine { get; }

    public ProBalanceHarness()
    {
        var monitor = Monitor;
        monitor.Setup(m => m.GetAllProcesses()).Returns(() => Processes.ToList());
        monitor.Setup(m => m.GetProcess(It.IsAny<int>())).Returns((int pid) => Processes.FirstOrDefault(p => p.Pid == pid));
        monitor.Setup(m => m.GetForegroundPid()).Returns(() => ForegroundPid);
        Engine = new ProBalanceEngine(NullLogger<ProBalanceEngine>.Instance, monitor.Object, Store.Object,
            () => SystemCpu, Access, () => Now, IdleSource, Fallback);
    }

    public ProcessInfo Add(int pid, double cpu, ProcessPriorityClass priority = ProcessPriorityClass.Normal,
        int session = 1, int? parent = null, string name = "hog", long affinity = 0xFF, int io = 2,
        EfficiencyModeState eco = EfficiencyModeState.NotSet)
    {
        var info = new ProcessInfo { Pid = pid, Name = name, CpuPercent = cpu, ParentPid = parent };
        Processes.Add(info);
        Access.Priority[pid] = priority;
        Access.Session[pid] = session;
        Access.Affinity[pid] = affinity;
        Access.Io[pid] = io;
        Access.Eco[pid] = eco;
        return info;
    }

    public void Exit(int pid)
    {
        Processes.RemoveAll(p => p.Pid == pid);
        Access.Priority.Remove(pid);
        Access.Affinity.Remove(pid);
        Access.Io.Remove(pid);
        Access.Eco.Remove(pid);
    }

    public ProcessPriorityClass PriorityOf(int pid) => Access.Priority[pid];

    public void At(long ms)
    {
        Now = ms;
        Engine.Evaluate(null);
    }

    public void Dispose() => Engine.Dispose();
}

public class ProBalanceTests
{
    private static ProBalanceEngine NewEngine() =>
        new(new Mock<ILogger<ProBalanceEngine>>().Object, new Mock<IProcessMonitor>().Object, new Mock<IRuleStore>().Object);

    [Fact]
    public void Defaults_MatchProcessLassoIni()
    {
        using var engine = NewEngine();

        Assert.Equal(10.0, engine.TotalProcessorUsageBeforeRestraint);
        Assert.Equal(5.0, engine.PerProcessUsageBeforeRestraint);
        Assert.Equal(900, engine.TimeOverQuotaBeforeRestraint);
        Assert.Equal(2.0, engine.PerProcessUsageForRestore);
        Assert.Equal(4200, engine.MinimumTimeOfRestraint);
        Assert.Equal(0, engine.MaximumTimeOfRestraint);
        Assert.True(engine.TameOnlyNormal);
        Assert.False(engine.LowerToIdleInsteadOfBelowNormal);
        Assert.True(engine.ExcludeForegroundProcesses);
        Assert.True(engine.ExcludeChildrenOfForeground);
        Assert.True(engine.ExcludeServices);
        Assert.False(engine.RestrainByAffinity);
        Assert.Equal(0, engine.RestraintAffinity);
        Assert.False(engine.ProBalanceDropOneRandomCore);
        Assert.False(engine.LowerIOPriorityDuringRestraint);
        Assert.False(engine.UseEfficiencyMode);
        Assert.False(engine.DoNotLowerPriorityClass);
        Assert.False(engine.DisableProBalanceWhenSysIdle);
        Assert.Equal(30000, engine.DisableProBalanceIfSysIdleThisManyMS);
    }

    [Fact]
    public void RestraintOptionDefaults_AreAllOff_InOptionsAndSettings()
    {
        Assert.Equal(new ProBalanceRestraintOptions(), ProBalanceRestraintOptions.FromSettings(new AppSettings()));
        var o = new ProBalanceRestraintOptions();
        Assert.False(o.RestrainByAffinity || o.ProBalanceDropOneRandomCore || o.LowerIOPriorityDuringRestraint
                     || o.UseEfficiencyMode || o.DoNotLowerPriorityClass || o.DisableProBalanceWhenSysIdle);
        Assert.Equal(30000, o.DisableProBalanceIfSysIdleThisManyMS);
    }

    [Fact]
    public void Defaults_TouchOnlyThePriorityClass()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50);

        h.At(0);
        h.At(1000);

        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(100));
        Assert.Equal(0xFF, h.Access.Affinity[100]);
        Assert.Equal(2, h.Access.Io[100]);
        Assert.Equal(EfficiencyModeState.NotSet, h.Access.Eco[100]);
    }

    [Fact]
    public void SetSensitivity_Level5_IsProcessLassoDefaults()
    {
        using var engine = NewEngine();
        engine.SetSensitivity(1);
        engine.SetSensitivity(5);

        Assert.Equal(10.0, engine.TotalProcessorUsageBeforeRestraint);
        Assert.Equal(5.0, engine.PerProcessUsageBeforeRestraint);
        Assert.Equal(900, engine.TimeOverQuotaBeforeRestraint);
        Assert.Equal(2.0, engine.PerProcessUsageForRestore);
    }

    [Fact]
    public void Engine_StartsNotEngaged()
    {
        using var engine = NewEngine();

        Assert.False(engine.IsEngaged);
        Assert.Equal(0, engine.ActiveInterventions);
    }

    [Fact]
    public void ExcludeProcess_ExemptsByName_AndRemoveExclusionLiftsIt()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50, name: "game");
        h.Engine.TimeOverQuotaBeforeRestraint = 0;

        h.Engine.ExcludeProcess("game.exe");
        h.At(0);
        Assert.False(h.Engine.IsEngaged);

        h.Engine.RemoveExclusion("GAME.exe");
        h.At(1000);
        Assert.True(h.Engine.IsEngaged);
    }

    [Fact]
    public void RuleExcludeFromProBalance_IsRespected()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50, name: "render");
        h.Store.Setup(s => s.GetProcessRule("render")).Returns(new ProcessRule { ExcludeFromProBalance = true });
        h.Engine.TimeOverQuotaBeforeRestraint = 0;

        h.At(0);
        h.At(1000);

        Assert.False(h.Engine.IsEngaged);
    }

    // ---------- Who may be restrained ----------

    [Theory]
    [InlineData(ProcessPriorityClass.AboveNormal)]
    [InlineData(ProcessPriorityClass.High)]
    [InlineData(ProcessPriorityClass.RealTime)]
    [InlineData(ProcessPriorityClass.BelowNormal)]
    [InlineData(ProcessPriorityClass.Idle)]
    public void TameOnlyNormal_LeavesNonNormalProcessesAlone(ProcessPriorityClass priority)
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50, priority: priority);

        h.At(0);
        h.At(1000);

        Assert.False(h.Engine.IsEngaged);
        Assert.Equal(priority, h.PriorityOf(100));
        Assert.Equal(0, h.Access.SetCalls);
    }

    [Theory]
    [InlineData(ProcessPriorityClass.AboveNormal, true)]
    [InlineData(ProcessPriorityClass.High, true)]
    [InlineData(ProcessPriorityClass.BelowNormal, false)]
    [InlineData(ProcessPriorityClass.Idle, false)]
    public void TameOnlyNormalOff_RestrainsAnythingAboveTheTarget(ProcessPriorityClass priority, bool restrained)
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50, priority: priority);
        h.Engine.TameOnlyNormal = false;

        h.At(0);
        h.At(1000);

        Assert.Equal(restrained, h.Engine.IsEngaged);
    }

    [Fact]
    public void LowerToIdleInsteadOfBelowNormal_RestrainsToIdle()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50);
        h.Engine.LowerToIdleInsteadOfBelowNormal = true;

        h.At(0);
        h.At(1000);

        Assert.Equal(ProcessPriorityClass.Idle, h.PriorityOf(100));
    }

    [Fact]
    public void ForegroundProcess_IsNeverRestrained()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 90);
        h.ForegroundPid = 100;

        for (var t = 0; t <= 10_000; t += 1000) h.At(t);

        Assert.False(h.Engine.IsEngaged);
    }

    [Fact]
    public void ForegroundFlagFromMonitor_IsHonouredWhenForegroundPidUnknown()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 90).IsForeground = true;

        for (var t = 0; t <= 5000; t += 1000) h.At(t);

        Assert.False(h.Engine.IsEngaged);
    }

    [Fact]
    public void ChildOfForeground_IsExempt_ByParentPidFromMonitorOrLookup()
    {
        using var h = new ProBalanceHarness();
        h.Add(10, cpu: 1);                        // foreground app
        h.Add(100, cpu: 50, parent: 10);          // parent known from the snapshot
        h.Add(101, cpu: 50);                      // parent only known via lookup
        h.Access.Parent[101] = 10;
        h.Add(102, cpu: 50, parent: 999);         // unrelated
        h.ForegroundPid = 10;

        h.At(0);
        h.At(1000);

        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(100));
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(101));
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(102));
    }

    [Fact]
    public void ChildOfForeground_IsRestrained_WhenExclusionOff()
    {
        using var h = new ProBalanceHarness();
        h.Add(10, cpu: 1);
        h.Add(100, cpu: 50, parent: 10);
        h.ForegroundPid = 10;
        h.Engine.ExcludeChildrenOfForeground = false;

        h.At(0);
        h.At(1000);

        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(100));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Session0Services_AreExemptUnlessExcludeServicesOff(bool excludeServices, bool restrained)
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50, session: 0);
        h.Engine.ExcludeServices = excludeServices;

        h.At(0);
        h.At(1000);

        Assert.Equal(restrained, h.Engine.IsEngaged);
    }

    // ---------- Ending restraint ----------

    [Fact]
    public void ReleaseRestraint_RestoresImmediately()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50);
        h.At(0);
        h.At(1000);
        Assert.True(h.Engine.IsEngaged);

        Assert.True(h.Engine.ReleaseRestraint(100));
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(100));
        Assert.False(h.Engine.IsEngaged);
        Assert.False(h.Engine.ReleaseRestraint(100));
    }

    [Fact]
    public void UserChangeDuringRestraint_IsNotOverwritten()
    {
        using var h = new ProBalanceHarness();
        var hog = h.Add(100, cpu: 50);
        h.At(0);
        h.At(1000);
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(100));

        h.Access.Priority[100] = ProcessPriorityClass.High;  // user sets High in Task Manager
        hog.CpuPercent = 0;
        h.At(10_000);

        Assert.Equal(ProcessPriorityClass.High, h.PriorityOf(100));
        Assert.False(h.Engine.IsEngaged);
    }

    [Fact]
    public void UserChangeDuringRestraint_IsNotOverwritten_WhenDisabled()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50);
        h.At(0);
        h.At(1000);

        h.Access.Priority[100] = ProcessPriorityClass.Idle;
        h.Engine.SetEnabled(false);

        Assert.Equal(ProcessPriorityClass.Idle, h.PriorityOf(100));
    }

    [Fact]
    public void Disabling_RestoresEverything()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50);
        h.Add(101, cpu: 50);
        h.At(0);
        h.At(1000);
        Assert.Equal(2, h.Engine.ActiveInterventions);

        h.Engine.SetEnabled(false);

        Assert.False(h.Engine.IsEngaged);
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(100));
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(101));

        h.At(2000); // no evaluation while disabled
        Assert.False(h.Engine.IsEngaged);
    }

    [Fact]
    public void ExitedProcess_IsForgottenWithoutWritingItsPid()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50);
        h.At(0);
        h.At(1000);
        var setsBefore = h.Access.SetCalls;

        h.Exit(100);
        h.At(2000);

        Assert.False(h.Engine.IsEngaged);
        Assert.Equal(setsBefore, h.Access.SetCalls);
    }

    [Fact]
    public async Task EvaluatesAfterEachProcessRefresh_OnlyWhileStarted()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 50);
        void Refresh(long ms) { h.Now = ms; h.Monitor.Raise(m => m.Refreshed += null, EventArgs.Empty); }

        Refresh(0);
        Refresh(1000);
        Assert.False(h.Engine.IsEngaged); // not started: refreshes are ignored

        await h.Engine.StartAsync(default);
        Refresh(2000);
        Refresh(3000);
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(100));

        await h.Engine.StopAsync(default);
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(100));
        Refresh(4000);
        Refresh(5000);
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(100));
    }

    [Fact]
    public void RestraintAndRestore_AreBothLogged()
    {
        using var h = new ProBalanceHarness();
        var hog = h.Add(100, cpu: 50);
        h.At(0);
        h.At(1000);
        hog.CpuPercent = 0;
        h.At(6000);

        h.Store.Verify(s => s.LogProBalanceActionAsync(It.Is<ProBalanceAction>(a => !a.IsRestored && a.Pid == 100
            && a.NewPriority == (int)ProcessPriorityClass.BelowNormal)), Times.Once);
        h.Store.Verify(s => s.LogProBalanceActionAsync(It.Is<ProBalanceAction>(a => a.IsRestored && a.Pid == 100
            && a.NewPriority == (int)ProcessPriorityClass.Normal)), Times.Once);
    }
}
