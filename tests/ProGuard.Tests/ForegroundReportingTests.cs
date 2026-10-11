using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;

namespace ProGuard.Tests;

/// <summary>
/// The engine runs as a session-0 service and can't see the user's desktop, so the UI reports the foreground
/// process over <see cref="PipeNames.Foreground"/>.
/// </summary>
public class ForegroundReportingTests
{
    private const int ConsoleSession = 1;

    /// <summary>PIDs 1xx live in session 1, 2xx in session 2; anything else is session 0.</summary>
    private static ForegroundTracker Tracker(int localPid = 0, bool canSeeDesktop = false, int console = ConsoleSession) =>
        new(null, () => localPid, pid => pid / 100 is 1 or 2 ? pid / 100 : 0, () => console, canSeeDesktop);

    // ---------- ForegroundTracker ----------

    [Fact]
    public void NoReporters_InSession0_KnowsNoForeground()
    {
        Assert.Equal(0, Tracker(localPid: 123, canSeeDesktop: false).GetForegroundPid());
    }

    [Fact]
    public void NoReporters_InAUserSession_AsksWindowsDirectly()
    {
        Assert.Equal(123, Tracker(localPid: 123, canSeeDesktop: true).GetForegroundPid());
    }

    [Fact]
    public void Report_IsUsed_AndOverridesTheLocalView()
    {
        var t = Tracker(localPid: 150, canSeeDesktop: true);
        Assert.True(t.Report(connectionId: 1, clientSessionId: 1, pid: 101));
        Assert.Equal(101, t.GetForegroundPid());

        Assert.True(t.Report(1, 1, 0));         // desktop locked / nothing focused
        Assert.Equal(0, t.GetForegroundPid());
    }

    [Theory]
    [InlineData(201)]   // another user's process
    [InlineData(4)]     // System, session 0
    [InlineData(-5)]
    public void Report_OfAProcessOutsideTheReportersSession_IsRejected(int pid)
    {
        var t = Tracker();
        Assert.False(t.Report(1, 1, pid));
        Assert.False(t.HasReporters);
        Assert.Equal(0, t.GetForegroundPid());
    }

    [Fact]
    public void ConsoleSession_WinsOverAMoreRecentRemoteSession()
    {
        var t = Tracker(console: 1);
        t.Report(1, 1, 101);
        t.Report(2, 2, 201);
        Assert.Equal(101, t.GetForegroundPid());

        var away = Tracker(console: 7);          // console on a session with no reporter: most recent wins
        away.Report(1, 1, 101);
        away.Report(2, 2, 201);
        Assert.Equal(201, away.GetForegroundPid());
    }

    [Fact]
    public void MostRecentReport_WinsWithinASession()
    {
        var t = Tracker();
        t.Report(1, 1, 101);
        t.Report(2, 1, 102);
        Assert.Equal(102, t.GetForegroundPid());
        t.Report(1, 1, 103);
        Assert.Equal(103, t.GetForegroundPid());
    }

    [Fact]
    public void Disconnect_DropsThatReporter_AndFallsBack()
    {
        var t = Tracker(localPid: 150, canSeeDesktop: true);
        t.Report(1, 1, 101);
        t.Report(2, 1, 102);

        t.Disconnect(2);
        Assert.Equal(101, t.GetForegroundPid());
        t.Disconnect(1);
        Assert.False(t.HasReporters);
        Assert.Equal(150, t.GetForegroundPid());
    }

    [Fact]
    public void ProcessMonitor_TakesTheForegroundFromTheTracker()
    {
        var tracker = new Mock<IForegroundTracker>();
        tracker.Setup(t => t.GetForegroundPid()).Returns(4242);
        using var monitor = new ProcessMonitor(NullLogger<ProcessMonitor>.Instance, tracker.Object);

        Assert.Equal(4242, monitor.GetForegroundPid());
    }

    // ---------- Over the pipe ----------

    private static string UniquePipe() => "ProGuardTest_Fg_" + Guid.NewGuid().ToString("N");

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

    /// <summary>A real tracker that sees nothing locally, so any non-zero answer came over the pipe.</summary>
    private static ForegroundTracker RealTrackerBlindLocally() =>
        new(null, () => 0, pid => System.Diagnostics.Process.GetProcessById(pid).SessionId, () => -1, canSeeDesktop: false);

    private static async Task<(NamedPipeClientStream Pipe, StreamWriter Writer)> ConnectWriter(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(cts.Token);
        return (pipe, new StreamWriter(pipe) { AutoFlush = true });
    }

