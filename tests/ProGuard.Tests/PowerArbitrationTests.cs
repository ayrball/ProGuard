using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

/// <summary>
/// PowerManager's single arbitration point: Performance Mode beats IdleSaver, which beats the load switcher; IdleSaver
/// is off during Performance Mode; manual Performance Mode outlives the automatic one; manual plan changes win; and
/// every switch is logged with a reason. Plans, idle time, CPU load and the clock are all fakes.
/// </summary>
public class PowerArbitrationTests
{
    private static readonly Guid Balanced = Powrprof.Balanced;
    private static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid LoadHigh = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid LoadLow = new("66666666-7777-8888-9999-000000000000");
    private const int IdleMinutes = 10;

    private sealed class FakeIdle : IUserIdleSource
    {
        public TimeSpan? Idle { get; set; } = TimeSpan.Zero;
        public TimeSpan? GetIdleTime() => Idle;
    }

    private sealed class FakePlans
    {
        public Guid Active { get; set; } = Balanced;
        public List<Guid> Writes { get; } = new();
        public bool Fail { get; set; }
        public bool Write(Guid plan)
        {
            if (Fail) return false;
            Writes.Add(plan);
            Active = plan;
            return true;
        }
    }

    private readonly FakeIdle _idle = new();
    private readonly FakePlans _plans = new();
    private double _cpu;
    private TimeSpan _now = TimeSpan.FromHours(1);
    private readonly PowerManager _power;

    public PowerArbitrationTests()
    {
        _power = new PowerManager(null, _idle, () => _plans.Active, _plans.Write, () => _cpu, () => _now);
    }

    private void EnableLoadSwitcher(bool deferLowPlan = true) => _power.ConfigureLoadSwitcher(new LoadSwitcherOptions
    {
        Enabled = true,
        LowThreshold = 10,
        HighThreshold = 50,
        AveragingWindowSamples = 1,
        MinimumTimeInPlanMs = 0,
        LowPlan = LoadLow,
        HighPlan = LoadHigh,
        DeferLowPlan = deferLowPlan,
    });

    private void Load(double cpu)
    {
        _cpu = cpu;
        _now += TimeSpan.FromSeconds(1);
        _power.SampleLoad();
    }

    private void EnableIdleSaver() => _power.ConfigureIdleSaver(true, IdleMinutes, Powrprof.PowerSaver);
    private void Away() { _idle.Idle = TimeSpan.FromMinutes(IdleMinutes + 1); _power.CheckIdle(); }
    private void Back() { _idle.Idle = TimeSpan.FromSeconds(1); _power.CheckIdle(); }

    // ---------- the load switcher on its own ----------

    [Fact]
    public void LoadSwitcher_HighLoad_SwitchesToTheHighPlan_AndLowLoadRestores_WhenDeferred()
    {
        EnableLoadSwitcher(deferLowPlan: true);
        Load(80);
        Assert.Equal(LoadHigh, _plans.Active);
        Load(2);
        Assert.Equal(Balanced, _plans.Active);
        Assert.Equal(new[] { LoadHigh, Balanced }, _plans.Writes);
    }

    [Fact]
    public void LoadSwitcher_UsesTheLowPlan_WhenNotDeferred_AndRestoresWhenDisabled()
    {
        EnableLoadSwitcher(deferLowPlan: false);
        Load(80);
        Load(2);
        Assert.Equal(LoadLow, _plans.Active);

        _power.ConfigureLoadSwitcher(new LoadSwitcherOptions { Enabled = false });
        Assert.Equal(Balanced, _plans.Active);
    }

    [Fact]
    public void LoadSwitcher_InvalidSettings_AreRefused()
    {
        _power.ConfigureLoadSwitcher(new LoadSwitcherOptions { Enabled = true, LowThreshold = 60, HighThreshold = 50, AveragingWindowSamples = 1 });
        Load(80);
        Load(80);
        Assert.Empty(_plans.Writes);
    }

    // ---------- precedence ----------

    [Fact]
    public void PerformanceMode_BeatsTheLoadSwitcher_WhichResumesAfterwards()
    {
        EnableLoadSwitcher();
        Load(80);
        Assert.Equal(LoadHigh, _plans.Active);

        _power.EnablePerformanceMode(true);
        Assert.Equal(Powrprof.HighPerformance, _plans.Active);
        Load(2);
        Load(80);
        Assert.Equal(Powrprof.HighPerformance, _plans.Active);   // the switcher still decides, but doesn't switch

        _power.EnablePerformanceMode(false);
        Assert.Equal(LoadHigh, _plans.Active);                   // its current decision applies at once

        Load(2);
        Assert.Equal(Balanced, _plans.Active);                   // and the baseline survived both
    }

