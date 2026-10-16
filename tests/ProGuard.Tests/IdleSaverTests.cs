using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Native;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Shared.Messages;
using ProGuard.Shared.Reporting;
using ProGuard.UI.Services;

namespace ProGuard.Tests;

/// <summary>
/// IdleSaver: switch to the idle plan after the timeout, switch back on input, and never fight Performance Mode or
/// a plan the user picked themselves. The power-plan API and the idle source are fakes, so nothing here waits minutes
/// or changes this machine's plan.
/// </summary>
public class IdleSaverTests
{
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private const int TimeoutMinutes = 10;

    private sealed class FakeIdle : IUserIdleSource
    {
        public TimeSpan? Idle { get; set; } = TimeSpan.Zero;
        public TimeSpan? GetIdleTime() => Idle;
    }

    private sealed class FakePlans
    {
        public Guid Active { get; set; } = Balanced;
        public List<Guid> Writes { get; } = new();
        public bool Write(Guid plan) { Writes.Add(plan); Active = plan; return true; }
    }

    private readonly FakeIdle _idle = new();
    private readonly FakePlans _plans = new();
    private readonly PowerManager _power;

    public IdleSaverTests()
    {
        _power = new PowerManager(null, _idle, () => _plans.Active, _plans.Write);
        _power.ConfigureIdleSaver(true, TimeoutMinutes, Powrprof.PowerSaver);
    }

    private void Away() { _idle.Idle = TimeSpan.FromMinutes(TimeoutMinutes) + TimeSpan.FromSeconds(1); _power.CheckIdle(); }
    private void Back() { _idle.Idle = TimeSpan.FromMilliseconds(200); _power.CheckIdle(); }

    [Fact]
    public void Idle_PastTheTimeout_SwitchesToTheIdlePlan_OnlyOnce()
    {
        _idle.Idle = TimeSpan.FromMinutes(TimeoutMinutes) - TimeSpan.FromSeconds(1);
        _power.CheckIdle();
        Assert.Empty(_plans.Writes);

        Away();
        Away();
        Assert.Equal(new[] { Powrprof.PowerSaver }, _plans.Writes);
    }

    [Fact]
    public void Input_RestoresThePreviousPlan()
    {
        Away();
        Back();
        Assert.Equal(Balanced, _plans.Active);
        Assert.Equal(new[] { Powrprof.PowerSaver, Balanced }, _plans.Writes);

        Back();
        Assert.Equal(2, _plans.Writes.Count);
    }

    [Fact]
    public void IdleTimeBecomingUnknown_RestoresThePreviousPlan()
    {
        Away();
        _idle.Idle = null;      // e.g. the user's UI closed: we can no longer claim they're idle
        _power.CheckIdle();
        Assert.Equal(Balanced, _plans.Active);
    }

    [Fact]
    public void UnknownIdleTime_NeverSwitches()
    {
        _idle.Idle = null;
        _power.CheckIdle();
        Assert.Empty(_plans.Writes);
    }

    [Fact]
    public void DisablingWhileIdle_RestoresAtOnce_AndStaysOff()
    {
        Away();
        _power.ConfigureIdleSaver(false, TimeoutMinutes, Powrprof.PowerSaver);
        Assert.Equal(Balanced, _plans.Active);

        Away();
        Assert.Equal(Balanced, _plans.Active);
    }

    [Fact]
    public void ChangingTheIdlePlanWhileIdle_RestoresThenAppliesTheNewPlan()
    {
        Away();
        _power.ConfigureIdleSaver(true, TimeoutMinutes, Ultimate);
        Assert.Equal(Balanced, _plans.Active);

        Away();
        Assert.Equal(Ultimate, _plans.Active);
        Back();
        Assert.Equal(Balanced, _plans.Active);
    }

    [Fact]
    public async Task EngineStop_RestoresThePreviousPlan()
    {
        await _power.StartAsync(CancellationToken.None);
        Away();
        await _power.StopAsync(CancellationToken.None);
        Assert.Equal(Balanced, _plans.Active);
    }

    [Fact]
    public void AlreadyOnTheIdlePlan_NothingToSwitch_AndNothingToRestore()
    {
        _plans.Active = Powrprof.PowerSaver;
        Away();
        Back();
        Assert.Empty(_plans.Writes);
        Assert.Equal(Powrprof.PowerSaver, _plans.Active);
    }