    [Fact]
    public async Task UiReporter_DeliversTheForegroundPid_AndTheEngineForgetsItOnDisconnect()
    {
        var tracker = RealTrackerBlindLocally();
        var pipeName = UniquePipe();
        var server = new ForegroundPipeServer(NullLogger<ForegroundPipeServer>.Instance, tracker) { PipeName = pipeName };
        server.Start();
        var reporter = new ForegroundReporter(pipeName, () => Environment.ProcessId);
        try
        {
            await reporter.StartAsync(CancellationToken.None);
            Assert.True(await WaitFor(() => tracker.GetForegroundPid() == Environment.ProcessId));

            await reporter.StopAsync(CancellationToken.None);
            reporter.Dispose();
            Assert.True(await WaitFor(() => !tracker.HasReporters));
            Assert.Equal(0, tracker.GetForegroundPid());
        }
        finally
        {
            await reporter.StopAsync(CancellationToken.None);
            server.Stop();
        }
    }

    [Fact]
    public async Task UiReporter_WithNoEngine_KeepsRetryingQuietly_AndStops()
    {
        var reporter = new ForegroundReporter(UniquePipe(), () => 1);
        await reporter.StartAsync(CancellationToken.None);
        await Task.Delay(1500);
        await reporter.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        reporter.Dispose();
    }

    [Fact]
    public async Task UnprivilegedRights_AreEnoughForAWriteOnlyClient()
    {
        // Only "Authenticated Users: ReadWrite | Synchronize" - exactly what an unelevated UI gets from
        // CreateSecurity(). No ACE for this account or Administrators, so this proves those rights suffice.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        var tracker = RealTrackerBlindLocally();
        var pipeName = UniquePipe();
        var server = new ForegroundPipeServer(NullLogger<ForegroundPipeServer>.Instance, tracker) { PipeName = pipeName, Security = security };
        server.Start();
        try
        {
            var (pipe, writer) = await ConnectWriter(pipeName);
            using (pipe)
            using (writer)
            {
                await writer.WriteLineAsync(ForegroundReporter.Serialize(Environment.ProcessId));
                Assert.True(await WaitFor(() => tracker.GetForegroundPid() == Environment.ProcessId));
            }
        }
        finally { server.Stop(); }
    }

    [Fact]
    public void DefaultSecurity_GivesUsersNoRightToCreatePipeInstances()
    {
        var rules = ForegroundPipeServer.CreateSecurity().GetAccessRules(true, false, typeof(SecurityIdentifier));
        var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var userRights = rules.Cast<PipeAccessRule>().Where(r => r.IdentityReference.Equals(users)).Single().PipeAccessRights;

        Assert.True(userRights.HasFlag(PipeAccessRights.WriteData));
        Assert.False(userRights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.False(userRights.HasFlag(PipeAccessRights.ChangePermissions));
    }

    [Fact]
    public async Task Pipe_IgnoresAnythingButForegroundReports()
    {
        var tracker = RealTrackerBlindLocally();
        var pipeName = UniquePipe();
        var server = new ForegroundPipeServer(NullLogger<ForegroundPipeServer>.Instance, tracker) { PipeName = pipeName };
        server.Start();
        try
        {
            var (pipe, writer) = await ConnectWriter(pipeName);
            using (pipe)
            using (writer)
            {
                await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new CommandMessage
                {
                    CommandType = CommandTypes.TerminateProcess,
                    TargetPid = Environment.ProcessId
                }));
                await writer.WriteLineAsync(ForegroundReporter.Serialize(Environment.ProcessId));
                Assert.True(await WaitFor(() => tracker.GetForegroundPid() == Environment.ProcessId));
            }
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task Pipe_DropsAClientThatSendsAnOverlongLine()
    {
        var tracker = RealTrackerBlindLocally();
        var pipeName = UniquePipe();
        var server = new ForegroundPipeServer(NullLogger<ForegroundPipeServer>.Instance, tracker) { PipeName = pipeName };
        server.Start();
        try
        {
            var (pipe, writer) = await ConnectWriter(pipeName);
            try
            {
                await writer.WriteLineAsync(ForegroundReporter.Serialize(Environment.ProcessId));
                Assert.True(await WaitFor(() => tracker.HasReporters));

                // No newline, ever: the server must give up on the line and hang up.
                for (var i = 0; i < 20 && tracker.HasReporters; i++)
                    await writer.WriteAsync(new string('x', ForegroundPipeServer.MaxLineLength));
            }
            catch (IOException) { /* the server hung up on us mid-write */ }
            finally
            {
                try { writer.Dispose(); } catch (IOException) { }
                pipe.Dispose();
            }

            Assert.True(await WaitFor(() => !tracker.HasReporters));
        }
        finally { server.Stop(); }
    }
}
