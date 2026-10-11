using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Shared.Messages;
using ProGuard.UI.IPC;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>
/// SmartTrim follows Process Lasso's [MemoryManagement] rules: trims are gated on memory load, purges on free RAM and
/// (optionally) Performance Mode, and the foreground, protected and excluded processes are never trimmed. The gates
/// run against fake memory readings and a fake native layer, so they don't depend on the test machine.
/// </summary>
public class SmartTrimTests
{
    private const long Mb = 1024 * 1024;

    // ---------- Fakes ----------

    private sealed class FakeMemory : IMemoryStatus
    {
        public int? Load { get; set; } = 80;
        public long? Free { get; set; } = 4096 * Mb;
        public long? FileCache { get; set; } = 2048 * Mb;

        public int? GetMemoryLoadPercent() => Load;
        public long? GetFreeRamBytes() => Free;
        public long? GetFileCacheBytes() => FileCache;
    }

    private sealed class FakeNative : IMemoryOperations
    {
        private readonly FakeMemory _memory;
        public FakeNative(FakeMemory memory) => _memory = memory;

        public List<int> Trimmed { get; } = new();
        public List<string> PrivilegeRequests { get; } = new();
        public int StandbyPurges { get; private set; }
        public int FileCachePurges { get; private set; }

        public Dictionary<string, string?> PrivilegeErrors { get; } = new();
        public string? StandbyError { get; set; }
        public string? FileCacheError { get; set; }
        /// <summary>Each trim reports this many bytes freed.</summary>
        public long BytesFreedPerTrim { get; set; } = 100 * Mb;
        /// <summary>What a successful standby purge adds to free RAM.</summary>
        public long StandbyFrees { get; set; } = 1500 * Mb;

        public string? EnablePrivilege(string privilege)
        {
            PrivilegeRequests.Add(privilege);
            return PrivilegeErrors.TryGetValue(privilege, out var error) ? error : null;
        }

        public WorkingSetTrimResult TrimWorkingSet(int pid)
        {
            Trimmed.Add(pid);
            return new WorkingSetTrimResult(true, 300 * Mb, 300 * Mb - BytesFreedPerTrim, null);
        }

        public string? PurgeStandbyList()
        {
            if (StandbyError != null) return StandbyError;
            StandbyPurges++;
            _memory.Free += StandbyFrees;
            return null;
        }

        public string? PurgeFileCache()
        {
            if (FileCacheError != null) return FileCacheError;
            FileCachePurges++;
            _memory.FileCache -= 800 * Mb;
            return null;
        }
    }

    private sealed class ListLogger : ILogger<SmartTrimService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Harness
    {
        public FakeMemory Memory { get; } = new();
        public FakeNative Native { get; }
        public Mock<IProcessMonitor> Monitor { get; } = new();
        public ListLogger Log { get; } = new();
        public DateTime Now { get; set; } = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        public bool PerformanceMode { get; set; }
        public SmartTrimService Service { get; }

        public Harness(params ProcessInfo[] processes)
        {
            Native = new FakeNative(Memory);
            Monitor.Setup(m => m.GetAllProcesses()).Returns(processes);
            Service = new SmartTrimService(Log, Monitor.Object, Memory, Native, () => Now)
            {
                IsPerformanceModeActive = () => PerformanceMode
            };
            Service.Configure(true, TimeSpan.FromMinutes(15), 196 * Mb);
        }

        public void Policy(SmartTrimPolicy policy) => Service.ConfigurePolicy(policy);
    }

    private static ProcessInfo Proc(int pid, string name, long workingSetMb, bool foreground = false) =>
        new() { Pid = pid, Name = name, WorkingSetBytes = workingSetMb * Mb, IsForeground = foreground };

    // ---------- Working-set trim gating ----------

    [Theory]
    [InlineData(64, false)]
    [InlineData(65, true)]
    [InlineData(90, true)]
    public void TrimPass_RunsOnlyAtOrAboveTheMinimumMemoryLoad(int load, bool trims)
    {
        var h = new Harness(Proc(1000, "chrome", 500));
        h.Memory.Load = load;

        var report = h.Service.RunTrimPass();

        Assert.Equal(trims, h.Native.Trimmed.Contains(1000));
        Assert.Equal(trims ? 1 : 0, report.ProcessesTrimmed);
    }

