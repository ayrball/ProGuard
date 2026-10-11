using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;
using static ProGuard.Engine.Services.LoadPowerSwitcher;

namespace ProGuard.Tests;

/// <summary>
/// The load-based power switcher's decisions: moving average, thresholds, the minimum time in a plan, and the deferred
/// low plan. Time is a fake clock advanced by the test, one sample per second like the engine's loop.
/// </summary>
public class LoadPowerSwitcherTests
{
    private static readonly Guid LowPlan = Powrprof.PowerSaver;
    private static readonly Guid HighPlan = Powrprof.HighPerformance;

    private static LoadSwitcherOptions Options(bool deferLowPlan = false, int samples = 5, int minimumMs = 5000) => new()
    {
        Enabled = true,
        LowThreshold = 10,
        HighThreshold = 50,
        AveragingWindowSamples = samples,
        MinimumTimeInPlanMs = minimumMs,
        LowPlan = LowPlan,
        HighPlan = HighPlan,
        DeferLowPlan = deferLowPlan,
    };

    private sealed class Driver
    {
        public LoadPowerSwitcher Switcher { get; }
        public TimeSpan Now { get; private set; } = TimeSpan.FromHours(1);
        public Driver(LoadSwitcherOptions options) => Switcher = new LoadPowerSwitcher(options);

        /// <summary>Feeds samples one second apart; returns the change descriptions.</summary>
        public List<string> Feed(params double[] samples)
        {
            var changes = new List<string>();
            foreach (var cpu in samples)
            {
                Now += TimeSpan.FromSeconds(1);
                if (Switcher.AddSample(cpu, Now) is { } change) changes.Add(change);
            }
            return changes;
        }

        public void Wait(TimeSpan time) => Now += time;
    }

    [Fact]
    public void NoDecision_UntilTheWindowIsFull()
    {
        var d = new Driver(Options());
        d.Feed(90, 90, 90, 90);
        Assert.Null(d.Switcher.Average);
        Assert.Equal(LoadLevel.Undecided, d.Switcher.Level);
        Assert.Null(d.Switcher.RequestedPlan);

        d.Feed(90);
        Assert.Equal(90, d.Switcher.Average);
        Assert.Equal(LoadLevel.High, d.Switcher.Level);
        Assert.Equal(HighPlan, d.Switcher.RequestedPlan);
    }

    [Fact]
    public void Average_IsOverTheLastSamplesOnly()
    {
        var d = new Driver(Options());
        d.Feed(100, 100, 100, 0, 0, 0, 0, 0);    // the 100s have left the 5-sample window
        Assert.Equal(0, d.Switcher.Average);
        d.Feed(30);
        Assert.Equal(6, d.Switcher.Average!.Value, 6);
    }

    [Fact]
    public void ASingleSpike_DoesNotSwitch_ButSustainedLoadDoes()
    {
        var d = new Driver(Options());
        d.Feed(20, 20, 20, 20, 100);   // average 32%: between the thresholds
        Assert.Equal(LoadLevel.Undecided, d.Switcher.Level);

        d.Feed(100, 100);              // 20, 20, 100, 100, 100 = 68%
        Assert.Equal(LoadLevel.High, d.Switcher.Level);
    }

    [Theory]
    [InlineData(50.0, LoadLevel.Undecided)]   // "above" means strictly above
    [InlineData(50.1, LoadLevel.High)]
    [InlineData(10.0, LoadLevel.Undecided)]   // and "below" strictly below
    [InlineData(9.9, LoadLevel.Low)]
    [InlineData(30.0, LoadLevel.Undecided)]
    public void Thresholds(double load, LoadLevel expected)
    {
        var d = new Driver(Options());
        d.Feed(load, load, load, load, load);
        Assert.Equal(expected, d.Switcher.Level);
    }

    [Fact]
    public void BetweenTheThresholds_TheLastDecisionStands()
    {
        var d = new Driver(Options());
        d.Feed(80, 80, 80, 80, 80);
        Assert.Equal(LoadLevel.High, d.Switcher.Level);
        d.Wait(TimeSpan.FromMinutes(1));
        d.Feed(30, 30, 30, 30, 30, 30, 30);
        Assert.Equal(LoadLevel.High, d.Switcher.Level);
    }

