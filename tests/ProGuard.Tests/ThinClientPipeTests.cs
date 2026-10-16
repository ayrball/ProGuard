using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Native;
using ProGuard.Engine.IPC;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

/// <summary>
/// The thin client's own transport, <see cref="EngineClient"/>, against a real <see cref="PipeServer"/> on a private
/// pipe name: the engine identifies the real account at the other end and authorizes with its real token.
/// </summary>
public class ThinClientPipeTests
{
    private static bool RunningElevated()
    {
        using var self = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(self).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static SecurityIdentifier Sid(WellKnownSidType type) => new(type, null);

    private static PipeServer StartServer(ThinHarness h, out string pipeName, PipeSecurity? security = null, Func<NamedPipeServerStream, IIpcCaller?>? identify = null)
    {
        pipeName = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        var server = new PipeServer(NullLogger<PipeServer>.Instance, h.Dispatcher)
        {
            PipeName = pipeName,
            Security = security ?? PipeServer.CreateSecurity(),
            IdentifyCaller = identify ?? PipeCaller.Identify,
        };
        server.Start();
        return server;
    }

    private static EngineClient Client(string pipeName) => new(pipeName, connectTimeout: TimeSpan.FromSeconds(5));

    /// <summary>A process this user owns that does nothing for a couple of minutes.</summary>
    private static Process StartIdleProcess()
    {
        var psi = new ProcessStartInfo("ping.exe", "-n 121 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        var p = Process.Start(psi)!;
        // A child inherits BelowNormal/Idle from its parent, and Process Lasso may have restrained this test host.
        p.PriorityClass = ProcessPriorityClass.Normal;
        return p;
    }

    [Fact]
    public async Task UiClient_Connects_WithNothingButTheInteractiveUsersAce()
    {
        // No ACE for this account or Administrators: this proves an unelevated UI gets in on the INTERACTIVE rule alone.
        var interactive = PipeServer.CreateSecurity().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()
            .Single(r => r.IdentityReference.Equals(Sid(WellKnownSidType.InteractiveSid)));
        var security = new PipeSecurity();
        security.AddAccessRule(interactive);

        var h = new ThinHarness();
        var server = StartServer(h, out var pipeName, security);
        try
        {
            await using var client = Client(pipeName);

            Assert.Equal(MessageType.Pong, (await client.SendAsync(new IpcMessage { Type = MessageType.Ping })).Type);
            await client.CommandAsync(CommandTypes.EnablePerformanceMode);

            h.Power.Verify(p => p.EnablePerformanceMode(true), Times.Once);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task RealCaller_MayActOnItsOwnProcess()
    {
        var h = new ThinHarness();
        var server = StartServer(h, out var pipeName);
        using var child = StartIdleProcess();
        try
        {
            await using var client = Client(pipeName);

            await client.CommandAsync(CommandTypes.TerminateProcess, child.Id);

            h.Controller.Verify(p => p.Terminate(child.Id), Times.Once);
        }
        finally
        {
            server.Stop();
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task RealCaller_IsRefused_OnASystemProcess_UnlessElevated()
    {
        using var services = Process.GetProcessesByName("services").First();
        var h = new ThinHarness();
        var server = StartServer(h, out var pipeName);
        try
        {
            await using var client = Client(pipeName);

            if (RunningElevated())
            {
                await client.CommandAsync(CommandTypes.SetPriority, services.Id, "BelowNormal");
                return;
            }

            var refused = await Assert.ThrowsAsync<EngineCommandException>(() =>
                client.CommandAsync(CommandTypes.SetPriority, services.Id, "BelowNormal"));
            Assert.True(refused.IsAccessDenied);
            using var self = WindowsIdentity.GetCurrent();
            Assert.Contains(self.Name, refused.Message);
            h.Controller.Verify(p => p.SetPriority(It.IsAny<int>(), It.IsAny<ProcessPriorityClass>()), Times.Never);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task Snapshot_OverThePipe_CarriesTheRealCallersRights()
    {
        var h = new ThinHarness();
        using var child = StartIdleProcess();
        var info = h.Process(child.Id, "ping", start: child.StartTime.ToUniversalTime());
        info.FilePath = @"C:\Windows\System32\PING.EXE";
        var server = StartServer(h, out var pipeName);
        try
        {
            await using var client = Client(pipeName);

            var snapshot = await client.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot);

            Assert.Equal(RunningElevated(), snapshot.CallerIsAdministrator);
            // The caller owns this process, so its path is shown whatever their rights.
            Assert.Equal(@"C:\Windows\System32\PING.EXE", snapshot.Processes.Single(p => p.Pid == child.Id).FilePath);
        }
        finally
        {
            server.Stop();
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task PipeCaller_FilterOpenable_AgreesWithCanOpenProcess()
    {
        var h = new ThinHarness();
        PipeCaller? caller = null;
        var server = StartServer(h, out var pipeName, identify: pipe => caller = PipeCaller.Identify(pipe));
        using var child = StartIdleProcess();
        try
        {
            await using var client = Client(pipeName);
            await client.SendAsync(new IpcMessage { Type = MessageType.Ping }); // the engine can only impersonate after reading
            Assert.NotNull(caller);

            using var self = Process.GetCurrentProcess();
            using var services = Process.GetProcessesByName("services").First();
            var start = child.StartTime.ToUniversalTime();
            var list = new (int Pid, DateTime StartTimeUtc)[]
            {
                (child.Id, start), (self.Id, self.StartTime.ToUniversalTime()), (services.Id, DateTime.MinValue), (4, DateTime.MinValue), (int.MaxValue - 5, DateTime.MinValue),
            };
            const ProcessAccessFlags Access = ProcessAccessFlags.QueryLimitedInformation;

            var batch = caller!.FilterOpenable(list, Access);

            foreach (var (pid, _) in list)
                Assert.Equal(caller.CanOpenProcess(pid, Access), batch.Contains(pid));
            Assert.Contains(child.Id, batch);
            Assert.DoesNotContain(int.MaxValue - 5, batch);
        }
        finally
        {
            server.Stop();
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void TheEngineHost_WiresEveryServiceTheDispatcherNeeds()
    {
        using var host = ProGuard.Engine.Program.CreateHostBuilder(Array.Empty<string>()).Build();

        var dispatcher = host.Services.GetRequiredService<IpcDispatcher>();
        var reply = dispatcher.Dispatch(new CommandMessage { CommandType = CommandTypes.GetPower });
        var result = System.Text.Json.JsonSerializer.Deserialize<CommandResult>(reply.Payload)!;

        Assert.True(result.Success, result.Error);
        Assert.NotNull(host.Services.GetRequiredService<ProGuard.Engine.Services.SnapshotBuilder>());

        // CPU tuning is wired in too. A read: the machine's power plans are left as they are.
        reply = dispatcher.Dispatch(new CommandMessage { CommandType = CommandTypes.GetCpuTuning });
        result = System.Text.Json.JsonSerializer.Deserialize<CommandResult>(reply.Payload)!;
        Assert.True(result.Success, result.Error);
    }
}
