using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

/// <summary>
/// Timeline simulations of ProBalance's timing and hysteresis rules on a fake clock, using Process Lasso's
/// defaults unless a test says otherwise: system >= 10 %, process >= 5 % for 900 ms, restore at <= 2 % after
/// >= 4200 ms restrained.
/// </summary>
public class Pass3_ProBalance_Simulation_Tests
{
    private const ProcessPriorityClass Restrained = ProcessPriorityClass.BelowNormal;
    private const ProcessPriorityClass Normal = ProcessPriorityClass.Normal;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void AllSensitivityLevels_KeepAValidHysteresisBand(int sensitivity)
    {
        using var engine = new ProBalanceEngine(new Mock<ILogger<ProBalanceEngine>>().Object,
            new Mock<IProcessMonitor>().Object, new Mock<IRuleStore>().Object);
        engine.SetSensitivity(sensitivity);

        Assert.True(engine.PerProcessUsageForRestore < engine.PerProcessUsageBeforeRestraint,
            $"At sensitivity {sensitivity}, restore ({engine.PerProcessUsageForRestore}) must be below restrain ({engine.PerProcessUsageBeforeRestraint})");
        Assert.True(engine.PerProcessUsageForRestore > 0);
        Assert.True(engine.TotalProcessorUsageBeforeRestraint > 0);
        Assert.True(engine.TimeOverQuotaBeforeRestraint > 0);
    }

    [Fact]
    public void HigherSensitivity_RestrainsEarlier()
    {
        using var low = new ProBalanceEngine(new Mock<ILogger<ProBalanceEngine>>().Object,
            new Mock<IProcessMonitor>().Object, new Mock<IRuleStore>().Object);
        using var high = new ProBalanceEngine(new Mock<ILogger<ProBalanceEngine>>().Object,
            new Mock<IProcessMonitor>().Object, new Mock<IRuleStore>().Object);
        low.SetSensitivity(2);
        high.SetSensitivity(9);

        Assert.True(high.TotalProcessorUsageBeforeRestraint < low.TotalProcessorUsageBeforeRestraint);
        Assert.True(high.PerProcessUsageBeforeRestraint < low.PerProcessUsageBeforeRestraint);
        Assert.True(high.TimeOverQuotaBeforeRestraint < low.TimeOverQuotaBeforeRestraint);
    }

    // ---------- Restraint: time over quota ----------