    [Fact]
    public void Hysteresis_WaitsTheMinimumTimeInAPlan_BeforeTheNextChange()
    {
        var d = new Driver(Options(samples: 1, minimumMs: 5000));
        Assert.Single(d.Feed(80));              // first decision: immediate
        var highAt = d.Now;

        // Load drops at once, but the plan has been on for less than 5 s.
        Assert.Empty(d.Feed(0, 0, 0, 0));       // 1-4 s after the switch
        Assert.Equal(LoadLevel.High, d.Switcher.Level);

        Assert.Single(d.Feed(0));               // 5 s after
        Assert.Equal(TimeSpan.FromSeconds(5), d.Now - highAt);
        Assert.Equal(LoadLevel.Low, d.Switcher.Level);
    }

    [Fact]
    public void Hysteresis_DoesNotDelayStayingPut()
    {
        // Load going up and down quickly within the hold time ends up wherever it is once the hold expires.
        var d = new Driver(Options(samples: 1, minimumMs: 5000));
        d.Feed(80);
        d.Feed(0, 80, 0, 80, 80);
        Assert.Equal(LoadLevel.High, d.Switcher.Level);
    }

    [Fact]
    public void NoHysteresis_WhenTheMinimumTimeIsZero()
    {
        var d = new Driver(Options(samples: 1, minimumMs: 0));
        Assert.Equal(3, d.Feed(80, 0, 80).Count);
    }

    [Fact]
    public void DeferLowPlan_AsksForNoPlanAtLowLoad()
    {
        var d = new Driver(Options(deferLowPlan: true, samples: 1, minimumMs: 0));
        d.Feed(80);
        Assert.Equal(HighPlan, d.Switcher.RequestedPlan);
        d.Feed(1);
        Assert.Equal(LoadLevel.Low, d.Switcher.Level);
        Assert.Null(d.Switcher.RequestedPlan);   // PowerManager restores the plan from before
    }

    [Fact]
    public void LowPlan_IsRequestedAtLowLoad_WhenNotDeferred()
    {
        var d = new Driver(Options(deferLowPlan: false, samples: 1));
        d.Feed(1);
        Assert.Equal(LowPlan, d.Switcher.RequestedPlan);
    }

    [Fact]
    public void ChangeDescription_SaysWhy()
    {
        var d = new Driver(Options(samples: 1));
        Assert.Equal("average CPU 75.0% is above 50%", d.Feed(75).Single());
    }

    // ---------- validation ----------

    [Theory]
    [InlineData(10, 50, null)]
    [InlineData(50, 50, "greater")]
    [InlineData(60, 50, "greater")]
    [InlineData(-1, 50, "low CPU threshold")]
    [InlineData(10, 101, "high CPU threshold")]
    public void Validate_HighMustBeAboveLow(double low, double high, string? error)
    {
        var message = (Options() with { LowThreshold = low, HighThreshold = high }).Validate();
        if (error == null) Assert.Null(message);
        else Assert.Contains(error, message);
    }

    [Fact]
    public void Validate_ChecksWindowTimeAndPlans()
    {
        Assert.NotNull((Options() with { AveragingWindowSamples = 0 }).Validate());
        Assert.NotNull((Options() with { MinimumTimeInPlanMs = -1 }).Validate());
        Assert.NotNull((Options() with { HighPlan = Guid.Empty }).Validate());
        Assert.NotNull((Options() with { LowPlan = Guid.Empty }).Validate());
        Assert.Null((Options(deferLowPlan: true) with { LowPlan = Guid.Empty }).Validate());   // unused when deferred
    }

    [Fact]
    public void Defaults_MatchProcessLasso()
    {
        var o = LoadSwitcherOptions.FromSettings(new ProGuard.Core.Models.AppSettings());
        Assert.False(o.Enabled);
        Assert.Equal(10, o.LowThreshold);
        Assert.Equal(50, o.HighThreshold);
        Assert.Equal(5, o.AveragingWindowSamples);
        Assert.Equal(5000, o.MinimumTimeInPlanMs);
        Assert.True(o.DeferLowPlan);
        Assert.Null(o.Validate());
    }
}
