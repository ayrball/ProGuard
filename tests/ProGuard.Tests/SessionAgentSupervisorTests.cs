using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.SessionAgents;
using ProGuard.Shared.Reporting;

namespace ProGuard.Tests;

/// <summary>
/// <see cref="SessionAgentSupervisor"/> on a fake clock and fake WTS: which sessions get an agent, restarts and their
/// back-off, sign-out, adoption, and stopping.
/// </summary>
[Collection(SessionAgentTests.Collection)]
public class SessionAgentSupervisorTests
{
    private sealed class FakeAgent : IAgentProcess
    {
        public FakeAgent(int id, int session) { Id = id; Session = session; }
        public int Id { get; }
        public int Session { get; }
        public int? ExitCode { get; set; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public void Kill() { Killed = true; ExitCode ??= 1; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakePlatform : ISessionAgentPlatform
    {
        private int _nextPid = 1000;
        public bool IsAgentInstalled { get; set; } = true;
        public List<UserSession> Sessions { get; } = new();
        public List<FakeAgent> Launched { get; } = new();
        public Dictionary<int, FakeAgent> Orphans { get; } = new();
        /// <summary>Sessions where launching throws, as WTSQueryUserToken does before the user's token exists.</summary>
        public HashSet<int> Refusing { get; } = new();
        public int LaunchAttempts { get; private set; }

        public IReadOnlyList<UserSession> GetUserSessions() => Sessions.ToList();

        public IAgentProcess? FindRunningAgent(int sessionId) =>
            Orphans.Remove(sessionId, out var orphan) ? orphan : null;

        public IAgentProcess Launch(int sessionId)
        {
            LaunchAttempts++;
            if (Refusing.Contains(sessionId)) throw new InvalidOperationException($"No user is signed in to session {sessionId}");
            var agent = new FakeAgent(_nextPid++, sessionId);
            Launched.Add(agent);
            return agent;
        }

        public FakeAgent Running(int session) => Launched.Last(a => a.Session == session);
        public int LaunchesIn(int session) => Launched.Count(a => a.Session == session);
    }

    private TimeSpan _now = TimeSpan.FromHours(1);
    private readonly FakePlatform _platform = new();

    private SessionAgentSupervisor Supervisor(int? onlySession = null)
    {
        var supervisor = new SessionAgentSupervisor(null, _platform, onlySession, () => _now);
        Assert.True(supervisor.StartSupervising());
        return supervisor;
    }

    private void After(TimeSpan elapsed, SessionAgentSupervisor supervisor)
    {
        _now += elapsed;
        supervisor.Reconcile();
    }

    [Fact]
    public void StartsOneAgentInEveryActiveUserSession()
    {
        _platform.Sessions.AddRange(new[] { new UserSession(1, true), new UserSession(2, true), new UserSession(3, false) });
        using var s = Supervisor();
        s.Reconcile();
        s.Reconcile();

        Assert.Equal(1, _platform.LaunchesIn(1));
        Assert.Equal(1, _platform.LaunchesIn(2));
        Assert.Equal(0, _platform.LaunchesIn(3));           // disconnected: nobody to report for
        Assert.Equal(new[] { 1, 2 }, s.SupervisedSessions.OrderBy(x => x));
    }

    [Fact]
    public void NeverStartsAnAgentInSession0()
    {
        _platform.Sessions.Add(new UserSession(0, true));
        using var s = Supervisor();
        s.Reconcile();
        Assert.Empty(_platform.Launched);
    }

    [Fact]
    public void AsAnOrdinaryProcess_OnlyItsOwnSessionGetsAnAgent()
    {
        _platform.Sessions.AddRange(new[] { new UserSession(1, true), new UserSession(2, true) });
        using var s = Supervisor(onlySession: 2);
        s.Reconcile();
        Assert.Equal(0, _platform.LaunchesIn(1));
        Assert.Equal(1, _platform.LaunchesIn(2));
    }

    [Fact]
    public void WithoutTheAgentInstalled_DoesNothing()
    {
        _platform.IsAgentInstalled = false;
        _platform.Sessions.Add(new UserSession(1, true));
        using var s = new SessionAgentSupervisor(null, _platform, null, () => _now);
        Assert.False(s.Start());
        Assert.False(s.StartSupervising());
        s.Reconcile();
        Assert.Empty(_platform.Launched);
    }

    [Fact]
    public void ADeadAgent_IsRestarted_WithADoublingBackOff()
    {
        _platform.Sessions.Add(new UserSession(1, true));
        using var s = Supervisor();
        s.Reconcile();

        var delays = new[] { 2, 4, 8, 16 };
        foreach (var seconds in delays)
        {
            var launches = _platform.LaunchesIn(1);
            _platform.Running(1).ExitCode = -1073741819;     // crashed at once
            After(TimeSpan.FromSeconds(0.5), s);             // noticed, restart scheduled
            After(TimeSpan.FromSeconds(seconds - 1), s);
            Assert.Equal(launches, _platform.LaunchesIn(1)); // not yet
            After(TimeSpan.FromSeconds(1), s);
            Assert.Equal(launches + 1, _platform.LaunchesIn(1));
        }
    }

    [Fact]
    public void BackOff_IsCapped()
    {
        Assert.Equal(SessionAgentSupervisor.FirstRetryDelay, SessionAgentSupervisor.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), SessionAgentSupervisor.RetryDelay(2));
        Assert.Equal(SessionAgentSupervisor.MaxRetryDelay, SessionAgentSupervisor.RetryDelay(12));
        Assert.Equal(SessionAgentSupervisor.MaxRetryDelay, SessionAgentSupervisor.RetryDelay(int.MaxValue));
    }

    [Fact]
    public void AnAgentThatRanForAWhile_IsRestartedPromptly()
    {
        _platform.Sessions.Add(new UserSession(1, true));
        using var s = Supervisor();
        s.Reconcile();
        // Three quick failures build up a back-off...
        for (var i = 0; i < 3; i++)
        {
            _platform.Running(1).ExitCode = 1;
            After(TimeSpan.FromSeconds(0.1), s);
            After(SessionAgentSupervisor.MaxRetryDelay, s);
        }
        var launches = _platform.LaunchesIn(1);

        // ...which a long healthy run wipes out.
        After(SessionAgentSupervisor.StableRunTime, s);
        _platform.Running(1).ExitCode = 1;
        After(TimeSpan.FromSeconds(0.1), s);
        After(SessionAgentSupervisor.FirstRetryDelay, s);
        Assert.Equal(launches + 1, _platform.LaunchesIn(1));
    }

    [Fact]
    public void ASessionThatRefusesTheLaunch_IsRetriedWithBackOff()
    {
        _platform.Sessions.Add(new UserSession(1, true));
        _platform.Refusing.Add(1);
        using var s = Supervisor();
        s.Reconcile();
        Assert.Equal(1, _platform.LaunchAttempts);

        After(TimeSpan.FromSeconds(1), s);
        Assert.Equal(1, _platform.LaunchAttempts);
        After(TimeSpan.FromSeconds(1), s);                  // 2 s
        Assert.Equal(2, _platform.LaunchAttempts);
        After(TimeSpan.FromSeconds(3), s);                  // 4 s needed
        Assert.Equal(2, _platform.LaunchAttempts);

        _platform.Refusing.Clear();
        After(TimeSpan.FromSeconds(1), s);
        Assert.Equal(3, _platform.LaunchAttempts);
        Assert.Equal(new[] { 1 }, s.SupervisedSessions);
    }

    [Fact]
    public void SignOut_EndsTheAgent_AndStartsNothingWhileTheSessionCloses()
    {
        _platform.Sessions.Add(new UserSession(1, true));
        using var s = Supervisor();
        s.Reconcile();
        var agent = _platform.Running(1);

        s.OnSessionChange(1, SessionChangeReason.SessionLogoff);
        Assert.True(agent.Killed);
        Assert.True(agent.Disposed);
        Assert.Empty(s.SupervisedSessions);

        // Still listed while Windows closes it: no new agent.
        After(TimeSpan.FromSeconds(10), s);
        Assert.Equal(1, _platform.LaunchesIn(1));

        // The id is reused by the next sign-in: an agent at once.
        s.OnSessionChange(1, SessionChangeReason.SessionLogon);
        Assert.Equal(2, _platform.LaunchesIn(1));
    }

    [Fact]
    public void ASessionThatDisappears_LosesItsAgent()
    {
        _platform.Sessions.AddRange(new[] { new UserSession(1, true), new UserSession(2, true) });
        using var s = Supervisor();
        s.Reconcile();
        var second = _platform.Running(2);

        _platform.Sessions.RemoveAll(x => x.Id == 2);       // signed out with no notification (not a service)
        After(TimeSpan.FromSeconds(2), s);
        Assert.True(second.Killed);
        Assert.Equal(new[] { 1 }, s.SupervisedSessions);
    }

    [Fact]
    public void ADisconnectedSession_KeepsItsAgent_AndGetsANewOneOnlyWhenItReconnects()
    {
        _platform.Sessions.Add(new UserSession(1, true));
        using var s = Supervisor();
        s.Reconcile();
        var agent = _platform.Running(1);

        _platform.Sessions[0] = new UserSession(1, false);  // fast user switching away from it
        After(TimeSpan.FromSeconds(2), s);
        Assert.False(agent.Killed);

        agent.ExitCode = 1;
        After(TimeSpan.FromMinutes(1), s);
        Assert.Equal(1, _platform.LaunchesIn(1));            // nobody there to report for

        _platform.Sessions[0] = new UserSession(1, true);
        s.OnSessionChange(1, SessionChangeReason.ConsoleConnect);
        After(SessionAgentSupervisor.FirstRetryDelay, s);
        Assert.Equal(2, _platform.LaunchesIn(1));
    }

    [Fact]
    public void AnAgentLeftByACrashedEngine_IsAdopted_NotDuplicated()
    {
        var orphan = new FakeAgent(77, 1);
        _platform.Orphans[1] = orphan;
        _platform.Sessions.Add(new UserSession(1, true));
        using var s = Supervisor();
        s.Reconcile();

        Assert.Empty(_platform.Launched);
        Assert.Equal(77, s.AgentProcessId(1));
    }

    [Fact]
    public void AnAgentThatFindsAnotherInItsSession_IsNotReportedAsAFailureLoop()
    {
        _platform.Sessions.Add(new UserSession(1, true));
        using var s = Supervisor();
        s.Reconcile();
        _platform.Running(1).ExitCode = SessionAgentInstance.AlreadyRunningExitCode;

        // The other copy is found (it's ours, left behind) and watched from then on.
        _platform.Orphans[1] = new FakeAgent(88, 1);
        After(TimeSpan.FromSeconds(0.1), s);
        After(SessionAgentSupervisor.FirstRetryDelay, s);
        Assert.Equal(88, s.AgentProcessId(1));
        Assert.Equal(1, _platform.LaunchesIn(1));
    }

    [Fact]
    public void Stop_EndsEveryAgent_AndIgnoresLaterEvents()
    {
        _platform.Sessions.AddRange(new[] { new UserSession(1, true), new UserSession(2, true) });
        var s = Supervisor();
        s.Reconcile();

        s.Stop();
        Assert.All(_platform.Launched, a => Assert.True(a.Killed && a.Disposed));

        s.OnSessionChange(3, SessionChangeReason.SessionLogon);
        s.Reconcile();
        Assert.Equal(2, _platform.Launched.Count);
    }

    // ---------- The Win32 platform, against this machine ----------

    [Fact]
    public void Win32_ListsSignedInSessions_ButNeverSession0()
    {
        var platform = new Win32SessionAgentPlatform(new UserSessionLauncher(NullLogger<UserSessionLauncher>.Instance));
        var sessions = platform.GetUserSessions();
        Assert.DoesNotContain(sessions, x => x.Id == 0);

        var own = Process.GetCurrentProcess().SessionId;
        if (own != 0) Assert.Contains(sessions, x => x.Id == own);
    }

    [Fact]
    public void Win32_StartsWatchesAndEndsTheAgent_InThisSession()
    {
        var own = Process.GetCurrentProcess().SessionId;
        if (own == 0) return;   // a CI runner in the services session: nothing may be started there

        var platform = new Win32SessionAgentPlatform(new UserSessionLauncher(NullLogger<UserSessionLauncher>.Instance));
        Assert.True(platform.IsAgentInstalled, platform.AgentPath);

        // Not SYSTEM, so the launcher starts it as us in our own session. It reports to the test run's private pipe.
        using var agent = platform.Launch(own);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => SessionAgentInstance.IsRunning(), 15_000), "the agent never claimed the session");
            Assert.Null(agent.ExitCode);

            using (var found = platform.FindRunningAgent(own))
                Assert.Equal(agent.Id, found?.Id);

            // A program of the same name elsewhere is none of an engine's business, nor is ours to another install.
            var elsewhere = new Win32SessionAgentPlatform(new UserSessionLauncher(NullLogger<UserSessionLauncher>.Instance),
                Path.Combine(Path.GetTempPath(), "nowhere", SessionAgentInstance.ExecutableName));
            Assert.False(elsewhere.IsAgentInstalled);
            Assert.Null(elsewhere.FindRunningAgent(own));
        }
        finally
        {
            agent.Kill();
        }
        Assert.True(SpinWait.SpinUntil(() => agent.ExitCode != null, 5_000));
        Assert.Equal(1, agent.ExitCode);
    }
}
