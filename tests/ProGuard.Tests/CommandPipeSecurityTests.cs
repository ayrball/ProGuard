using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Shared.Messages;
using ProGuard.UI.IPC;

namespace ProGuard.Tests;

/// <summary>
/// The engine's command pipe is open to the unelevated UI, so its ACL must stop at interactive users, and each
/// request is authorized against the caller: a standard user may change settings but may only act on processes they
/// could open themselves.
/// </summary>
public class CommandPipeSecurityTests
{
    private sealed class FakeCaller : IIpcCaller
    {
        public string Name { get; init; } = @"TEST\standard";
        public bool IsAdministrator { get; init; }
        public HashSet<int> OpenablePids { get; } = new();
        public List<(int Pid, ProcessAccessFlags Access)> Checks { get; } = new();

        public bool CanOpenProcess(int pid, ProcessAccessFlags access)
        {
            Checks.Add((pid, access));
            return OpenablePids.Contains(pid);
        }
    }

    private sealed class Harness
    {
        public Mock<IRuleStore> RuleStore { get; } = new();
        public Mock<IProBalanceEngine> ProBalance { get; } = new();
        public Mock<IPowerManager> Power { get; } = new();
        public Mock<ISmartTrimService> SmartTrim { get; } = new();
        public Mock<ICpuLimiter> CpuLimiter { get; } = new();
        public Mock<IProcessController> Processes { get; } = new();
        public Mock<IProcessMonitor> Monitor { get; } = new();
        public AppSettings Stored { get; private set; } = new() { Id = 1 };
        public IpcDispatcher Dispatcher { get; }

        public Harness()
        {
            RuleStore.Setup(s => s.GetSettings()).Returns(() => Stored);
            RuleStore.Setup(s => s.UpdateSettings(It.IsAny<AppSettings>())).Callback<AppSettings>(s => Stored = s);
            RuleStore.Setup(s => s.GetProcessRules()).Returns(Array.Empty<ProcessRule>());
            Monitor.Setup(m => m.GetAllProcesses()).Returns(Array.Empty<ProcessInfo>());

            var applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, Monitor.Object, SmartTrim.Object, Power.Object, ProBalance.Object);
            Dispatcher = new IpcDispatcher(NullLogger<IpcDispatcher>.Instance, RuleStore.Object, applier, ProBalance.Object,
                Power.Object, SmartTrim.Object, CpuLimiter.Object, Processes.Object, Monitor.Object);
        }

        public IpcMessage Send(IIpcCaller caller, CommandMessage request) => Dispatcher.Dispatch(request, caller);

        public CommandResult Command(IIpcCaller caller, string command, int? pid = null, string? argument = null)
        {
            var reply = Send(caller, new CommandMessage { CommandType = command, TargetPid = pid, Argument = argument });
            Assert.Equal(MessageType.CommandResult, reply.Type);
            return JsonSerializer.Deserialize<CommandResult>(reply.Payload)!;
        }

