using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Shared.Messages;
using ProGuard.UI.IPC;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>
/// The UI's <see cref="EngineProxy"/> talks to a real <see cref="PipeServer"/> (on a unique pipe name, so a running
/// engine is never involved), and the Performance Mode toggle only falls back to the UI process when the engine
/// can't be reached.
/// </summary>
public class EngineProxyTests
{
    private sealed class Engine : IDisposable
    {
        public Mock<IRuleStore> RuleStore { get; } = new();
        public Mock<IProBalanceEngine> ProBalance { get; } = new();
        public Mock<IPowerManager> Power { get; } = new();
        public Mock<ISmartTrimService> SmartTrim { get; } = new();
        public Mock<ICpuLimiter> CpuLimiter { get; } = new();
        public Mock<IProcessController> Processes { get; } = new();
        public Mock<IProcessMonitor> Monitor { get; } = new();

        public AppSettings Stored { get; private set; } = new() { Id = 1 };
        public string PipeName { get; } = "ProGuardTest_" + Guid.NewGuid().ToString("N");

        private readonly PipeServer _server;

        public Engine()
        {
            RuleStore.Setup(s => s.GetSettings()).Returns(() => Stored);
            RuleStore.Setup(s => s.UpdateSettings(It.IsAny<AppSettings>())).Callback<AppSettings>(s => Stored = s);
            RuleStore.Setup(s => s.GetProcessRules()).Returns(Array.Empty<ProcessRule>());
            Monitor.Setup(m => m.GetAllProcesses()).Returns(new[] { new ProcessInfo { Pid = 1 }, new ProcessInfo { Pid = 2 } });

            var applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, Monitor.Object, SmartTrim.Object, Power.Object, ProBalance.Object);
            var dispatcher = new IpcDispatcher(NullLogger<IpcDispatcher>.Instance, RuleStore.Object, applier, ProBalance.Object,
                Power.Object, SmartTrim.Object, CpuLimiter.Object, Processes.Object, Monitor.Object);
            // These tests are about routing, so the client is trusted; CommandPipeSecurityTests covers authorization.
            _server = new PipeServer(NullLogger<PipeServer>.Instance, dispatcher) { PipeName = PipeName, IdentifyCaller = _ => IpcCaller.InProcess };
            _server.Start();
        }

        public EngineProxy CreateProxy() => new(new PipeClient(PipeName) { ConnectTimeout = TimeSpan.FromSeconds(5) });