    // ---------- the user changed the plan themselves ----------

    [Fact]
    public void ManualPlanChangeWhileIdle_IsNotRestoredOver_NorSwitchedBackToIdle()
    {
        Away();
        _plans.Active = Ultimate;   // changed in Windows' power options, or by another tool
        Away();                     // same idle stretch: IdleSaver already had its go
        Assert.Equal(Ultimate, _plans.Active);

        Back();
        Assert.Equal(Ultimate, _plans.Active);
        Assert.Equal(new[] { Powrprof.PowerSaver }, _plans.Writes);
    }

    [Fact]
    public void ManualPlanChange_AppliesAgainOnTheNextIdleStretch()
    {
        Away();
        _plans.Active = Ultimate;
        Back();

        Away();
        Assert.Equal(Powrprof.PowerSaver, _plans.Active);
        Back();
        Assert.Equal(Ultimate, _plans.Active);
    }

    [Fact]
    public void ManualPlanChange_ThroughTheEngine_IsAlsoRespected()
    {
        Away();
        _power.SetActivePlan(Ultimate);
        Back();
        Assert.Equal(Ultimate, _plans.Active);
    }

    // ---------- Performance Mode ----------

    [Fact]
    public void PerformanceMode_BlocksTheIdleSwitch()
    {
        _power.EnablePerformanceMode(true);
        Away();
        Assert.Equal(Powrprof.HighPerformance, _plans.Active);

        _power.EnablePerformanceMode(false);
        Assert.Equal(Balanced, _plans.Active);
    }

    [Fact]
    public void PerformanceModeEnding_WhileStillIdle_LetsIdleSaverAct()
    {
        _power.EnablePerformanceMode(true);
        Away();
        _power.EnablePerformanceMode(false);

        Away();
        Assert.Equal(Powrprof.PowerSaver, _plans.Active);
        Back();
        Assert.Equal(Balanced, _plans.Active);
    }

    [Fact]
    public void PerformanceMode_StartedWhileIdle_TakesOver_AndEndsOnTheUsersOwnPlan()
    {
        Away();
        _power.EnablePerformanceMode(true);
        Assert.Equal(Powrprof.HighPerformance, _plans.Active);

        Back();     // IdleSaver no longer owes a restore: it must not touch Performance Mode's plan
        Assert.Equal(Powrprof.HighPerformance, _plans.Active);

        _power.EnablePerformanceMode(false);
        Assert.Equal(Balanced, _plans.Active);     // not the idle plan
    }

    [Fact]
    public void PerformanceModeEnding_DoesNotRestoreOverAManualChange()
    {
        _power.EnablePerformanceMode(true);
        _plans.Active = Ultimate;
        _power.EnablePerformanceMode(false);
        Assert.Equal(Ultimate, _plans.Active);
    }

    // ---------- where the idle time comes from ----------

    /// <summary>PIDs 1xx live in session 1, 2xx in session 2.</summary>
    private static ForegroundTracker Tracker(Func<TimeSpan> clock, bool canSeeDesktop = false, TimeSpan? localIdle = null) =>
        new(null, () => 0, pid => pid / 100, () => 1, canSeeDesktop, () => localIdle, clock);

    [Fact]
    public void Tracker_WithNoReports_InSession0_DoesNotKnow()
    {
        Assert.Null(Tracker(() => TimeSpan.Zero, localIdle: TimeSpan.FromHours(1)).GetIdleTime());
    }

