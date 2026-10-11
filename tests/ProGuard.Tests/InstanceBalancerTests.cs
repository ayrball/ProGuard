using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Models;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

public class InstanceBalancerTests
{
    private const long AllCores = 0xFF;
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeProcessMonitor _monitor = new();
    private readonly FakeRuleProcessAccess _access = new();
    private readonly List<ProcessRule> _rules = new();
    private readonly InstanceBalancer _balancer;
    private readonly ProcessRule _rule = new() { ProcessName = "worker.exe", InstanceBalancerMode = InstanceBalancerMode.EqualCoresPerInstance };

    public InstanceBalancerTests()
    {
        _rules.Add(_rule);
        _balancer = new InstanceBalancer(NullLogger<InstanceBalancer>.Instance, _monitor, FakeRules.Store(_rules).Object,
            _access, FakeRules.EightThreadsFourCores, new ManualTimeProvider());
    }

    private void Start(int pid, double cpu = 0, long affinity = AllCores)
    {
        _access.Affinity[pid] = affinity;
        _access.Start[pid] = T0.AddSeconds(pid);
        _balancer.OnProcessStarted(_monitor.Add(pid, "worker", cpu), _rule);
    }

    private void Exit(int pid)
    {
        _monitor.Exit(pid);
        _access.Affinity.Remove(pid);
        _balancer.OnProcessExited(pid);
    }

    private long MaskOf(int pid) => _access.Affinity[pid];

    // === PlanMasks ===

    private static IReadOnlyList<long> Plan(InstanceBalancerMode mode, int instances, int coresEach = 1, bool avoidSmt = false, int reserved = 0) =>
        InstanceBalancer.PlanMasks(FakeRules.EightThreadsFourCores(), mode, instances, coresEach, avoidSmt, reserved);

    [Fact]
    public void Equal_SplitsTheCoresEvenly()
    {
        Assert.Equal(new long[] { 0xFF }, Plan(InstanceBalancerMode.EqualCoresPerInstance, 1));
        Assert.Equal(new long[] { 0x0F, 0xF0 }, Plan(InstanceBalancerMode.EqualCoresPerInstance, 2));
        Assert.Equal(new long[] { 0x03, 0x0C, 0x30, 0xC0 }, Plan(InstanceBalancerMode.EqualCoresPerInstance, 4));
    }

    [Fact]
    public void Equal_UnevenSplit_GivesTheOldestInstancesTheExtraCores()
    {
        Assert.Equal(new long[] { 0x07, 0x38, 0xC0 }, Plan(InstanceBalancerMode.EqualCoresPerInstance, 3));
    }

    [Fact]
    public void Equal_MoreInstancesThanCores_ShareCoresRoundRobin()
    {
        var masks = Plan(InstanceBalancerMode.EqualCoresPerInstance, 10);
        Assert.Equal(new long[] { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x01, 0x02 }, masks);
    }

    [Fact]
    public void Fixed_GivesEachInstanceNCores_WrappingRound()
    {
        Assert.Equal(new long[] { 0x03, 0x0C, 0x30 }, Plan(InstanceBalancerMode.FixedCoresPerInstance, 3, coresEach: 2));
        Assert.Equal(new long[] { 0x03, 0x0C, 0x30, 0xC0, 0x03 }, Plan(InstanceBalancerMode.FixedCoresPerInstance, 5, coresEach: 2));
        Assert.Equal(new long[] { 0x07, 0x38, 0xC1 }, Plan(InstanceBalancerMode.FixedCoresPerInstance, 3, coresEach: 3));
    }

    [Fact]
    public void Fixed_MoreCoresThanExist_GivesEveryoneEverything()
    {
        Assert.Equal(new long[] { 0xFF, 0xFF }, Plan(InstanceBalancerMode.FixedCoresPerInstance, 2, coresEach: 64));
    }

    [Fact]
    public void AvoidSmt_UsesOnlyTheFirstThreadOfEachCore()
    {
        Assert.Equal(new long[] { 0x05, 0x50 }, Plan(InstanceBalancerMode.EqualCoresPerInstance, 2, avoidSmt: true));
        Assert.Equal(new long[] { 0x01, 0x04, 0x10 }, Plan(InstanceBalancerMode.FixedCoresPerInstance, 3, avoidSmt: true));
    }

    [Fact]
    public void ReservedCores_AreTheLowestAndNeverHandedOut()
    {
        Assert.Equal(new long[] { 0x1E, 0xE0 }, Plan(InstanceBalancerMode.EqualCoresPerInstance, 2, reserved: 1));
        Assert.Equal(new long[] { 0x50 }, Plan(InstanceBalancerMode.EqualCoresPerInstance, 1, avoidSmt: true, reserved: 2));
        Assert.Equal(new long[] { 0x0C, 0x30 }, Plan(InstanceBalancerMode.FixedCoresPerInstance, 2, coresEach: 2, reserved: 2));
    }