    [Fact]
    public void TrimPass_DoesNothing_WhenMemoryLoadIsUnknown()
    {
        var h = new Harness(Proc(1000, "chrome", 500));
        h.Memory.Load = null;

        h.Service.RunTrimPass();

        Assert.Empty(h.Native.Trimmed);
    }

    [Fact]
    public void TrimPass_HonoursCustomMinimumLoad()
    {
        var h = new Harness(Proc(1000, "chrome", 500));
        h.Policy(new SmartTrimPolicy { MinimumRamLoadPercent = 90 });
        h.Memory.Load = 85;

        h.Service.RunTrimPass();

        Assert.Empty(h.Native.Trimmed);
    }

    [Fact]
    public void TrimPass_SkipsSmallForegroundProtectedAndExcludedProcesses()
    {
        var h = new Harness(
            Proc(1001, "chrome", 500),
            Proc(1002, "edge", 196),                      // exactly the threshold: trimmed
            Proc(1003, "notepad", 195),                   // below the threshold
            Proc(1004, "game", 2000, foreground: true),   // foreground per the snapshot
            Proc(1005, "obs64", 900),                     // foreground per the tracker (below)
            Proc(1006, "dwm", 400),                       // protected
            Proc(1007, "csrss", 400),                     // protected
            Proc(1008, "KeepMe", 400),                    // excluded
            Proc(4, "System", 400),
            Proc(Environment.ProcessId, "ProGuard.Engine", 400));
        h.Monitor.Setup(m => m.GetForegroundPid()).Returns(1005);
        h.Policy(new SmartTrimPolicy { Exclusions = new[] { " keepme.EXE " } });

        var report = h.Service.RunTrimPass();

        Assert.Equal(new[] { 1001, 1002 }, h.Native.Trimmed.OrderBy(p => p));
        Assert.Equal(2, report.ProcessesTrimmed);
        Assert.Equal(200 * Mb, report.WorkingSetBytesReclaimed);
    }

    [Fact]
    public void TrimMemory_IgnoresTheLoadGateButKeepsTheExclusions()
    {
        var h = new Harness(Proc(1001, "chrome", 500), Proc(1006, "audiodg", 500), Proc(1003, "notepad", 50));
        h.Memory.Load = 10;

        h.Service.TrimMemory();

        Assert.Equal(new[] { 1001 }, h.Native.Trimmed);
    }

