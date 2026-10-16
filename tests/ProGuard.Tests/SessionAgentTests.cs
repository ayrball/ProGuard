using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Shared.Reporting;

namespace ProGuard.Tests;

/// <summary>
/// The session agent reports the foreground process for its session whether or not the UI is open; the UI's reporter
/// stands aside while one runs. Agents started here inherit the test run's private pipe and mutex names
/// (<see cref="TestEnvironment"/>), so they never reach an installed engine or silence the UI on this desktop.
/// </summary>
public class SessionAgentTests
{
    private static string UniqueName(string what) => $@"Local\ProGuardTest_{what}_" + Guid.NewGuid().ToString("N");

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public void OneAgentPerSession_TheSecondClaimFails_UntilTheFirstLetsGo()
    {
        var name = UniqueName("Mutex");
        Assert.False(SessionAgentInstance.IsRunning(name));

        using (var first = SessionAgentInstance.TryClaim(name))
        {
            Assert.NotNull(first);
            Assert.True(SessionAgentInstance.IsRunning(name));
            Assert.Null(SessionAgentInstance.TryClaim(name));
        }

        Assert.False(SessionAgentInstance.IsRunning(name));
        using var again = SessionAgentInstance.TryClaim(name);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task UiReporter_StandsAsideWhileAnAgentReports_AndResumesWhenItIsGone()
    {
        var tracker = new ForegroundTracker(null, () => 0, pid => Process.GetProcessById(pid).SessionId, () => -1, canSeeDesktop: false);
        var pipeName = "ProGuardTest_Fg_" + Guid.NewGuid().ToString("N");
        var server = new ForegroundPipeServer(NullLogger<ForegroundPipeServer>.Instance, tracker) { PipeName = pipeName };
        server.Start();
        var agentRunning = false;
        var reporter = new ForegroundReporter(pipeName, () => Environment.ProcessId, shouldReport: () => !Volatile.Read(ref agentRunning));
        try
        {
            await reporter.StartAsync(CancellationToken.None);
            Assert.True(await WaitFor(() => tracker.HasReporters));

            // An agent starts: the UI hangs up, so the engine hears only from the agent.
            Volatile.Write(ref agentRunning, true);
            Assert.True(await WaitFor(() => !tracker.HasReporters));
            await Task.Delay(500);
            Assert.False(tracker.HasReporters);

            // The agent is gone (crashed, being restarted): the UI takes over again.
            Volatile.Write(ref agentRunning, false);
            Assert.True(await WaitFor(() => tracker.GetForegroundPid() == Environment.ProcessId));
        }
        finally
        {
            await reporter.StopAsync(CancellationToken.None);
            reporter.Dispose();
            server.Stop();
        }
    }

    // ---------- The agent executable ----------

    private static string AgentPath => Path.Combine(AppContext.BaseDirectory, SessionAgentInstance.ExecutableName);

    [Fact]
    public void AgentIsBuiltNextToTheEngine()
    {
        Assert.True(File.Exists(AgentPath), $"{AgentPath} missing: the engine references the agent project so it ships with it");
    }

    [Fact]
    public void AgentExitsAtOnce_WhenAnotherAlreadyRunsInTheSession()
    {
        // The run's private mutex name, which the agent inherits through the environment.
        using var claim = SessionAgentInstance.TryClaim();
        Assert.NotNull(claim);

        using var agent = Process.Start(new ProcessStartInfo(AgentPath) { UseShellExecute = false })!;
        try
        {
            Assert.True(agent.WaitForExit(15_000), "the second agent kept running");
            Assert.Equal(SessionAgentInstance.AlreadyRunningExitCode, agent.ExitCode);
        }
        finally
        {
            if (!agent.HasExited) agent.Kill();
        }
    }

    [Fact]
    public void Agent_KeepsRunning_AndHoldsTheSession()
    {
        using var agent = Process.Start(new ProcessStartInfo(AgentPath) { UseShellExecute = false })!;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => SessionAgentInstance.IsRunning(), 15_000), "the agent never claimed the session");
            Assert.False(agent.WaitForExit(500));
            Assert.Null(SessionAgentInstance.TryClaim());
        }
        finally
        {
            agent.Kill();
            agent.WaitForExit(5_000);
        }
        Assert.True(SpinWait.SpinUntil(() => !SessionAgentInstance.IsRunning(), 5_000));
    }
}