    [Fact]
    public void ReservingEveryCore_StillLeavesOne()
    {
        Assert.Equal(new long[] { 0x80, 0x80 }, Plan(InstanceBalancerMode.EqualCoresPerInstance, 2, reserved: 100));
    }

    // === Rebalancing as instances come and go ===

    [Fact]
    public void Equal_RebalancesAsInstancesStartAndExit()
    {
        Start(1);
        Assert.Equal(0xFF, MaskOf(1));

        Start(2);
        Assert.Equal(0x0F, MaskOf(1));
        Assert.Equal(0xF0, MaskOf(2));

        Start(3);
        Assert.Equal(0x07, MaskOf(1));
        Assert.Equal(0x38, MaskOf(2));
        Assert.Equal(0xC0, MaskOf(3));

        Exit(2);
        Assert.Equal(0x0F, MaskOf(1));
        Assert.Equal(0xF0, MaskOf(3));

        Exit(1);
        Assert.Equal(0xFF, MaskOf(3));
    }

    [Fact]
    public void Fixed_RebalancesAsInstancesStartAndExit()
    {
        _rule.InstanceBalancerMode = InstanceBalancerMode.FixedCoresPerInstance;
        _rule.InstanceBalancerCoresPerInstance = 2;

        Start(1);
        Start(2);
        Start(3);
        Assert.Equal(0x03, MaskOf(1));
        Assert.Equal(0x0C, MaskOf(2));
        Assert.Equal(0x30, MaskOf(3));

        Exit(1);
        Assert.Equal(0x03, MaskOf(2));
        Assert.Equal(0x0C, MaskOf(3));

        Start(4);
        Assert.Equal(0x30, MaskOf(4));
    }

    [Fact]
    public void OnlyChangedMasksAreWritten()
    {
        _rule.InstanceBalancerMode = InstanceBalancerMode.FixedCoresPerInstance;
        _rule.InstanceBalancerCoresPerInstance = 2;
        Start(1);
        Start(2);
        _access.AffinityWrites.Clear();

        Start(3);
        _balancer.Tick();

        Assert.Equal(new[] { (3, 0x30L) }, _access.AffinityWrites);
    }

    [Fact]
    public void MinCpu_InstancesJoinOnceTheyReachIt()
    {
        _rule.InstanceBalancerMinCpuPercent = 10;
        Start(1, cpu: 50);
        Start(2, cpu: 1, affinity: 0x0F);

        Assert.Equal(0xFF, MaskOf(1));
        Assert.Equal(0x0F, MaskOf(2)); // below the minimum: left alone
        Assert.Null(_balancer.GetAppliedMask(2));

        _monitor.GetProcess(2)!.CpuPercent = 12;
        _balancer.Tick();
        Assert.Equal(0x0F, MaskOf(1));
        Assert.Equal(0xF0, MaskOf(2));

        // Once in, an instance stays in even if it goes quiet, so masks don't flap.
        _monitor.GetProcess(2)!.CpuPercent = 0;
        _balancer.Tick();
        Assert.Equal(0xF0, MaskOf(2));
    }

    [Fact]
    public void AffinityChangedElsewhere_ThatInstanceIsLetGo()
    {
        Start(1);
        Start(2);
        _access.Affinity[1] = 0x01; // the user (or another rule) set it

        _balancer.Tick();

        Assert.Equal(0x01, MaskOf(1));
        Assert.Equal(0xFF, MaskOf(2));
        Assert.Null(_balancer.GetAppliedMask(1));
    }

    [Fact]
    public void RuleTurnedOff_RestoresOriginalAffinity()
    {
        Start(1, affinity: 0x7F);
        Start(2, affinity: 0xFE);

        _rule.InstanceBalancerMode = null;
        _balancer.Tick();

        Assert.Equal(0x7F, MaskOf(1));
        Assert.Equal(0xFE, MaskOf(2));
    }

    [Fact]
    public void Stop_RestoresOriginalAffinity()
    {
        Start(1);
        Start(2);

        _balancer.Stop();

        Assert.Equal(AllCores, MaskOf(1));
        Assert.Equal(AllCores, MaskOf(2));
    }

    [Fact]
    public void Tick_PicksUpInstancesAlreadyRunningWhenTheRuleWasEnabled()
    {
        _access.Affinity[1] = AllCores;
        _access.Start[1] = T0;
        _access.Affinity[2] = AllCores;
        _access.Start[2] = T0.AddSeconds(1);
        _monitor.Add(1, "worker");
        _monitor.Add(2, "worker");
        _monitor.Add(3, "unrelated");

        _balancer.Tick();

        Assert.Equal(0x0F, MaskOf(1));
        Assert.Equal(0xF0, MaskOf(2));
    }

    [Fact]
    public void RuleWithoutBalancer_IsIgnored()
    {
        _rule.InstanceBalancerMode = null;
        Start(1);
        Start(2);

        Assert.Equal(AllCores, MaskOf(1));
        Assert.Empty(_access.AffinityWrites);
    }
}
