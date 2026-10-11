using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

/// <summary>
/// The UI's settings and commands must actually reach the engine services: IPC commands are routed to the
/// right service, and settings (from IPC or from the shared database) are applied to SmartTrim, the power
/// manager, the process monitor and ProBalance.
/// </summary>
public class IpcDispatchAndSettingsTests
{
    private sealed class Harness
    {
        public Mock<IRuleStore> RuleStore { get; } = new();
        public Mock<IProBalanceEngine> ProBalance { get; } = new();
        public Mock<IPowerManager> Power { get; } = new();
        public Mock<ISmartTrimService> SmartTrim { get; } = new();
        public Mock<ICpuLimiter> CpuLimiter { get; } = new();
        public Mock<IProcessController> Processes { get; } = new();
        public Mock<IProcessMonitor> Monitor { get; } = new();

        /// <summary>What the store currently holds; UpdateSettings replaces it like the real store does.</summary>
        public AppSettings Stored { get; private set; } = new() { Id = 1 };

        public SettingsApplier Applier { get; }
        public IpcDispatcher Dispatcher { get; }

        public Harness()
        {
            RuleStore.Setup(s => s.GetSettings()).Returns(() => Stored);
            RuleStore.Setup(s => s.UpdateSettings(It.IsAny<AppSettings>())).Callback<AppSettings>(s => Stored = s);
            RuleStore.Setup(s => s.GetProcessRules()).Returns(Array.Empty<ProcessRule>());
            Monitor.Setup(m => m.GetAllProcesses()).Returns(new[] { new ProcessInfo { Pid = 1 }, new ProcessInfo { Pid = 2 } });

            Applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, Monitor.Object, SmartTrim.Object, Power.Object, ProBalance.Object);
            Dispatcher = new IpcDispatcher(NullLogger<IpcDispatcher>.Instance, RuleStore.Object, Applier, ProBalance.Object,
                Power.Object, SmartTrim.Object, CpuLimiter.Object, Processes.Object, Monitor.Object);
        }