        public void Dispose() => _server.Stop();
    }

    private static PipeClient UnreachableClient() =>
        new("ProGuardTest_" + Guid.NewGuid().ToString("N")) { ConnectTimeout = TimeSpan.FromMilliseconds(100) };

    [Fact]
    public async Task ProcessCommands_ReachTheEngineServices()
    {
        using var engine = new Engine();
        using var proxy = engine.CreateProxy();

        Assert.True(await proxy.PingAsync());
        Assert.True(proxy.IsConnected);

        Assert.True((await proxy.SetPriorityAsync(1234, ProcessPriorityClass.BelowNormal))!.Success);
        engine.Processes.Verify(p => p.SetPriority(1234, ProcessPriorityClass.BelowNormal), Times.Once);

        Assert.True((await proxy.SetAffinityAsync(1234, 0xF0))!.Success);
        engine.Processes.Verify(p => p.SetAffinity(1234, 0xF0), Times.Once);

        Assert.True((await proxy.SetCpuLimitAsync(1234, 12.5))!.Success);
        engine.CpuLimiter.Verify(c => c.AddLimit(1234, 12.5), Times.Once);

        Assert.True((await proxy.RemoveCpuLimitAsync(1234))!.Success);
        engine.CpuLimiter.Verify(c => c.RemoveLimit(1234), Times.Once);

        Assert.True((await proxy.SuspendProcessAsync(1234))!.Success);
        Assert.True((await proxy.ResumeProcessAsync(1234))!.Success);
        Assert.True((await proxy.TerminateProcessAsync(1234))!.Success);
        engine.Processes.Verify(p => p.Suspend(1234), Times.Once);
        engine.Processes.Verify(p => p.Resume(1234), Times.Once);
        engine.Processes.Verify(p => p.Terminate(1234), Times.Once);

        Assert.True((await proxy.TrimWorkingSetAsync())!.Success);
        Assert.True((await proxy.TrimWorkingSetAsync(1234))!.Success);
        engine.SmartTrim.Verify(t => t.TrimMemory(), Times.Once);
        engine.SmartTrim.Verify(t => t.TrimProcess(1234), Times.Once);
    }

    [Fact]
    public async Task SettingsCommands_AreSavedAndApplied()
    {
        using var engine = new Engine();
        using var proxy = engine.CreateProxy();

        Assert.True((await proxy.SetSensitivityAsync(3))!.Success);
        Assert.Equal(3, engine.Stored.ProBalanceSensitivity);
        engine.ProBalance.Verify(p => p.SetSensitivity(3), Times.Once);

        Assert.True((await proxy.SetProBalanceEnabledAsync(false))!.Success);
        Assert.False(engine.Stored.ProBalanceEnabled);

        var plan = Guid.NewGuid().ToString();
        Assert.True((await proxy.ConfigureIdleSaverAsync(new IdleSaverArgument { Enabled = true, TimeoutMinutes = 7, PlanGuid = plan }))!.Success);
        Assert.True(engine.Stored.IdleSaverEnabled);
        Assert.Equal(7, engine.Stored.IdleTimeoutMinutes);
        Assert.Equal(plan, engine.Stored.IdlePowerPlanGuid);

        Assert.True((await proxy.ConfigureSmartTrimAsync(new SmartTrimArgument { Enabled = true, IntervalSeconds = 60, ThresholdMb = 256 }))!.Success);
        Assert.True(engine.Stored.SmartTrimEnabled);
        Assert.Equal(60, engine.Stored.SmartTrimIntervalSeconds);
        Assert.Equal(256, engine.Stored.SmartTrimMemoryThresholdMB);
    }

    [Fact]
    public async Task EngineRejection_IsReportedAsAFailedResult()
    {
        using var engine = new Engine();
        using var proxy = engine.CreateProxy();

        var result = await proxy.SetSensitivityAsync(11);
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("between 1 and 10", result.Error);

        // The connection is still usable after a failed command.
        Assert.True(await proxy.PingAsync());
    }

    [Fact]
    public async Task GetStatus_ReturnsTheEngineStatus()
    {
        using var engine = new Engine();
        using var proxy = engine.CreateProxy();
        engine.ProBalance.Setup(p => p.ActiveInterventions).Returns(4);

        var status = await proxy.GetStatusAsync();

        Assert.NotNull(status);
        Assert.True(status!.IsRunning);
        Assert.Equal(4, status.ActiveInterventions);
        Assert.Equal(2, status.ProcessCount);
    }

    [Fact]
    public async Task ConcurrentRequests_EachGetTheirOwnReply()
    {
        using var engine = new Engine();
        using var proxy = engine.CreateProxy();

        var calls = new List<Task<CommandResult?>>();
        for (var pid = 100; pid < 120; pid++) calls.Add(proxy.SetCpuLimitAsync(pid, 50));
        calls.Add(proxy.SetSensitivityAsync(0)); // rejected: must not be confused with the others' replies
        var results = await Task.WhenAll(calls);

        for (var i = 0; i < 20; i++) Assert.True(results[i]!.Success);
        Assert.False(results[20]!.Success);
        engine.CpuLimiter.Verify(c => c.AddLimit(It.IsAny<int>(), 50), Times.Exactly(20));
    }

    [Fact]
    public async Task NoEngine_CommandsReturnNull()
    {
        using var proxy = new EngineProxy(UnreachableClient());

        Assert.Null(await proxy.EnablePerformanceModeAsync());
        Assert.Null(await proxy.GetStatusAsync());
        Assert.False(await proxy.PingAsync());
        Assert.False(proxy.IsConnected);
    }

    [Fact]
    public async Task EngineStartedLater_IsPickedUpByTheNextRequest()
    {
        var pipeName = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        using var client = new PipeClient(pipeName) { ConnectTimeout = TimeSpan.FromMilliseconds(100) };
        Assert.Null(await client.SendAsync(new IpcMessage { Type = MessageType.Ping }));

        using var server = new OneReplyPerConnectionServer(pipeName);
        Assert.Equal(MessageType.Pong, (await client.SendAsync(new IpcMessage { Type = MessageType.Ping }))?.Type);
    }

    [Fact]
    public async Task ConnectionClosedByEngine_IsReopenedTransparently()
    {
        var pipeName = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        using var server = new OneReplyPerConnectionServer(pipeName);
        var changes = new List<bool>();
        using var client = new PipeClient(pipeName) { ConnectTimeout = TimeSpan.FromSeconds(5) };
        client.ConnectionChanged += (_, connected) => { lock (changes) changes.Add(connected); };

        // The server hangs up after every reply, like an engine restarting between two of the UI's requests.
        for (var i = 0; i < 3; i++)
        {
            var reply = await client.SendAsync(new IpcMessage { Type = MessageType.Ping });
            Assert.Equal(MessageType.Pong, reply?.Type);
            Assert.True(await server.Closed.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        lock (changes) Assert.Equal(new[] { true, false, true, false, true }, changes);
    }

    [Fact]
    public async Task UnansweredRequest_TimesOutAndDropsTheConnection()
    {
        var pipeName = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();

        using var client = new PipeClient(pipeName)
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ResponseTimeout = TimeSpan.FromMilliseconds(200)
        };
        var watch = Stopwatch.StartNew();
        var send = client.SendAsync(new IpcMessage { Type = MessageType.Ping });
        await accepted;
        // Read the request so the client is left waiting for a reply that never comes.
        var request = await new StreamReader(server).ReadLineAsync();
        var reply = await send;

        Assert.NotNull(request);
        Assert.Null(reply);
        Assert.False(client.IsConnected);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task UnreadRequest_TimesOut()
    {
        // A hung engine never reads; the pipe has no buffer, so the write itself has to time out.
        var pipeName = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();

        using var client = new PipeClient(pipeName)
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ResponseTimeout = TimeSpan.FromMilliseconds(200)
        };
        var send = client.SendAsync(new CommandMessage { CommandType = CommandTypes.SetSensitivity, Argument = new string('x', 64 * 1024) });
        await accepted;

        Assert.Null(await send.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(client.IsConnected);
    }

    // ---------- Performance Mode toggle ----------

    [Fact]
    public async Task PerformanceModeToggle_GoesThroughTheEngine_WhenConnected()
    {
        using var engine = new Engine();
        var local = new List<bool>();
        var vm = new PowerViewModel(engine.CreateProxy(), local.Add);

        vm.IsPerformanceModeEnabled = true;
        await vm.PerformanceModeChange;
        vm.IsPerformanceModeEnabled = false;
        await vm.PerformanceModeChange;

        engine.Power.Verify(p => p.EnablePerformanceMode(true), Times.Once);
        engine.Power.Verify(p => p.EnablePerformanceMode(false), Times.Once);
        Assert.Empty(local);
    }

    [Fact]
    public async Task PerformanceModeToggle_FallsBackToTheUiProcess_WhenTheEngineIsDown()
    {
        var local = new List<bool>();
        var vm = new PowerViewModel(new EngineProxy(UnreachableClient()), local.Add);

        vm.IsPerformanceModeEnabled = true;
        await vm.PerformanceModeChange;
        vm.IsPerformanceModeEnabled = false;
        await vm.PerformanceModeChange;

        Assert.Equal(new[] { true, false }, local);
    }

    [Fact]
    public async Task PerformanceModeToggle_DoesNotFallBack_WhenTheEngineRefuses()
    {
        using var engine = new Engine();
        engine.Power.Setup(p => p.EnablePerformanceMode(true)).Throws(new InvalidOperationException("no plan"));
        var local = new List<bool>();
        var vm = new PowerViewModel(engine.CreateProxy(), local.Add);

        vm.IsPerformanceModeEnabled = true;
        await vm.PerformanceModeChange;

        engine.Power.Verify(p => p.EnablePerformanceMode(true), Times.Once);
        Assert.Empty(local);
    }

    /// <summary>A server that answers one request per connection and then hangs up, accepting new connections until disposed.</summary>
    private sealed class OneReplyPerConnectionServer : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        /// <summary>Released each time the server has closed a connection.</summary>
        public SemaphoreSlim Closed { get; } = new(0);

        public OneReplyPerConnectionServer(string pipeName)
        {
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        try
                        {
                            await pipe.WaitForConnectionAsync(_stop.Token);
                            var reader = new StreamReader(pipe);
                            var writer = new StreamWriter(pipe) { AutoFlush = true };
                            if (await reader.ReadLineAsync(_stop.Token) != null)
                            {
                                await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcMessage { Type = MessageType.Pong, Payload = "{}" }));
                                pipe.WaitForPipeDrain();
                            }
                        }
                        finally
                        {
                            pipe.Dispose();
                            Closed.Release();
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { /* client went away; accept the next one */ }
                }
            });
        }

        public void Dispose() => _stop.Cancel();
    }
}
