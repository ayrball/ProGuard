using System.Diagnostics;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services.ProcessRules;

namespace ProGuard.Tests;

/// <summary>
/// ProBalance's optional restraint actions (affinity, I/O priority, efficiency mode, priority-free restraint) and its
/// idle pause, on the fake clock of <see cref="ProBalanceHarness"/>. With the default timing a hog first over quota at
/// t=0 is restrained at t=1000, and can be restored from t=5200 (4200 ms minimum restraint).
/// </summary>
public class ProBalanceRestraintActionsTests
{
    private static ProBalanceHarness Restrained(Action<ProBalanceHarness> configure, out ProcessInfo hog,
        long affinity = 0xFF, int io = 2, EfficiencyModeState eco = EfficiencyModeState.NotSet)
    {
        var h = new ProBalanceHarness();
        configure(h);
        hog = h.Add(100, cpu: 50, affinity: affinity, io: io, eco: eco);
        h.At(0);
        h.At(1000);
        return h;
    }

    /// <summary>Usage drops; restore needs both that and the minimum restraint time.</summary>
    private static void CalmDown(ProBalanceHarness h, ProcessInfo hog)
    {
        hog.CpuPercent = 0;
        h.At(5000);
        Assert.True(h.Engine.IsEngaged, "restored before the minimum restraint time");
        h.At(6000);
        Assert.False(h.Engine.IsEngaged);
    }

    // ---------- Affinity ----------

    [Fact]
    public void RestrainByAffinity_NarrowsToTheRestraintAffinity_AndRestoresIt()
    {
        using var h = Restrained(h => { h.Engine.RestrainByAffinity = true; h.Engine.RestraintAffinity = 0x0F; }, out var hog);

        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(100));
        Assert.Equal(0x0F, h.Access.Affinity[100]);