    [Fact]
    public void IdleSaver_BeatsTheLoadSwitcher()
    {
        EnableIdleSaver();
        EnableLoadSwitcher();
        Load(80);
        Away();
        Assert.Equal(Powrprof.PowerSaver, _plans.Active);
        Load(90);
        Assert.Equal(Powrprof.PowerSaver, _plans.Active);

        Back();
        Assert.Equal(LoadHigh, _plans.Active);
        Load(1);
        Assert.Equal(Balanced, _plans.Active);
    }

    [Fact]
    public void PerformanceMode_BeatsIdleSaver_AndDisablesIt()
    {
        EnableIdleSaver();
        _power.EnablePerformanceMode(true);
        Away();
        Away();
        Assert.Equal(Powrprof.HighPerformance, _plans.Active);
        Assert.Equal(PowerPlanOwner.PerformanceMode, _power.Owner);

        _power.EnablePerformanceMode(false);
        Assert.Equal(Balanced, _plans.Active);   // IdleSaver starts over rather than taking over at once
        Away();
        Assert.Equal(Powrprof.PowerSaver, _plans.Active);
    }

    [Fact]
    public void AllThree_UnwindToTheUsersPlan()
    {
        EnableIdleSaver();
        EnableLoadSwitcher();
        Load(80);
        Away();
        _power.SetAutomaticPerformanceMode(true, "game.exe is running");
        Assert.Equal(Powrprof.HighPerformance, _plans.Active);

        _power.SetAutomaticPerformanceMode(false, "game.exe exited");
        Back();
        Load(1);
        Assert.Equal(Balanced, _plans.Active);
        Assert.Equal(PowerPlanOwner.None, _power.Owner);
    }

    [Fact]
    public void PerformanceMode_UsesTheConfiguredPlan()
    {
        _power.ConfigurePerformanceMode(Ultimate);
        _power.EnablePerformanceMode(true);
        Assert.Equal(Ultimate, _plans.Active);

        _power.ConfigurePerformanceMode(Powrprof.HighPerformance);   // changed while on: applies at once
        Assert.Equal(Powrprof.HighPerformance, _plans.Active);

        _power.EnablePerformanceMode(false);
        Assert.Equal(Balanced, _plans.Active);
    }

    // ---------- manual and automatic Performance Mode ----------

    [Fact]
    public void AutomaticPerformanceMode_EndsWhenTheGameDoes()
    {
        _power.SetAutomaticPerformanceMode(true, "game.exe is running");
        Assert.True(_power.IsPerformanceModeActive);
        Assert.Equal(Powrprof.HighPerformance, _plans.Active);

        _power.SetAutomaticPerformanceMode(false, "game.exe exited");
        Assert.False(_power.IsPerformanceModeActive);
        Assert.Equal(Balanced, _plans.Active);
    }

    [Fact]
    public void ManualPerformanceMode_OutlivesTheAutomaticOne()
    {
        _power.SetAutomaticPerformanceMode(true, "game.exe is running");
        _power.EnablePerformanceMode(true);
        _power.SetAutomaticPerformanceMode(false, "game.exe exited");
        Assert.True(_power.IsPerformanceModeActive);
        Assert.Equal(Powrprof.HighPerformance, _plans.Active);

        _power.EnablePerformanceMode(false);
        Assert.Equal(Balanced, _plans.Active);
    }

    [Fact]
    public void ManualPerformanceMode_StartedFirst_StaysOnAfterTheGame()
    {
        _power.EnablePerformanceMode(true);
        _power.SetAutomaticPerformanceMode(true, "game.exe is running");
        _power.SetAutomaticPerformanceMode(false, "game.exe exited");
        Assert.True(_power.IsPerformanceModeActive);
        Assert.Single(_plans.Writes);
    }

    [Fact]
    public void TurningItOffByHand_WhileAGameRuns_StaysOffUntilTheGamesHaveGone()
    {
        _power.SetAutomaticPerformanceMode(true, "game.exe is running");
        _power.EnablePerformanceMode(false);
        Assert.False(_power.IsPerformanceModeActive);
        Assert.Equal(Balanced, _plans.Active);

        _power.SetAutomaticPerformanceMode(true, "other.exe is running");   // another game: still off
        Assert.False(_power.IsPerformanceModeActive);

        _power.SetAutomaticPerformanceMode(false, "other.exe exited");
        _power.SetAutomaticPerformanceMode(true, "game.exe is running");     // next session: back on
        Assert.True(_power.IsPerformanceModeActive);
    }