    [Fact]
    public void Restrains_OnlyAfter900msContinuouslyOverQuota()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 30);

        h.At(0);
        Assert.Equal(Normal, h.PriorityOf(100));
        h.At(500);
        Assert.Equal(Normal, h.PriorityOf(100));
        h.At(899);
        Assert.Equal(Normal, h.PriorityOf(100));
        h.At(900);
        Assert.Equal(Restrained, h.PriorityOf(100));
    }

    [Fact]
    public void With1sPoll_RestrainsOnSecondOverQuotaSample()
    {
        // Matches PL observed live: restraint ~1.9 s after launch = first sample at ~1 s + 900 ms rounded up to a poll.
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 0);
        h.At(0);                                // process just started, no usage sample yet
        h.Processes[0].CpuPercent = 99;
        h.At(1000);
        Assert.Equal(Normal, h.PriorityOf(100));
        h.At(2000);
        Assert.Equal(Restrained, h.PriorityOf(100));
    }

    [Fact]
    public void DipBelowQuota_RestartsTheOverQuotaClock()
    {
        using var h = new ProBalanceHarness();
        var hog = h.Add(100, cpu: 30);

        h.At(0);
        h.At(500);
        hog.CpuPercent = 4.9;                   // one sample under 5 %
        h.At(800);
        hog.CpuPercent = 30;
        h.At(1000);                             // run restarts here
        h.At(1500);
        h.At(1899);
        Assert.Equal(Normal, h.PriorityOf(100));
        h.At(1900);
        Assert.Equal(Restrained, h.PriorityOf(100));
    }

    [Theory]
    [InlineData(4.99, false)]
    [InlineData(5.0, true)]
    public void PerProcessThreshold_IsFivePercentOfTotalCpu(double cpu, bool restrained)
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: cpu);

        h.At(0);
        h.At(1000);

        Assert.Equal(restrained, h.Engine.IsEngaged);
    }

    [Theory]
    [InlineData(9.9, false)]
    [InlineData(10.0, true)]
    public void SystemCpuGate_IsTenPercent(double systemCpu, bool restrained)
    {
        using var h = new ProBalanceHarness { SystemCpu = systemCpu };
        h.Add(100, cpu: 8);

        h.At(0);
        h.At(1000);

        Assert.Equal(restrained, h.Engine.IsEngaged);
    }

    [Fact]
    public void SystemDroppingBelowGate_RestartsTheOverQuotaClock()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 8);

        h.At(0);
        h.SystemCpu = 9;
        h.At(500);
        h.SystemCpu = 50;
        h.At(1000);
        Assert.Equal(Normal, h.PriorityOf(100));
        h.At(1900);
        Assert.Equal(Restrained, h.PriorityOf(100));
    }

    [Fact]
    public void LeavingTheForeground_StartsAFreshOverQuotaRun()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 90);
        h.ForegroundPid = 100;
        for (var t = 0; t <= 5000; t += 1000) h.At(t);

        h.ForegroundPid = 0;                    // user switches away
        h.At(6000);
        Assert.Equal(Normal, h.PriorityOf(100));
        h.At(6900);
        Assert.Equal(Restrained, h.PriorityOf(100));
    }

    // ---------- Restore: per-process hysteresis ----------

    [Fact]
    public void Restore_WaitsForMinimumRestraintTime()
    {
        using var h = new ProBalanceHarness();
        var hog = h.Add(100, cpu: 30);
        h.At(0);
        h.At(1000);                             // restrained at t=1000
        Assert.Equal(Restrained, h.PriorityOf(100));

        hog.CpuPercent = 0;                     // load stops right away
        h.At(2000);
        h.At(5199);
        Assert.Equal(Restrained, h.PriorityOf(100));
        h.At(5200);                             // 4200 ms restrained
        Assert.Equal(Normal, h.PriorityOf(100));
        Assert.False(h.Engine.IsEngaged);
    }

    [Fact]
    public void Restore_AfterMinimumTime_NeedsOneSampleAtOrBelowTwoPercent()
    {
        // PL observed live: a 6 s burst was restored ~1.7 s after the load stopped (one poll below 2 %).
        using var h = new ProBalanceHarness();
        var hog = h.Add(100, cpu: 30);
        h.At(0);
        h.At(1000);
        for (var t = 2000; t <= 6000; t += 1000) h.At(t);
        Assert.Equal(Restrained, h.PriorityOf(100));

        hog.CpuPercent = 1;
        h.At(7000);
        Assert.Equal(Normal, h.PriorityOf(100));
    }

    [Theory]
    [InlineData(2.0, true)]
    [InlineData(2.1, false)]
    [InlineData(4.9, false)]
    public void Restore_ThresholdIsTwoPercent(double cpu, bool restored)
    {
        using var h = new ProBalanceHarness();
        var hog = h.Add(100, cpu: 30);
        h.At(0);
        h.At(1000);

        hog.CpuPercent = cpu;
        for (var t = 2000; t <= 20_000; t += 1000) h.At(t);

        Assert.Equal(restored ? Normal : Restrained, h.PriorityOf(100));
    }

    [Fact]
    public void BetweenRestoreAndRestrainThresholds_NoFlapping()
    {
        using var h = new ProBalanceHarness();
        var hog = h.Add(100, cpu: 30);
        h.At(0);
        h.At(1000);
        hog.CpuPercent = 0;
        h.At(5200);
        Assert.Equal(Normal, h.PriorityOf(100));

        hog.CpuPercent = 4;                     // in the 2..5 % band: stays unrestrained
        for (var t = 6000; t <= 20_000; t += 1000) h.At(t);
        Assert.Equal(Normal, h.PriorityOf(100));
        Assert.Equal(2, h.Access.SetCalls);     // exactly one restrain + one restore
    }

    [Fact]
    public void SystemLoadDoesNotGateRestore_OnlyTheProcessUsage()
    {
        using var h = new ProBalanceHarness();
        var hog = h.Add(100, cpu: 30);
        h.At(0);
        h.At(1000);

        h.SystemCpu = 100;                      // machine still busy with other things
        hog.CpuPercent = 0;
        h.At(5200);

        Assert.Equal(Normal, h.PriorityOf(100));
    }

    [Fact]
    public void MaximumRestraintTime_ForcesRestore_ThenReRestrainsAfterAnotherQuotaRun()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 90);
        h.Engine.MaximumTimeOfRestraint = 3000;

        h.At(0);
        h.At(1000);                             // restrained
        h.At(3999);
        Assert.Equal(Restrained, h.PriorityOf(100));
        h.At(4000);                             // 3000 ms restrained -> released despite 90 %
        Assert.Equal(Normal, h.PriorityOf(100));
        h.At(4899);
        Assert.Equal(Normal, h.PriorityOf(100));
        h.At(4900);
        Assert.Equal(Restrained, h.PriorityOf(100));
    }

    [Fact]
    public void NoMaximumRestraintTime_ByDefault()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 90);

        for (var t = 0; t <= 120_000; t += 1000) h.At(t);

        Assert.Equal(Restrained, h.PriorityOf(100));
        Assert.Equal(1, h.Access.SetCalls);
    }

    [Fact]
    public void BecomingForeground_EndsRestraintImmediately_EvenBeforeMinimumTime()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 90);
        h.At(0);
        h.At(1000);
        Assert.Equal(Restrained, h.PriorityOf(100));

        h.ForegroundPid = 100;
        h.At(1500);

        Assert.Equal(Normal, h.PriorityOf(100));
        Assert.False(h.Engine.IsEngaged);
    }

    [Fact]
    public void BecomingForeground_DoesNotEndRestraint_WhenForegroundExclusionOff()
    {
        using var h = new ProBalanceHarness();
        h.Add(100, cpu: 90);
        h.Engine.ExcludeForegroundProcesses = false;
        h.At(0);
        h.At(1000);

        h.ForegroundPid = 100;
        h.At(2000);

        Assert.Equal(Restrained, h.PriorityOf(100));
    }

    [Fact]
    public void ManyProcesses_AreTrackedIndependently()
    {
        using var h = new ProBalanceHarness();
        var a = h.Add(100, cpu: 30);
        var b = h.Add(101, cpu: 0);

        h.At(0);
        b.CpuPercent = 30;
        h.At(500);
        h.At(900);                              // a: 900 ms over; b: 400 ms
        Assert.Equal(Restrained, h.PriorityOf(100));
        Assert.Equal(Normal, h.PriorityOf(101));
        h.At(1400);
        Assert.Equal(Restrained, h.PriorityOf(101));

        a.CpuPercent = 0;
        h.At(5100);                             // a restrained 4200 ms; b only 3700 ms
        Assert.Equal(Normal, h.PriorityOf(100));
        Assert.Equal(Restrained, h.PriorityOf(101));
    }
}