        CalmDown(h, hog);
        Assert.Equal(0xFF, h.Access.Affinity[100]);
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(100));
    }

    [Fact]
    public void RestrainByAffinity_IntersectsWithTheProcessAffinity()
    {
        using var h = Restrained(h => { h.Engine.RestrainByAffinity = true; h.Engine.RestraintAffinity = 0x0F; }, out _, affinity: 0x3C);

        Assert.Equal(0x0C, h.Access.Affinity[100]);
    }

    [Theory]
    [InlineData(0xF0L, 0x0FL)]   // no CPU in common
    [InlineData(0x03L, 0x0FL)]   // already within the restraint affinity
    [InlineData(0xFFL, 0L)]      // no restraint affinity chosen
    public void RestrainByAffinity_LeavesAffinityAlone_WhenItWouldChangeNothingOrLeaveNoCpu(long processMask, long restraintMask)
    {
        using var h = Restrained(h => { h.Engine.RestrainByAffinity = true; h.Engine.RestraintAffinity = restraintMask; }, out _, affinity: processMask);

        Assert.Equal(processMask, h.Access.Affinity[100]);
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.PriorityOf(100));
    }

    [Theory]
    [InlineData(0, 0b1010L)]
    [InlineData(1, 0b1001L)]
    [InlineData(2, 0b0011L)]
    public void DropOneRandomCore_RemovesTheChosenCpu_AndRestoresIt(int index, long expected)
    {
        using var h = Restrained(h =>
        {
            h.Engine.ProBalanceDropOneRandomCore = true;
            h.Engine.RestraintAffinity = 0x1;   // ignored: dropping a core takes precedence
            h.Engine.RandomIndex = count => { Assert.Equal(3, count); return index; };
        }, out var hog, affinity: 0b1011);

        Assert.Equal(expected, h.Access.Affinity[100]);

        CalmDown(h, hog);
        Assert.Equal(0b1011, h.Access.Affinity[100]);
    }

    [Fact]
    public void DropOneRandomCore_LeavesASingleCpuProcessAlone()
    {
        using var h = Restrained(h => h.Engine.ProBalanceDropOneRandomCore = true, out _, affinity: 0x4);

        Assert.Equal(0x4, h.Access.Affinity[100]);
        Assert.True(h.Engine.IsEngaged);
    }

    // ---------- I/O priority ----------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void LowerIOPriority_LowersToLow_AndRestoresTheOriginal(int original)
    {
        using var h = Restrained(h => h.Engine.LowerIOPriorityDuringRestraint = true, out var hog, io: original);

        Assert.Equal(1, h.Access.Io[100]);

        CalmDown(h, hog);
        Assert.Equal(original, h.Access.Io[100]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LowerIOPriority_LeavesAlreadyLowIoAlone(int original)
    {
        using var h = Restrained(h => h.Engine.LowerIOPriorityDuringRestraint = true, out var hog, io: original);

        Assert.Equal(original, h.Access.Io[100]);
        CalmDown(h, hog);
        Assert.Equal(original, h.Access.Io[100]);
    }

    // ---------- Efficiency mode ----------

    [Theory]
    [InlineData(EfficiencyModeState.NotSet)]
    [InlineData(EfficiencyModeState.Off)]
    public void UseEfficiencyMode_TurnsEcoQoSOn_AndRestoresTheOriginalState(EfficiencyModeState original)
    {
        using var h = Restrained(h => h.Engine.UseEfficiencyMode = true, out var hog, eco: original);

        Assert.Equal(EfficiencyModeState.On, h.Access.Eco[100]);

        CalmDown(h, hog);
        Assert.Equal(original, h.Access.Eco[100]);
    }

    [Fact]
    public void UseEfficiencyMode_LeavesAProcessAlreadyInEfficiencyModeAlone()
    {
        using var h = Restrained(h => h.Engine.UseEfficiencyMode = true, out var hog, eco: EfficiencyModeState.On);

        CalmDown(h, hog);
        Assert.Equal(EfficiencyModeState.On, h.Access.Eco[100]);
    }

    // ---------- DoNotLowerPriorityClass ----------

    [Fact]
    public void DoNotLowerPriorityClass_RestrainsOnlyThroughTheOtherActions_WithTheSameTiming()
    {
        using var h = new ProBalanceHarness();
        h.Engine.DoNotLowerPriorityClass = true;
        h.Engine.LowerIOPriorityDuringRestraint = true;
        h.Engine.UseEfficiencyMode = true;
        var hog = h.Add(100, cpu: 50);

        h.At(0);
        h.At(800);
        Assert.False(h.Engine.IsEngaged);   // 900 ms over quota not reached yet
        h.At(1000);

        Assert.True(h.Engine.IsEngaged);
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(100));
        Assert.Equal(1, h.Access.Io[100]);
        Assert.Equal(EfficiencyModeState.On, h.Access.Eco[100]);

        CalmDown(h, hog);
        Assert.Equal(2, h.Access.Io[100]);
        Assert.Equal(EfficiencyModeState.NotSet, h.Access.Eco[100]);
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(100));
    }

    [Fact]
    public void DoNotLowerPriorityClass_WithNoOtherAction_RestrainsNothing()
    {
        using var h = Restrained(h => h.Engine.DoNotLowerPriorityClass = true, out _);

        Assert.False(h.Engine.IsEngaged);
        Assert.Equal(0, h.Access.SetCalls);
    }

    [Fact]
    public void DoNotLowerPriorityClass_StillHonoursTameOnlyNormal()
    {
        using var h = new ProBalanceHarness();
        h.Engine.DoNotLowerPriorityClass = true;
        h.Engine.UseEfficiencyMode = true;
        h.Add(100, cpu: 50, priority: ProcessPriorityClass.High);

        h.At(0);
        h.At(1000);

        Assert.False(h.Engine.IsEngaged);
        Assert.Equal(EfficiencyModeState.NotSet, h.Access.Eco[100]);
    }

    [Fact]
    public void DoNotLowerPriorityClass_RestraintAndRestore_AreLoggedWithTheUnchangedPriority()
    {
        using var h = Restrained(h => { h.Engine.DoNotLowerPriorityClass = true; h.Engine.UseEfficiencyMode = true; }, out var hog);
        CalmDown(h, hog);

        h.Store.Verify(s => s.LogProBalanceActionAsync(It.Is<ProBalanceAction>(a => !a.IsRestored && a.Pid == 100
            && a.OriginalPriority == (int)ProcessPriorityClass.Normal && a.NewPriority == (int)ProcessPriorityClass.Normal
            && a.Reason.Contains("efficiency mode"))), Times.Once);
        h.Store.Verify(s => s.LogProBalanceActionAsync(It.Is<ProBalanceAction>(a => a.IsRestored && a.Pid == 100)), Times.Once);
    }

    // ---------- Changes made by someone else ----------

    [Fact]
    public void ExternallyChangedSetting_IsNotRestored_ButTheOthersAre()
    {
        using var h = Restrained(h =>
        {
            h.Engine.RestrainByAffinity = true;
            h.Engine.RestraintAffinity = 0x0F;
            h.Engine.LowerIOPriorityDuringRestraint = true;
            h.Engine.UseEfficiencyMode = true;
        }, out var hog);

        h.Access.Affinity[100] = 0x30;                     // the user picks other CPUs
        h.At(2000);
        Assert.True(h.Engine.IsEngaged);
        h.Access.Eco[100] = EfficiencyModeState.Off;       // and later turns efficiency mode off

        CalmDown(h, hog);
        Assert.Equal(0x30, h.Access.Affinity[100]);
        Assert.Equal(EfficiencyModeState.Off, h.Access.Eco[100]);
        Assert.Equal(2, h.Access.Io[100]);
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(100));
    }

    [Fact]
    public void ExternalPriorityChange_EndsTheRestraint_AndPutsBackWhatIsStillOurs()
    {
        using var h = Restrained(h => { h.Engine.LowerIOPriorityDuringRestraint = true; h.Engine.UseEfficiencyMode = true; }, out _);

        h.Access.Priority[100] = ProcessPriorityClass.High;
        h.Access.Io[100] = 0;
        h.At(2000);

        Assert.False(h.Engine.IsEngaged);
        Assert.Equal(ProcessPriorityClass.High, h.PriorityOf(100));
        Assert.Equal(0, h.Access.Io[100]);
        Assert.Equal(EfficiencyModeState.NotSet, h.Access.Eco[100]);
    }

    [Fact]
    public void DoNotLowerPriorityClass_RestraintEnds_WhenEverythingItChangedWasChangedExternally()
    {
        using var h = Restrained(h => { h.Engine.DoNotLowerPriorityClass = true; h.Engine.LowerIOPriorityDuringRestraint = true; }, out _);
        Assert.True(h.Engine.IsEngaged);
        var sets = h.Access.SetCalls;

        h.Access.Io[100] = 3;
        h.At(2000);

        Assert.False(h.Engine.IsEngaged);
        Assert.Equal(3, h.Access.Io[100]);
        Assert.Equal(sets, h.Access.SetCalls);
    }

    [Fact]
    public void ExitedProcess_WithOptionalActions_IsForgottenWithoutWritingItsPid()
    {
        using var h = Restrained(h => { h.Engine.ProBalanceDropOneRandomCore = true; h.Engine.UseEfficiencyMode = true; }, out _);
        var sets = h.Access.SetCalls;

        h.Exit(100);
        h.At(2000);

        Assert.False(h.Engine.IsEngaged);
        Assert.Equal(sets, h.Access.SetCalls);
    }

    [Fact]
    public void Disabling_RestoresTheOptionalActionsToo()
    {
        using var h = Restrained(h => { h.Engine.LowerIOPriorityDuringRestraint = true; h.Engine.ProBalanceDropOneRandomCore = true; }, out _);

        h.Engine.SetEnabled(false);

        Assert.Equal(2, h.Access.Io[100]);
        Assert.Equal(0xFF, h.Access.Affinity[100]);
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(100));
    }

    [Fact]
    public void ConfigureRestraint_SetsEveryOption()
    {
        using var h = new ProBalanceHarness();
        h.Engine.ConfigureRestraint(new ProBalanceRestraintOptions
        {
            RestrainByAffinity = true, RestraintAffinity = 0x5, ProBalanceDropOneRandomCore = true,
            LowerIOPriorityDuringRestraint = true, UseEfficiencyMode = true, DoNotLowerPriorityClass = true,
            DisableProBalanceWhenSysIdle = true, DisableProBalanceIfSysIdleThisManyMS = 12345
        });

        Assert.True(h.Engine.RestrainByAffinity);
        Assert.Equal(0x5, h.Engine.RestraintAffinity);
        Assert.True(h.Engine.ProBalanceDropOneRandomCore);
        Assert.True(h.Engine.LowerIOPriorityDuringRestraint);
        Assert.True(h.Engine.UseEfficiencyMode);
        Assert.True(h.Engine.DoNotLowerPriorityClass);
        Assert.True(h.Engine.DisableProBalanceWhenSysIdle);
        Assert.Equal(12345, h.Engine.DisableProBalanceIfSysIdleThisManyMS);
    }

    // ---------- Idle pause ----------

    [Fact]
    public void SysIdle_PreventsRestraint_AndLiftsRestraintsInPlace()
    {
        using var h = Restrained(h => h.Engine.DisableProBalanceWhenSysIdle = true, out _);
        Assert.True(h.Engine.IsEngaged);

        h.IdleSource.Idle = TimeSpan.FromMilliseconds(30000);
        h.At(2000);

        Assert.True(h.Engine.IsPausedForIdle);
        Assert.False(h.Engine.IsEngaged);
        Assert.Equal(ProcessPriorityClass.Normal, h.PriorityOf(100));

        for (var t = 3000; t <= 10_000; t += 1000) h.At(t);
        Assert.False(h.Engine.IsEngaged);
    }

    [Fact]
    public void SysIdle_ResumesOnInput_AndTheOverQuotaRunStartsAfresh()
    {
        using var h = new ProBalanceHarness();
        h.Engine.DisableProBalanceWhenSysIdle = true;
        h.IdleSource.Idle = TimeSpan.FromMinutes(5);
        h.Add(100, cpu: 50);
        for (var t = 0; t <= 5000; t += 1000) h.At(t);
        Assert.False(h.Engine.IsEngaged);

        h.IdleSource.Idle = TimeSpan.FromMilliseconds(200);
        h.At(6000);
        Assert.False(h.Engine.IsPausedForIdle);
        Assert.False(h.Engine.IsEngaged);   // first sample over quota since the pause
        h.At(7000);
        Assert.True(h.Engine.IsEngaged);
    }

    [Theory]
    [InlineData(false, 60_000.0)]   // option off: idle time ignored
    [InlineData(true, 29_999.0)]    // below the threshold
    [InlineData(true, null)]        // no UI reporting: nobody knows, so not idle
    public void SysIdle_DoesNotPause_UnlessEnabledAndIdleLongEnough(bool enabled, double? idleMs)
    {
        using var h = new ProBalanceHarness();
        h.Engine.DisableProBalanceWhenSysIdle = enabled;
        h.IdleSource.Idle = idleMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
        h.Add(100, cpu: 50);

        h.At(0);
        h.At(1000);

        Assert.False(h.Engine.IsPausedForIdle);
        Assert.True(h.Engine.IsEngaged);
    }

    [Fact]
    public void SysIdle_ThresholdIsConfigurable()
    {
        using var h = new ProBalanceHarness();
        h.Engine.DisableProBalanceWhenSysIdle = true;
        h.Engine.DisableProBalanceIfSysIdleThisManyMS = 5000;
        h.IdleSource.Idle = TimeSpan.FromSeconds(6);
        h.Add(100, cpu: 50);

        h.At(0);
        h.At(1000);

        Assert.True(h.Engine.IsPausedForIdle);
        Assert.False(h.Engine.IsEngaged);
    }
}