    // ---------- manual plan changes ----------

    [Fact]
    public void ManualChange_WhileTheLoadSwitcherHoldsThePlan_IsNotReappliedOrRestoredOver()
    {
        EnableLoadSwitcher();
        Load(80);
        _plans.Active = Ultimate;      // picked in Windows' power options
        Load(85);
        Assert.Equal(Ultimate, _plans.Active);

        Load(2);                       // low, deferred: would restore, but the user chose since
        Assert.Equal(Ultimate, _plans.Active);

        Load(80);                      // a new decision applies, and comes back to the user's plan
        Assert.Equal(LoadHigh, _plans.Active);
        Load(2);
        Assert.Equal(Ultimate, _plans.Active);
    }

    [Fact]
    public void ManualChange_DuringPerformanceMode_BecomesThePlanToReturnTo_AfterTheNextFeature()
    {
        EnableIdleSaver();
        _power.EnablePerformanceMode(true);
        _plans.Active = Ultimate;
        _power.EnablePerformanceMode(false);
        Assert.Equal(Ultimate, _plans.Active);

        Away();
        Assert.Equal(Powrprof.PowerSaver, _plans.Active);
        Back();
        Assert.Equal(Ultimate, _plans.Active);
    }

    [Fact]
    public void FailedSwitch_IsNotMistakenForAManualChange_NorRetriedEverySample()
    {
        EnableLoadSwitcher();
        _plans.Fail = true;
        Load(80);
        Load(80);
        Assert.Equal(Balanced, _plans.Active);

        _plans.Fail = false;
        Load(2);
        Load(80);                       // a new decision: tried again
        Assert.Equal(LoadHigh, _plans.Active);
        Load(2);
        Assert.Equal(Balanced, _plans.Active);
    }

    // ---------- logging ----------

    [Fact]
    public void EveryPlanChange_IsRecordedWithItsReason()
    {
        var raised = new List<PowerPlanChange>();
        _power.PlanChanged += (_, c) => raised.Add(c);
        EnableIdleSaver();
        EnableLoadSwitcher();

        Load(80);
        Away();
        _power.SetAutomaticPerformanceMode(true, "game.exe is running (detected game)");
        _power.SetAutomaticPerformanceMode(false, "game.exe exited (detected game)");
        Back();
        Load(1);

        var changes = _power.RecentChanges;
        Assert.Equal(_plans.Writes, changes.Select(c => c.To));
        Assert.Equal(changes, raised);
        Assert.All(changes, c => Assert.False(string.IsNullOrWhiteSpace(c.Reason)));
        Assert.Collection(changes,
            c => { Assert.Equal(PowerPlanOwner.LoadSwitcher, c.Owner); Assert.Contains("above 50%", c.Reason); },
            c => { Assert.Equal(PowerPlanOwner.IdleSaver, c.Owner); Assert.Contains("no input", c.Reason); },
            c => { Assert.Equal(PowerPlanOwner.PerformanceMode, c.Owner); Assert.Contains("game.exe is running", c.Reason); },
            // Performance Mode ends while the user is still away: IdleSaver is off during it, so the load switcher's
            // plan comes back first and IdleSaver gets its turn on the next check.
            c => { Assert.Equal(LoadHigh, c.To); Assert.Contains("game.exe exited", c.Reason); },
            c => { Assert.Equal(Balanced, c.To); Assert.Contains("below 10%", c.Reason); });
    }

    [Fact]
    public void EveryPlanChange_ReachesTheLogsPage()
    {
        var logged = new List<ProBalanceAction>();
        var store = new Mock<IRuleStore>();
        store.Setup(r => r.LogProBalanceActionAsync(It.IsAny<ProBalanceAction>()))
             .Callback<ProBalanceAction>(logged.Add).Returns(Task.CompletedTask);
        var power = new PowerManager(null, _idle, () => _plans.Active, _plans.Write, ruleStore: store.Object);

        power.EnablePerformanceMode(true);
        power.EnablePerformanceMode(false);

        Assert.Equal(2, logged.Count);
        Assert.All(logged, a =>
        {
            Assert.Equal(ProBalanceAction.PowerPlanEntryName, a.ProcessName);
            Assert.Equal(0, a.Pid);
            Assert.True(a.IsRestored);   // not counted as a restrained process on the dashboard
        });
        Assert.Equal("Balanced -> High Performance: Performance Mode turned on", logged[0].Reason);
        Assert.StartsWith("High Performance -> Balanced: Performance Mode turned off", logged[1].Reason);
    }
}