        /// <summary>Whether anything was asked to act on a process.</summary>
        public bool ProcessesTouched =>
            Processes.Invocations.Count + CpuLimiter.Invocations.Count + SmartTrim.Invocations.Count(i => i.Method.Name == nameof(ISmartTrimService.TrimProcess)) > 0;
    }

    private const int Pid = 4321;

    // ---------- Pipe ACL ----------

    private static List<PipeAccessRule> DefaultRules() =>
        PipeServer.CreateSecurity().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

    private static SecurityIdentifier Sid(WellKnownSidType type) => new(type, null);

    [Fact]
    public void DefaultSecurity_LetsInteractiveUsersReadAndWrite_ButNotCreateInstancesOrChangeTheAcl()
    {
        var rule = DefaultRules().Single(r => r.IdentityReference.Equals(Sid(WellKnownSidType.InteractiveSid)));

        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.True(rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadData));
        Assert.True(rule.PipeAccessRights.HasFlag(PipeAccessRights.WriteData));
        Assert.False(rule.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.False(rule.PipeAccessRights.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.False(rule.PipeAccessRights.HasFlag(PipeAccessRights.TakeOwnership));
    }

    [Fact]
    public void DefaultSecurity_DeniesNetworkLogons()
    {
        var rule = DefaultRules().Single(r => r.IdentityReference.Equals(Sid(WellKnownSidType.NetworkSid)));

        Assert.Equal(AccessControlType.Deny, rule.AccessControlType);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights & PipeAccessRights.FullControl);
    }

    [Fact]
    public void DefaultSecurity_GivesSystemAdministratorsAndTheEngineAccountFullControl()
    {
        var rules = DefaultRules();
        using var self = WindowsIdentity.GetCurrent();
        foreach (var sid in new[] { Sid(WellKnownSidType.LocalSystemSid), Sid(WellKnownSidType.BuiltinAdministratorsSid), self.User! })
        {
            var rule = rules.Single(r => r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow);
            Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights & PipeAccessRights.FullControl);
        }
    }

    [Fact]
    public void DefaultSecurity_GrantsNothingToEveryoneOrAnonymous()
    {
        var rules = DefaultRules();
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(Sid(WellKnownSidType.WorldSid)));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(Sid(WellKnownSidType.AnonymousSid)));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(Sid(WellKnownSidType.AuthenticatedUserSid)));
    }

    // ---------- Authorization decisions ----------

    public static IEnumerable<object?[]> UnrestrictedCommands() => new[]
    {
        new object?[] { CommandTypes.EnableProBalance, null },
        new object?[] { CommandTypes.DisableProBalance, null },
        new object?[] { CommandTypes.SetSensitivity, "3" },
        new object?[] { CommandTypes.ConfigureIdleSaver, JsonSerializer.Serialize(new IdleSaverArgument { Enabled = true, TimeoutMinutes = 5 }) },
        new object?[] { CommandTypes.ConfigureSmartTrim, JsonSerializer.Serialize(new SmartTrimArgument { Enabled = true }) },
        new object?[] { CommandTypes.EnablePerformanceMode, null },
        new object?[] { CommandTypes.DisablePerformanceMode, null },
        new object?[] { CommandTypes.TrimWorkingSet, null },
    };

    [Theory]
    [MemberData(nameof(UnrestrictedCommands))]
    public void StandardUser_MayChangeSettingsAndPerformanceMode(string command, string? argument)
    {
        var h = new Harness();
        var caller = new FakeCaller();

        var result = h.Command(caller, command, argument: argument);

        Assert.True(result.Success, result.Error);
        Assert.Empty(caller.Checks);
    }

    [Fact]
    public void StandardUser_MayPingGetStatusAndReplaceSettings()
    {
        var h = new Harness();
        var caller = new FakeCaller();

        Assert.Equal(MessageType.Pong, h.Send(caller, new CommandMessage { Type = MessageType.Ping }).Type);
        Assert.Equal(MessageType.Status, h.Send(caller, new CommandMessage { Type = MessageType.GetStatus }).Type);

        var reply = h.Send(caller, new CommandMessage { Type = MessageType.SetSetting, Payload = JsonSerializer.Serialize(new AppSettings { ProBalanceSensitivity = 7 }) });
        Assert.True(JsonSerializer.Deserialize<CommandResult>(reply.Payload)!.Success);
        Assert.Equal(7, h.Stored.ProBalanceSensitivity);
    }

    public static IEnumerable<object?[]> ProcessCommands() => new[]
    {
        new object?[] { CommandTypes.SetPriority, "BelowNormal", ProcessAccessFlags.SetInformation },
        new object?[] { CommandTypes.SetAffinity, "0x3", ProcessAccessFlags.SetInformation },
        new object?[] { CommandTypes.SetCpuLimit, "25", ProcessAccessFlags.SetQuota | ProcessAccessFlags.Terminate },
        new object?[] { CommandTypes.RemoveCpuLimit, null, ProcessAccessFlags.SetQuota | ProcessAccessFlags.Terminate },
        new object?[] { CommandTypes.TerminateProcess, null, ProcessAccessFlags.Terminate },
        new object?[] { CommandTypes.SuspendProcess, null, ProcessAccessFlags.SuspendResume },
        new object?[] { CommandTypes.ResumeProcess, null, ProcessAccessFlags.SuspendResume },
        new object?[] { CommandTypes.TrimWorkingSet, null, ProcessAccessFlags.SetQuota | ProcessAccessFlags.QueryLimitedInformation },
    };

    [Theory]
    [MemberData(nameof(ProcessCommands))]
    public void StandardUser_MayActOnAProcessTheyCanOpen_WithTheRightsTheActionNeeds(string command, string? argument, ProcessAccessFlags access)
    {
        var h = new Harness();
        var caller = new FakeCaller();
        caller.OpenablePids.Add(Pid);

        var result = h.Command(caller, command, Pid, argument);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { (Pid, access) }, caller.Checks);
        Assert.True(h.ProcessesTouched);
    }

    [Theory]
    [MemberData(nameof(ProcessCommands))]
    public void StandardUser_IsRefused_OnAProcessTheyCannotOpen(string command, string? argument, ProcessAccessFlags access)
    {
        var h = new Harness();
        var caller = new FakeCaller();

        var result = h.Command(caller, command, Pid, argument);

        Assert.False(result.Success);
        Assert.Contains("Access denied", result.Error);
        Assert.Contains(caller.Name, result.Error);
        Assert.Contains(Pid.ToString(), result.Error);
        Assert.Equal(new[] { (Pid, access) }, caller.Checks);
        Assert.False(h.ProcessesTouched);
    }

    [Theory]
    [InlineData("RealTime")]
    [InlineData("realtime")]
    [InlineData("256")]
    public void StandardUser_MayNotSetRealtimePriority_EvenOnTheirOwnProcess(string priority)
    {
        var h = new Harness();
        var caller = new FakeCaller();
        caller.OpenablePids.Add(Pid);

        var result = h.Command(caller, CommandTypes.SetPriority, Pid, priority);

        Assert.False(result.Success);
        Assert.Contains("Realtime", result.Error);
        h.Processes.Verify(p => p.SetPriority(It.IsAny<int>(), It.IsAny<ProcessPriorityClass>()), Times.Never);
    }

    [Fact]
    public void StandardUser_MaySetHighPriority_OnTheirOwnProcess()
    {
        var h = new Harness();
        var caller = new FakeCaller();
        caller.OpenablePids.Add(Pid);

        Assert.True(h.Command(caller, CommandTypes.SetPriority, Pid, "High").Success);
        h.Processes.Verify(p => p.SetPriority(Pid, ProcessPriorityClass.High), Times.Once);
    }

    [Theory]
    [InlineData("ReticulateSplines")]
    [InlineData(CommandTypes.ReportForeground)]
    public void StandardUser_IsRefused_CommandsThePolicyDoesNotList(string command)
    {
        var h = new Harness();

        var result = h.Command(new FakeCaller(), command, Pid);

        Assert.False(result.Success);
        Assert.Contains("Access denied", result.Error);
    }

    [Fact]
    public void StandardUser_WithoutAPid_GetsTheUsualValidationError()
    {
        var h = new Harness();
        var caller = new FakeCaller();

        var result = h.Command(caller, CommandTypes.TerminateProcess);

        Assert.False(result.Success);
        Assert.Contains("needs a TargetPid", result.Error);
        Assert.Empty(caller.Checks);
    }

    [Fact]
    public void Administrator_MayActOnAnyProcess()
    {
        var h = new Harness();
        var admin = new FakeCaller { IsAdministrator = true };

        Assert.True(h.Command(admin, CommandTypes.TerminateProcess, Pid).Success);
        Assert.True(h.Command(admin, CommandTypes.SetPriority, Pid, "RealTime").Success);
        h.Processes.Verify(p => p.Terminate(Pid), Times.Once);
        Assert.Empty(admin.Checks);
    }

    [Fact]
    public void Administrator_StillCannotTerminateTheSystemProcess()
    {
        var h = new Harness();

        var result = h.Command(new FakeCaller { IsAdministrator = true }, CommandTypes.TerminateProcess, 4);

        Assert.False(result.Success);
        h.Processes.Verify(p => p.Terminate(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void Administrator_UnknownCommand_IsStillUnknown()
    {
        var result = new Harness().Command(new FakeCaller { IsAdministrator = true }, "ReticulateSplines");

        Assert.False(result.Success);
        Assert.Contains("Unknown command", result.Error);
    }

    // ---------- End to end, as this (unelevated) user ----------

    private static bool RunningElevated()
    {
        using var self = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(self).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static PipeServer StartServer(Harness h, out string pipeName, PipeSecurity? security = null)
    {
        pipeName = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        var server = security == null
            ? new PipeServer(NullLogger<PipeServer>.Instance, h.Dispatcher) { PipeName = pipeName }
            : new PipeServer(NullLogger<PipeServer>.Instance, h.Dispatcher) { PipeName = pipeName, Security = security };
        server.Start();
        return server;
    }

    private static EngineProxy Proxy(string pipeName) => new(new PipeClient(pipeName) { ConnectTimeout = TimeSpan.FromSeconds(5) });

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
        var interactive = DefaultRules().Single(r => r.IdentityReference.Equals(Sid(WellKnownSidType.InteractiveSid)));
        var security = new PipeSecurity();
        security.AddAccessRule(interactive);

        var h = new Harness();
        var server = StartServer(h, out var pipeName, security);
        try
        {
            using var proxy = Proxy(pipeName);
            Assert.True(await proxy.PingAsync());

            var result = await proxy.EnablePerformanceModeAsync();
            Assert.True(result!.Success, result.Error);
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
        var h = new Harness();
        var server = StartServer(h, out var pipeName);
        using var child = StartIdleProcess();
        try
        {
            using var proxy = Proxy(pipeName);

            var result = await proxy.TerminateProcessAsync(child.Id);

            Assert.True(result!.Success, result.Error);
            h.Processes.Verify(p => p.Terminate(child.Id), Times.Once);
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
        var h = new Harness();
        var server = StartServer(h, out var pipeName);
        try
        {
            using var proxy = Proxy(pipeName);

            var result = await proxy.SetPriorityAsync(services.Id, ProcessPriorityClass.BelowNormal);

            if (RunningElevated())
            {
                Assert.True(result!.Success, result.Error);
                return;
            }
            Assert.False(result!.Success);
            using var self = WindowsIdentity.GetCurrent();
            Assert.Contains(self.Name, result.Error);
            h.Processes.Verify(p => p.SetPriority(It.IsAny<int>(), It.IsAny<ProcessPriorityClass>()), Times.Never);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task OverlongRequest_DropsTheConnection()
    {
        var h = new Harness();
        var server = StartServer(h, out var pipeName);
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(cts.Token);
            using var reader = new StreamReader(client);
            using var writer = new StreamWriter(client) { AutoFlush = true };

            try
            {
                await writer.WriteLineAsync(new string('x', PipeServer.MaxLineLength + 1).AsMemory(), cts.Token);
                Assert.Null(await reader.ReadLineAsync(cts.Token));
            }
            catch (IOException)
            {
                // The engine hung up while the request was still being written: just as good.
            }
            Assert.False(cts.IsCancellationRequested);
        }
        finally
        {
            server.Stop();
        }
    }
}