        public CommandResult Send(string command, int? pid = null, string? argument = null)
        {
            var reply = Dispatcher.Dispatch(new CommandMessage { CommandType = command, TargetPid = pid, Argument = argument });
            Assert.Equal(MessageType.CommandResult, reply.Type);
            return JsonSerializer.Deserialize<CommandResult>(reply.Payload)!;
        }
    }

    // ---------- Native constants ----------

    [Fact]
    public void Ntdll_ProcessIoPriority_IsTheRealProcessInfoClass()
    {
        Assert.Equal(33, Ntdll.ProcessIoPriority);
        Assert.Equal(0x1FFFFFu, (uint)ProcessAccessFlags.All);
    }

    [Fact]
    public void Ntdll_SetIoPriority_SucceedsOnOwnProcess()
    {
        // Low then back to Normal: harmless for the test host. The old value 21 is a different information class.
        using var self = Process.GetCurrentProcess();
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.SetInformation, false, self.Id);
        Assert.NotEqual(IntPtr.Zero, handle);
        try
        {
            int low = 1, normal = 2;
            Assert.Equal(0, Ntdll.NtSetInformationProcess(handle, Ntdll.ProcessIoPriority, ref low, sizeof(int)));
            Assert.Equal(0, Ntdll.NtSetInformationProcess(handle, Ntdll.ProcessIoPriority, ref normal, sizeof(int)));
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    // ---------- IPC dispatch ----------

    [Fact]
    public void Ping_RepliesPong()
    {
        var h = new Harness();
        var reply = h.Dispatcher.Dispatch(new CommandMessage { Type = MessageType.Ping });
        Assert.Equal(MessageType.Pong, reply.Type);
    }

    [Fact]
    public void GetStatus_RepliesWithEngineStatus()
    {
        var h = new Harness();
        h.ProBalance.SetupGet(p => p.ActiveInterventions).Returns(3);
        h.ProBalance.SetupGet(p => p.IsEngaged).Returns(true);

        var reply = h.Dispatcher.Dispatch(new CommandMessage { Type = MessageType.GetStatus });

        Assert.Equal(MessageType.Status, reply.Type);
        var status = JsonSerializer.Deserialize<EngineStatus>(reply.Payload)!;
        Assert.True(status.IsRunning);
        Assert.True(status.ProBalanceEngaged);
        Assert.Equal(3, status.ActiveInterventions);
        Assert.Equal(2, status.ProcessCount);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void EnablePerformanceMode_ReachesPowerManager(string? argument, bool expected)
    {
        var h = new Harness();
        Assert.True(h.Send(CommandTypes.EnablePerformanceMode, argument: argument).Success);
        h.Power.Verify(p => p.EnablePerformanceMode(expected), Times.Once);
    }

    [Fact]
    public void DisablePerformanceMode_ReachesPowerManager()
    {
        var h = new Harness();
        Assert.True(h.Send(CommandTypes.DisablePerformanceMode).Success);
        h.Power.Verify(p => p.EnablePerformanceMode(false), Times.Once);
    }

    [Fact]
    public void SetSensitivity_IsPersistedAndAppliedToProBalance()
    {
        var h = new Harness();
        Assert.True(h.Send(CommandTypes.SetSensitivity, argument: "8").Success);

        Assert.Equal(8, h.Stored.ProBalanceSensitivity);
        h.RuleStore.Verify(s => s.UpdateSettings(It.Is<AppSettings>(a => a.ProBalanceSensitivity == 8)), Times.Once);
        h.ProBalance.Verify(p => p.SetSensitivity(8), Times.Once);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("high")]
    public void SetSensitivity_RejectsInvalidValues(string argument)
    {
        var h = new Harness();
        var result = h.Send(CommandTypes.SetSensitivity, argument: argument);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
        h.RuleStore.Verify(s => s.UpdateSettings(It.IsAny<AppSettings>()), Times.Never);
        h.ProBalance.Verify(p => p.SetSensitivity(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void EnableAndDisableProBalance_ArePersistedAndApplied()
    {
        var h = new Harness();

        Assert.True(h.Send(CommandTypes.DisableProBalance).Success);
        Assert.False(h.Stored.ProBalanceEnabled);
        h.ProBalance.Verify(p => p.SetEnabled(false), Times.Once);

        Assert.True(h.Send(CommandTypes.EnableProBalance).Success);
        Assert.True(h.Stored.ProBalanceEnabled);
        h.ProBalance.Verify(p => p.SetEnabled(true), Times.Once);
    }

    [Fact]
    public void ConfigureIdleSaver_IsPersistedAndReachesPowerManager()
    {
        var h = new Harness();
        var plan = Powrprof.Balanced;
        var arg = JsonSerializer.Serialize(new IdleSaverArgument { Enabled = true, TimeoutMinutes = 20, PlanGuid = plan.ToString() });

        Assert.True(h.Send(CommandTypes.ConfigureIdleSaver, argument: arg).Success);

        Assert.True(h.Stored.IdleSaverEnabled);
        Assert.Equal(20, h.Stored.IdleTimeoutMinutes);
        Assert.Equal(plan.ToString(), h.Stored.IdlePowerPlanGuid);
        h.Power.Verify(p => p.ConfigureIdleSaver(true, 20, plan), Times.Once);
    }

    [Fact]
    public void ConfigureIdleSaver_RejectsBadPlanGuid()
    {
        var h = new Harness();
        var arg = JsonSerializer.Serialize(new IdleSaverArgument { Enabled = true, PlanGuid = "not-a-guid" });

        Assert.False(h.Send(CommandTypes.ConfigureIdleSaver, argument: arg).Success);
        h.RuleStore.Verify(s => s.UpdateSettings(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public void ConfigureSmartTrim_IsPersistedAndReachesSmartTrim()
    {
        var h = new Harness();
        var arg = JsonSerializer.Serialize(new SmartTrimArgument { Enabled = true, IntervalSeconds = 60, ThresholdMb = 256 });

        Assert.True(h.Send(CommandTypes.ConfigureSmartTrim, argument: arg).Success);

        Assert.True(h.Stored.SmartTrimEnabled);
        h.SmartTrim.Verify(t => t.Configure(true, TimeSpan.FromSeconds(60), 256L * 1024 * 1024), Times.Once);
    }

    [Fact]
    public void SetSetting_ReplacesSettingsButKeepsStoredId()
    {
        var h = new Harness();
        var incoming = new AppSettings { Id = 99, SmartTrimEnabled = true, SmartTrimMemoryThresholdMB = 2048, PollingIntervalMs = 2000 };

        var reply = h.Dispatcher.Dispatch(new CommandMessage { Type = MessageType.SetSetting, Payload = JsonSerializer.Serialize(incoming) });

        Assert.True(JsonSerializer.Deserialize<CommandResult>(reply.Payload)!.Success);
        Assert.Equal(1, h.Stored.Id);
        Assert.Equal(2048, h.Stored.SmartTrimMemoryThresholdMB);
        h.Monitor.Verify(m => m.SetPollingInterval(TimeSpan.FromMilliseconds(2000)), Times.Once);
        h.SmartTrim.Verify(t => t.Configure(true, It.IsAny<TimeSpan>(), 2048L * 1024 * 1024), Times.Once);
    }

    [Theory]
    [InlineData("BelowNormal", ProcessPriorityClass.BelowNormal)]
    [InlineData("idle", ProcessPriorityClass.Idle)]
    [InlineData("128", ProcessPriorityClass.High)]
    public void SetPriority_ReachesProcessController(string argument, ProcessPriorityClass expected)
    {
        var h = new Harness();
        Assert.True(h.Send(CommandTypes.SetPriority, 1234, argument).Success);
        h.Processes.Verify(p => p.SetPriority(1234, expected), Times.Once);
    }

    [Fact]
    public void SetPriority_RejectsUnknownPriority()
    {
        var h = new Harness();
        Assert.False(h.Send(CommandTypes.SetPriority, 1234, "Turbo").Success);
        h.Processes.Verify(p => p.SetPriority(It.IsAny<int>(), It.IsAny<ProcessPriorityClass>()), Times.Never);
    }

    [Theory]
    [InlineData("0xF0", 0xF0L)]
    [InlineData("15", 15L)]
    public void SetAffinity_ParsesHexAndDecimal(string argument, long expected)
    {
        var h = new Harness();
        Assert.True(h.Send(CommandTypes.SetAffinity, 1234, argument).Success);
        h.Processes.Verify(p => p.SetAffinity(1234, expected), Times.Once);
    }

    [Fact]
    public void ProcessCommands_ReachProcessController()
    {
        var h = new Harness();
        Assert.True(h.Send(CommandTypes.SuspendProcess, 1234).Success);
        Assert.True(h.Send(CommandTypes.ResumeProcess, 1234).Success);
        Assert.True(h.Send(CommandTypes.TerminateProcess, 1234).Success);

        h.Processes.Verify(p => p.Suspend(1234), Times.Once);
        h.Processes.Verify(p => p.Resume(1234), Times.Once);
        h.Processes.Verify(p => p.Terminate(1234), Times.Once);
    }

    [Theory]
    [InlineData(CommandTypes.TerminateProcess)]
    [InlineData(CommandTypes.SuspendProcess)]
    public void TerminateAndSuspend_RefuseTheEngineItselfAndSystem(string command)
    {
        var h = new Harness();
        Assert.False(h.Send(command, Environment.ProcessId).Success);
        Assert.False(h.Send(command, 4).Success);
        Assert.False(h.Send(command).Success); // no pid at all

        h.Processes.Verify(p => p.Terminate(It.IsAny<int>()), Times.Never);
        h.Processes.Verify(p => p.Suspend(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void ProcessControllerFailure_IsReportedNotThrown()
    {
        var h = new Harness();
        h.Processes.Setup(p => p.Terminate(1234)).Throws(new InvalidOperationException("Access is denied"));

        var result = h.Send(CommandTypes.TerminateProcess, 1234);

        Assert.False(result.Success);
        Assert.Contains("Access is denied", result.Error);
    }

    [Fact]
    public void CpuLimitCommands_ReachCpuLimiter()
    {
        var h = new Harness();
        Assert.True(h.Send(CommandTypes.SetCpuLimit, 1234, "25").Success);
        Assert.True(h.Send(CommandTypes.RemoveCpuLimit, 1234).Success);
        Assert.False(h.Send(CommandTypes.SetCpuLimit, 1234, "150").Success);

        h.CpuLimiter.Verify(c => c.AddLimit(1234, 25.0), Times.Once);
        h.CpuLimiter.Verify(c => c.RemoveLimit(1234), Times.Once);
    }

    [Fact]
    public void TrimWorkingSet_TrimsOneProcessOrAll()
    {
        var h = new Harness();
        Assert.True(h.Send(CommandTypes.TrimWorkingSet, 1234).Success);
        Assert.True(h.Send(CommandTypes.TrimWorkingSet).Success);

        h.SmartTrim.Verify(t => t.TrimProcess(1234), Times.Once);
        h.SmartTrim.Verify(t => t.TrimMemory(), Times.Once);
    }

    [Fact]
    public void UnknownCommand_FailsWithoutTouchingServices()
    {
        var h = new Harness();
        var result = h.Send("ReticulateSplines");

        Assert.False(result.Success);
        Assert.Contains("ReticulateSplines", result.Error);
        h.RuleStore.Verify(s => s.UpdateSettings(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public void HandleLine_MalformedJson_RepliesWithError()
    {
        var h = new Harness();
        var reply = JsonSerializer.Deserialize<IpcMessage>(h.Dispatcher.HandleLine("{not json"))!;

        Assert.Equal(MessageType.CommandResult, reply.Type);
        Assert.False(JsonSerializer.Deserialize<CommandResult>(reply.Payload)!.Success);
    }

    [Fact]
    public async Task PipeServer_RoutesCommandsOverTheNamedPipe()
    {
        var h = new Harness();
        var pipeName = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        var server = new PipeServer(NullLogger<PipeServer>.Instance, h.Dispatcher) { PipeName = pipeName };
        server.Start();
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(cts.Token);
            using var reader = new StreamReader(client);
            using var writer = new StreamWriter(client) { AutoFlush = true };

            async Task<IpcMessage> RoundTrip(IpcMessage request)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(request, request.GetType()));
                var line = await reader.ReadLineAsync(cts.Token);
                Assert.NotNull(line);
                return JsonSerializer.Deserialize<IpcMessage>(line!)!;
            }

            var pong = await RoundTrip(new IpcMessage { Type = MessageType.Ping });
            Assert.Equal(MessageType.Pong, pong.Type);

            var reply = await RoundTrip(new CommandMessage { CommandType = CommandTypes.EnablePerformanceMode });
            Assert.Equal(MessageType.CommandResult, reply.Type);
            Assert.True(JsonSerializer.Deserialize<CommandResult>(reply.Payload)!.Success);
            h.Power.Verify(p => p.EnablePerformanceMode(true), Times.Once);

            reply = await RoundTrip(new CommandMessage { CommandType = CommandTypes.SetSensitivity, Argument = "3" });
            Assert.True(JsonSerializer.Deserialize<CommandResult>(reply.Payload)!.Success);
            h.ProBalance.Verify(p => p.SetSensitivity(3), Times.Once);
        }
        finally
        {
            server.Stop();
        }
    }

    // ---------- Settings application ----------

    [Fact]
    public void SettingsApplier_FirstApply_ConfiguresEveryService()
    {
        var h = new Harness();
        var plan = Powrprof.HighPerformance;
        h.Applier.Apply(new AppSettings
        {
            PollingIntervalMs = 1500,
            SmartTrimEnabled = true, SmartTrimIntervalSeconds = 120, SmartTrimMemoryThresholdMB = 512,
            IdleSaverEnabled = true, IdleTimeoutMinutes = 10, IdlePowerPlanGuid = plan.ToString(),
            ProBalanceEnabled = false, ProBalanceSensitivity = 6
        });

        h.Monitor.Verify(m => m.SetPollingInterval(TimeSpan.FromMilliseconds(1500)), Times.Once);
        h.SmartTrim.Verify(t => t.Configure(true, TimeSpan.FromSeconds(120), 512L * 1024 * 1024), Times.Once);
        h.Power.Verify(p => p.ConfigureIdleSaver(true, 10, plan), Times.Once);
        h.ProBalance.Verify(p => p.SetEnabled(false), Times.Once);
        h.ProBalance.Verify(p => p.SetSensitivity(6), Times.Once);
    }

    [Fact]
    public void SettingsApplier_ReappliesOnlyTheGroupsThatChanged()
    {
        var h = new Harness();
        var settings = new AppSettings();
        h.Applier.Apply(settings);

        // Same values again (e.g. the DB was rewritten because a rule changed): nothing is reset.
        h.Applier.Apply(settings.Clone());
        h.Monitor.Verify(m => m.SetPollingInterval(It.IsAny<TimeSpan>()), Times.Once);
        h.SmartTrim.Verify(t => t.Configure(It.IsAny<bool>(), It.IsAny<TimeSpan>(), It.IsAny<long>()), Times.Once);
        h.Power.Verify(p => p.ConfigureIdleSaver(It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<Guid>()), Times.Once);
        h.ProBalance.Verify(p => p.SetSensitivity(It.IsAny<int>()), Times.Once);

        var changed = settings.Clone();
        changed.SmartTrimEnabled = !settings.SmartTrimEnabled;
        h.Applier.Apply(changed);
        h.SmartTrim.Verify(t => t.Configure(It.IsAny<bool>(), It.IsAny<TimeSpan>(), It.IsAny<long>()), Times.Exactly(2));
        h.Monitor.Verify(m => m.SetPollingInterval(It.IsAny<TimeSpan>()), Times.Once);
        h.ProBalance.Verify(p => p.SetSensitivity(It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void SettingsApplier_ProBalanceRestraintActions_AreAppliedWhenTheyChange()
    {
        var h = new Harness();
        var settings = new AppSettings();
        h.Applier.Apply(settings);
        h.ProBalance.Verify(p => p.ConfigureRestraint(new ProBalanceRestraintOptions()), Times.Once);

        h.Applier.Apply(settings.Clone());
        h.ProBalance.Verify(p => p.ConfigureRestraint(It.IsAny<ProBalanceRestraintOptions>()), Times.Once);

        var changed = settings.Clone();
        changed.ProBalanceRestrainByAffinity = true;
        changed.ProBalanceRestraintAffinity = 0x3;
        changed.ProBalanceLowerIoPriority = true;
        changed.ProBalanceUseEfficiencyMode = true;
        changed.ProBalanceDoNotLowerPriorityClass = true;
        changed.ProBalanceDisableWhenSysIdle = true;
        changed.ProBalanceDisableIfSysIdleMs = 60000;
        h.Applier.Apply(changed);

        h.ProBalance.Verify(p => p.ConfigureRestraint(new ProBalanceRestraintOptions
        {
            RestrainByAffinity = true, RestraintAffinity = 0x3, LowerIOPriorityDuringRestraint = true, UseEfficiencyMode = true,
            DoNotLowerPriorityClass = true, DisableProBalanceWhenSysIdle = true, DisableProBalanceIfSysIdleThisManyMS = 60000
        }), Times.Once);
        h.ProBalance.Verify(p => p.SetSensitivity(It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void SettingsApplier_MissingOrBadIdlePlan_FallsBackToPowerSaver()
    {
        var h = new Harness();
        h.Applier.Apply(new AppSettings { IdleSaverEnabled = true, IdlePowerPlanGuid = "garbage" });
        h.Power.Verify(p => p.ConfigureIdleSaver(true, It.IsAny<int>(), Powrprof.PowerSaver), Times.Once);
    }

    [Fact]
    public void SettingsApplier_OneFailingServiceDoesNotBlockTheOthers()
    {
        var h = new Harness();
        h.SmartTrim.Setup(t => t.Configure(It.IsAny<bool>(), It.IsAny<TimeSpan>(), It.IsAny<long>())).Throws<InvalidOperationException>();

        h.Applier.Apply(new AppSettings());

        h.Power.Verify(p => p.ConfigureIdleSaver(It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<Guid>()), Times.Once);
        h.ProBalance.Verify(p => p.SetEnabled(It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public void SmartTrimService_Configure_UpdatesStateAndClampsInterval()
    {
        using var trim = new SmartTrimService(NullLogger<SmartTrimService>.Instance, new Mock<IProcessMonitor>().Object);

        trim.Configure(false, TimeSpan.FromSeconds(1), 300L * 1024 * 1024);

        Assert.False(trim.IsEnabled);
        Assert.Equal(SmartTrimService.MinInterval, trim.Interval);
        Assert.Equal(300L * 1024 * 1024, trim.ThresholdBytes);
    }

    [Fact]
    public async Task SmartTrimService_ConfigureWhileRunning_DoesNotThrow()
    {
        using var trim = new SmartTrimService(NullLogger<SmartTrimService>.Instance, new Mock<IProcessMonitor>().Object);
        await trim.StartAsync(CancellationToken.None);

        trim.Configure(true, TimeSpan.FromMinutes(2), 1);
        trim.Configure(false, TimeSpan.FromMinutes(2), 1);
        trim.Configure(true, TimeSpan.FromMinutes(3), 1);
        Assert.Equal(TimeSpan.FromMinutes(3), trim.Interval);

        await trim.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ProcessMonitor_SetPollingInterval_IsClamped()
    {
        using var monitor = new ProcessMonitor(NullLogger<ProcessMonitor>.Instance);

        monitor.SetPollingInterval(TimeSpan.FromMilliseconds(2500));
        Assert.Equal(TimeSpan.FromMilliseconds(2500), monitor.PollingInterval);

        monitor.SetPollingInterval(TimeSpan.FromMilliseconds(1));
        Assert.Equal(ProcessMonitor.MinPollingInterval, monitor.PollingInterval);

        monitor.SetPollingInterval(TimeSpan.FromHours(1));
        Assert.Equal(ProcessMonitor.MaxPollingInterval, monitor.PollingInterval);
    }

    // ---------- UI database write -> engine ----------

    private sealed class TempDbContext : ProGuardDbContext
    {
        private readonly string _path;
        public TempDbContext(string path) { _path = path; }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlite($"Data Source={_path}");
    }

    [Fact]
    public void SettingsSavedByTheUi_ReachTheEngineServicesOnReload()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"proguard_settings_{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddScoped<ProGuardDbContext>(_ => new TempDbContext(dbPath));
        using var provider = services.BuildServiceProvider();
        try
        {
            var store = new RuleStore(NullLogger<RuleStore>.Instance, provider);
            var h = new Harness();
            h.Applier.Apply(store.GetSettings()); // what EngineWorker does at startup

            // The UI's SettingsViewModel writes straight to the shared database.
            using (var ui = new TempDbContext(dbPath))
            {
                var s = ui.Settings.First();
                s.SmartTrimEnabled = true;
                s.SmartTrimMemoryThresholdMB = 768;
                s.PollingIntervalMs = 3000;
                ui.SaveChanges();
            }
            Assert.True(store.ReloadIfChanged());
            h.Applier.Apply(store.GetSettings()); // what EngineWorker does on each reload

            h.SmartTrim.Verify(t => t.Configure(true, It.IsAny<TimeSpan>(), 768L * 1024 * 1024), Times.Once);
            h.Monitor.Verify(m => m.SetPollingInterval(TimeSpan.FromMilliseconds(3000)), Times.Once);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }
}