    [Fact]
    public void TrimPass_LogsEachTrimWithMegabytesReclaimed()
    {
        var h = new Harness(Proc(1001, "chrome", 500));

        h.Service.RunTrimPass();

        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("chrome") && e.Message.Contains("reclaimed 100 MB"));
    }

    [Theory]
    [InlineData(true, LogLevel.Information)]
    [InlineData(false, LogLevel.Debug)]
    public void NoActionPasses_AreLoggedAtInformationOnlyWhenConfigured(bool logNoAction, LogLevel expected)
    {
        var h = new Harness(Proc(1001, "chrome", 500));
        h.Policy(new SmartTrimPolicy { LogNoAction = logNoAction });
        h.Memory.Load = 20;

        h.Service.RunTrimPass();

        var entry = Assert.Single(h.Log.Entries, e => e.Message.Contains("no action"));
        Assert.Equal(expected, entry.Level);
        Assert.Contains("20%", entry.Message);
    }

    // ---------- Standby list / file cache purge gating ----------

    [Fact]
    public void LowMemoryCheck_DoesNothing_WhenNoPurgeIsEnabled()
    {
        var h = new Harness();
        h.Memory.Free = 100 * Mb;
        h.PerformanceMode = true;

        h.Service.RunLowMemoryCheck();

        Assert.Equal(0, h.Native.StandbyPurges);
        Assert.Equal(0, h.Native.FileCachePurges);
    }

    [Fact]
    public void LowMemoryCheck_ByDefaultOnlyPurgesInPerformanceMode()
    {
        var h = new Harness();
        h.Policy(new SmartTrimPolicy { StandbyPurgeEnabled = true });
        h.Memory.Free = 500 * Mb;

        h.Service.RunLowMemoryCheck();
        Assert.Equal(0, h.Native.StandbyPurges);

        h.PerformanceMode = true;
        var report = h.Service.RunLowMemoryCheck();
        Assert.Equal(1, h.Native.StandbyPurges);
        Assert.True(report.StandbyPurged);
        Assert.Equal(1500 * Mb, report.StandbyBytesReclaimed);
    }

    [Fact]
    public void LowMemoryCheck_PurgesOutsidePerformanceMode_WhenThatGateIsOff()
    {
        var h = new Harness();
        h.Policy(new SmartTrimPolicy { StandbyPurgeEnabled = true, StandbyOnlyInPerformanceMode = false });
        h.Memory.Free = 500 * Mb;

        h.Service.RunLowMemoryCheck();

        Assert.Equal(1, h.Native.StandbyPurges);
    }

    [Theory]
    [InlineData(1023, true)]
    [InlineData(1024, false)]
    [InlineData(8000, false)]
    public void LowMemoryCheck_PurgesOnlyBelowTheFreeRamThreshold(long freeMb, bool purges)
    {
        var h = new Harness();
        h.Policy(new SmartTrimPolicy { StandbyPurgeEnabled = true, StandbyOnlyInPerformanceMode = false });
        h.Memory.Free = freeMb * Mb;

        h.Service.RunLowMemoryCheck();

        Assert.Equal(purges ? 1 : 0, h.Native.StandbyPurges);
    }

    [Fact]
    public void LowMemoryCheck_DoesNothing_WhenFreeRamIsUnknown()
    {
        var h = new Harness();
        h.Policy(new SmartTrimPolicy { StandbyPurgeEnabled = true, StandbyOnlyInPerformanceMode = false });
        h.Memory.Free = null;

        h.Service.RunLowMemoryCheck();

        Assert.Equal(0, h.Native.StandbyPurges);
    }

    [Fact]
    public void LowMemoryCheck_WaitsOutTheCooldownBetweenAutomaticPurges()
    {
        var h = new Harness();
        h.Policy(new SmartTrimPolicy { StandbyPurgeEnabled = true, StandbyOnlyInPerformanceMode = false });
        h.Native.StandbyFrees = 0; // free RAM stays low, as when the standby list was already empty
        h.Memory.Free = 200 * Mb;

        h.Service.RunLowMemoryCheck();
        h.Now += SmartTrimService.LowMemoryCheckInterval;
        h.Service.RunLowMemoryCheck();
        Assert.Equal(1, h.Native.StandbyPurges);

        h.Now += SmartTrimService.AutomaticPurgeCooldown;
        h.Service.RunLowMemoryCheck();
        Assert.Equal(2, h.Native.StandbyPurges);
    }

    [Fact]
    public void LowMemoryCheck_PurgesOnlyTheEnabledLists()
    {
        var h = new Harness();
        h.Policy(new SmartTrimPolicy { FileCachePurgeEnabled = true, StandbyOnlyInPerformanceMode = false });
        h.Memory.Free = 200 * Mb;

        var report = h.Service.RunLowMemoryCheck();

        Assert.Equal(0, h.Native.StandbyPurges);
        Assert.Equal(1, h.Native.FileCachePurges);
        Assert.True(report.FileCachePurged);
        Assert.Equal(800 * Mb, report.FileCacheBytesReclaimed);
    }

    [Fact]
    public void Purge_ReportsNativeFailures()
    {
        var h = new Harness();
        h.Policy(new SmartTrimPolicy { StandbyPurgeEnabled = true, StandbyOnlyInPerformanceMode = false });
        h.Native.StandbyError = "Standby list purge failed: SeProfileSingleProcessPrivilege is not enabled";
        h.Memory.Free = 200 * Mb;

        var report = h.Service.RunLowMemoryCheck();

        Assert.False(report.StandbyPurged);
        Assert.Contains(h.Native.StandbyError, report.Errors);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SeProfileSingleProcessPrivilege"));
    }

    // ---------- Purge now ----------

    [Fact]
    public void PurgeNow_IgnoresTheGates()
    {
        var h = new Harness(Proc(1001, "chrome", 500), Proc(1003, "notepad", 50));
        h.Memory.Load = 10;
        h.Memory.Free = 30000 * Mb;
        h.PerformanceMode = false;

        var report = h.Service.PurgeNow();

        Assert.Equal(new[] { 1001 }, h.Native.Trimmed);
        Assert.Equal(1, h.Native.StandbyPurges);
        Assert.Equal(0, h.Native.FileCachePurges); // file cache purge is opt-in
        Assert.Equal(100 * Mb + 1500 * Mb, report.TotalBytesReclaimed);
        Assert.Contains("purged standby list (1500 MB)", report.Summary());
    }

    [Fact]
    public void PurgeNow_AlsoPurgesTheFileCache_WhenEnabled()
    {
        var h = new Harness();
        h.Policy(new SmartTrimPolicy { FileCachePurgeEnabled = true });

        var report = h.Service.PurgeNow();

        Assert.True(report.StandbyPurged);
        Assert.True(report.FileCachePurged);
    }

    // ---------- Privileges ----------

    [Fact]
    public async Task Start_EnablesBothPrivileges_AndReportsAFailureOnce()
    {
        var h = new Harness();
        h.Native.PrivilegeErrors[MemoryApi.SeProfileSingleProcessPrivilege] = "SeProfileSingleProcessPrivilege is not held by test's token";

        await h.Service.StartAsync(default);
        h.Service.PurgeNow();
        h.Service.PurgeNow();
        await h.Service.StopAsync(default);
        h.Service.Dispose();

        Assert.Contains(MemoryApi.SeProfileSingleProcessPrivilege, h.Native.PrivilegeRequests);
        Assert.Contains(MemoryApi.SeIncreaseQuotaPrivilege, h.Native.PrivilegeRequests);
        var warnings = h.Log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("could not enable")).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains("Standby list purges will fail", warning.Message);
    }

    // ---------- Settings ----------

    [Fact]
    public void AppSettings_DefaultToProcessLassosMemoryManagementValues()
    {
        var s = new AppSettings();
        Assert.Equal(15 * 60, s.SmartTrimIntervalSeconds);
        Assert.Equal(196, s.SmartTrimMemoryThresholdMB);
        Assert.Equal(65, s.SmartTrimMinimumRamLoadPercent);
        Assert.Equal(1024, s.StandbyPurgeFreeRamThresholdMB);
        Assert.True(s.StandbyPurgeOnlyInPerformanceMode);
        Assert.False(s.StandbyPurgeEnabled);
        Assert.False(s.FileCachePurgeEnabled);
        Assert.False(s.SmartTrimLogNoAction);
    }

    [Fact]
    public void SettingsApplier_PassesThePolicyToSmartTrim_OnlyWhenItChanges()
    {
        var trim = new Mock<ISmartTrimService>();
        var applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, new Mock<IProcessMonitor>().Object, trim.Object,
            new Mock<IPowerManager>().Object, new Mock<IProBalanceEngine>().Object);
        var settings = new AppSettings
        {
            SmartTrimMinimumRamLoadPercent = 70,
            SmartTrimExclusions = "obs64.exe, game.exe;\r\nfoo",
            StandbyPurgeEnabled = true,
            StandbyPurgeFreeRamThresholdMB = 2048,
            StandbyPurgeOnlyInPerformanceMode = false,
            FileCachePurgeEnabled = true,
            SmartTrimLogNoAction = true,
        };

        applier.Apply(settings);
        applier.Apply(settings.Clone());
        var unrelated = settings.Clone();
        unrelated.PollingIntervalMs = 5000;
        applier.Apply(unrelated);

        trim.Verify(t => t.ConfigurePolicy(It.IsAny<SmartTrimPolicy>()), Times.Once);
        trim.Verify(t => t.ConfigurePolicy(It.Is<SmartTrimPolicy>(p =>
            p.MinimumRamLoadPercent == 70
            && p.Exclusions.SequenceEqual(new[] { "obs64.exe", "game.exe", "foo" })
            && p.StandbyPurgeEnabled && !p.StandbyOnlyInPerformanceMode
            && p.StandbyFreeRamThresholdBytes == 2048 * Mb
            && p.FileCachePurgeEnabled && p.LogNoAction)), Times.Once);

        var changed = unrelated.Clone();
        changed.StandbyPurgeFreeRamThresholdMB = 512;
        applier.Apply(changed);
        trim.Verify(t => t.ConfigurePolicy(It.IsAny<SmartTrimPolicy>()), Times.Exactly(2));
    }

    [Fact]
    public void PowerManager_ReportsPerformanceModeForTheStandbyGate()
    {
        var plan = Powrprof.Balanced;
        var power = new PowerManager(null, new Mock<IUserIdleSource>().Object, () => plan, p => { plan = p; return true; });

        Assert.False(power.IsPerformanceModeActive);
        power.EnablePerformanceMode(true);
        Assert.True(power.IsPerformanceModeActive);
        power.EnablePerformanceMode(false);
        Assert.False(power.IsPerformanceModeActive);
    }

    // ---------- "Purge now" over IPC ----------

    private sealed class FakeCaller : IIpcCaller
    {
        public string Name { get; init; } = @"TEST\user";
        public bool IsAdministrator { get; init; }
        public bool CanOpenProcess(int pid, ProcessAccessFlags access) => true;
    }

    private static (IpcDispatcher Dispatcher, Mock<ISmartTrimService> Trim) Dispatcher()
    {
        var store = new Mock<IRuleStore>();
        store.Setup(s => s.GetSettings()).Returns(new AppSettings { Id = 1 });
        var monitor = new Mock<IProcessMonitor>();
        var trim = new Mock<ISmartTrimService>();
        var power = new Mock<IPowerManager>();
        var proBalance = new Mock<IProBalanceEngine>();
        var applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, monitor.Object, trim.Object, power.Object, proBalance.Object);
        var dispatcher = new IpcDispatcher(NullLogger<IpcDispatcher>.Instance, store.Object, applier, proBalance.Object, power.Object,
            trim.Object, new Mock<ICpuLimiter>().Object, new Mock<IProcessController>().Object, monitor.Object);
        return (dispatcher, trim);
    }

    private static CommandResult Send(IpcDispatcher dispatcher, IIpcCaller caller) =>
        JsonSerializer.Deserialize<CommandResult>(
            dispatcher.Dispatch(new CommandMessage { CommandType = CommandTypes.PurgeMemory }, caller).Payload)!;

    [Fact]
    public void PurgeMemory_IsAdministratorOnly()
    {
        var (dispatcher, trim) = Dispatcher();

        var denied = Send(dispatcher, new FakeCaller { IsAdministrator = false });

        Assert.False(denied.Success);
        Assert.Contains("Access denied", denied.Error);
        Assert.Contains(CommandTypes.PurgeMemory, IpcAuthorization.AdministratorOnlyCommands);
        trim.Verify(t => t.PurgeNow(), Times.Never);
    }

    [Fact]
    public void PurgeMemory_ReturnsTheReport()
    {
        var (dispatcher, trim) = Dispatcher();
        trim.Setup(t => t.PurgeNow()).Returns(new MemoryPurgeReport { ProcessesTrimmed = 3, WorkingSetBytesReclaimed = 600 * Mb, StandbyPurged = true, StandbyBytesReclaimed = 1000 * Mb });

        var result = Send(dispatcher, new FakeCaller { IsAdministrator = true });

        Assert.True(result.Success, result.Error);
        var report = JsonSerializer.Deserialize<MemoryPurgeReport>(result.Data!)!;
        Assert.Equal(3, report.ProcessesTrimmed);
        Assert.Equal(1600 * Mb, report.TotalBytesReclaimed);
    }

    [Fact]
    public void PurgeMemory_FailsWithTheErrors_WhenNothingCouldBeDone()
    {
        var (dispatcher, trim) = Dispatcher();
        trim.Setup(t => t.PurgeNow()).Returns(new MemoryPurgeReport { Errors = { "Standby list purge failed: access denied" } });

        var result = Send(dispatcher, new FakeCaller { IsAdministrator = true });

        Assert.False(result.Success);
        Assert.Contains("access denied", result.Error);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task SettingsPage_PurgeNow_ShowsTheEnginesAnswer()
    {
        var (dispatcher, trim) = Dispatcher();
        trim.Setup(t => t.PurgeNow()).Returns(new MemoryPurgeReport { ProcessesTrimmed = 2, WorkingSetBytesReclaimed = 300 * Mb, StandbyPurged = true, StandbyBytesReclaimed = 700 * Mb });
        var pipeName = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        var admin = true;
        var server = new PipeServer(NullLogger<PipeServer>.Instance, dispatcher)
        {
            PipeName = pipeName,
            IdentifyCaller = _ => new FakeCaller { IsAdministrator = admin }
        };
        server.Start();
        try
        {
            var vm = new SettingsViewModel(new EngineProxy(new PipeClient(pipeName) { ConnectTimeout = TimeSpan.FromSeconds(5) }));

            await vm.PurgeNowCommand.ExecuteAsync(null);
            Assert.Equal("Trimmed 2 processes (300 MB); purged standby list (700 MB). Reclaimed 1000 MB in total.", vm.PurgeResultText);

            admin = false;
            var vm2 = new SettingsViewModel(new EngineProxy(new PipeClient(pipeName) { ConnectTimeout = TimeSpan.FromSeconds(5) }));
            await vm2.PurgeNowCommand.ExecuteAsync(null);
            Assert.Contains("Access denied", vm2.PurgeResultText);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task SettingsPage_PurgeNow_SaysWhenTheEngineIsNotRunning()
    {
        var vm = new SettingsViewModel(new EngineProxy(new PipeClient("ProGuardTest_" + Guid.NewGuid().ToString("N")) { ConnectTimeout = TimeSpan.FromMilliseconds(100) }));

        await vm.PurgeNowCommand.ExecuteAsync(null);

        Assert.Equal("The ProGuard engine is not running.", vm.PurgeResultText);
    }
}

/// <summary>A fact that only runs when the test host is (or is not) elevated, and is reported as skipped otherwise.</summary>
public sealed class ElevationFactAttribute : FactAttribute
{
    public static bool IsElevated
    {
        get
        {
            using var self = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(self).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public ElevationFactAttribute(bool requiresElevation = true)
    {
        if (IsElevated != requiresElevation)
            Skip = requiresElevation ? "Needs an elevated test host (run as administrator)." : "Only meaningful when the test host is not elevated.";
    }
}

/// <summary>
/// The real Win32 memory layer. The purges need privileges only an elevated (or SYSTEM) token holds, so those tests skip
/// unless the host is elevated; the unelevated counterpart checks that the failure is reported clearly instead.
/// </summary>
public class SmartTrimNativeIntegrationTests
{
    [Fact]
    public void MemoryStatus_ReadsLoadAndFreeRam_WithoutPrivileges()
    {
        var status = new Win32MemoryStatus();

        Assert.InRange(status.GetMemoryLoadPercent()!.Value, 1, 100);
        Assert.True(status.GetFreeRamBytes() > 0);
        Assert.True(Win32MemoryStatus.QueryPdhFreeAndZero() > 0);
    }

    [Fact]
    public void TrimWorkingSet_TrimsAProcessThisUserOwns()
    {
        // ping is a low-CPU stand-in; its priority (which Process Lasso may change) doesn't matter here.
        var psi = new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        try
        {
            var result = new Win32MemoryOperations().TrimWorkingSet(p.Id);

            Assert.True(result.Success, result.Error);
            Assert.True(result.BytesBefore > 0);
            Assert.True(result.BytesAfter <= result.BytesBefore);
        }
        finally
        {
            try { p.Kill(); } catch { }
        }
    }

    [Fact]
    public void TrimWorkingSet_ReportsAProcessThatDoesNotExist()
    {
        var result = new Win32MemoryOperations().TrimWorkingSet(-1);

        Assert.False(result.Success);
        Assert.Contains("OpenProcess failed", result.Error);
    }

    [ElevationFact]
    public void Elevated_EnablesPrivileges_AndPurgesTheStandbyListAndFileCache()
    {
        var native = new Win32MemoryOperations();

        Assert.Null(native.EnablePrivilege(MemoryApi.SeProfileSingleProcessPrivilege));
        Assert.Null(native.EnablePrivilege(MemoryApi.SeIncreaseQuotaPrivilege));
        Assert.NotNull(Win32MemoryStatus.QueryMemoryLists());

        Assert.Null(native.PurgeStandbyList());
        Assert.Null(native.PurgeFileCache());
    }

    [ElevationFact(requiresElevation: false)]
    public void Unelevated_ReportsTheMissingPrivilegesClearly()
    {
        var native = new Win32MemoryOperations();

        var privilegeError = native.EnablePrivilege(MemoryApi.SeProfileSingleProcessPrivilege);
        Assert.NotNull(privilegeError);
        Assert.Contains("SeProfileSingleProcessPrivilege is not held", privilegeError);

        var purgeError = native.PurgeStandbyList();
        Assert.NotNull(purgeError);
        Assert.StartsWith("Standby list purge failed", purgeError);
    }
}