    [Fact]
    public void Tracker_OnAUserDesktop_ReadsItsOwnSession()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), Tracker(() => TimeSpan.Zero, canSeeDesktop: true, localIdle: TimeSpan.FromMinutes(3)).GetIdleTime());
    }

    [Fact]
    public void Tracker_AgesAReport_ByTheTimeSinceItArrived()
    {
        var now = TimeSpan.FromHours(5);
        var tracker = Tracker(() => now);
        tracker.Report(1, 1, 101, TimeSpan.FromMinutes(2));

        now += TimeSpan.FromMinutes(9);
        Assert.Equal(TimeSpan.FromMinutes(11), tracker.GetIdleTime());

        tracker.Report(1, 1, 101, TimeSpan.FromSeconds(1));   // the user came back
        Assert.Equal(TimeSpan.FromSeconds(1), tracker.GetIdleTime());

        tracker.Disconnect(1);
        Assert.Null(tracker.GetIdleTime());
    }

    [Fact]
    public void Tracker_IsIdleOnlyWhileEveryDesktopIs()
    {
        var tracker = Tracker(() => TimeSpan.Zero);
        tracker.Report(1, 1, 101, TimeSpan.FromMinutes(30));
        tracker.Report(2, 2, 201, TimeSpan.FromSeconds(4));
        tracker.Report(3, 1, 102, null);   // an older UI that doesn't send idle time says nothing either way
        Assert.Equal(TimeSpan.FromSeconds(4), tracker.GetIdleTime());
    }

    [Fact]
    public void Tracker_IgnoresTheIdleTimeOfARejectedReport()
    {
        var tracker = Tracker(() => TimeSpan.Zero);
        Assert.False(tracker.Report(1, 1, 201, TimeSpan.FromHours(1)));   // PID from another session
        Assert.Null(tracker.GetIdleTime());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("-5", null)]
    [InlineData("12.5", null)]
    [InlineData("90000", 90000L)]
    [InlineData("99999999999999999", 4294967295L)]   // clamped to what GetLastInputInfo can express
    public void PipeServer_ParsesTheIdleArgument(string? argument, long? expectedMs)
    {
        Assert.Equal(expectedMs, ForegroundPipeServer.ParseIdle(argument) is { } t ? (long)t.TotalMilliseconds : null);
    }

    [Fact]
    public void Reporter_SerializesIdleMilliseconds()
    {
        var message = JsonSerializer.Deserialize<CommandMessage>(ForegroundReporter.Serialize(7, TimeSpan.FromSeconds(90.25)))!;
        Assert.Equal(7, message.TargetPid);
        Assert.Equal("90250", message.Argument);
        Assert.Null(JsonSerializer.Deserialize<CommandMessage>(ForegroundReporter.Serialize(7))!.Argument);
    }

    [Theory]
    // pid, idle s, last pid, last idle s, since last send s, expected
    [InlineData(1, 0.0, 1, 0.0, 6.0, true)]        // periodic resend
    [InlineData(2, 0.0, 1, 0.0, 0.3, true)]        // foreground changed
    [InlineData(1, 0.1, 1, 300.0, 0.3, true)]      // back after being away: tell the engine now
    [InlineData(1, 0.1, 1, 2.0, 0.3, false)]       // ordinary typing: wait for the periodic report
    [InlineData(1, 310.0, 1, 300.0, 0.3, false)]   // still away
    public void Reporter_SendsAtOnceWhenTheUserReturns(int pid, double idle, int lastPid, double lastIdle, double since, bool expected)
    {
        Assert.Equal(expected, ForegroundReporter.ShouldSend(pid, TimeSpan.FromSeconds(idle), lastPid,
            TimeSpan.FromSeconds(lastIdle), TimeSpan.FromSeconds(since)));
    }

    [Fact]
    public async Task UiReporter_DeliversItsIdleTime_ToTheEngine()
    {
        var tracker = new ForegroundTracker(null, () => 0, pid => System.Diagnostics.Process.GetProcessById(pid).SessionId,
            () => -1, canSeeDesktop: false, localIdleTime: () => null, clock: () => TimeSpan.Zero);
        var pipeName = $"ProGuard.Test.Idle.{Guid.NewGuid():N}";
        var server = new ForegroundPipeServer(NullLogger<ForegroundPipeServer>.Instance, tracker) { PipeName = pipeName };
        server.Start();
        var reporter = new ForegroundReporter(pipeName, () => Environment.ProcessId, () => TimeSpan.FromMinutes(42));
        try
        {
            await reporter.StartAsync(CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (tracker.GetIdleTime() == null && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.Equal(TimeSpan.FromMinutes(42), tracker.GetIdleTime());
        }
        finally
        {
            await reporter.StopAsync(CancellationToken.None);
            reporter.Dispose();
            server.Stop();
        }
    }
}
