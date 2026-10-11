using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

/// <summary>An account at the other end of the pipe: which processes it may open is up to the test.</summary>
internal sealed class ThinCaller : IIpcCaller
{
    public string Name { get; init; } = @"TEST\standard";
    public bool IsAdministrator { get; init; }
    public HashSet<int> OpenablePids { get; } = new();
    public List<(int Pid, ProcessAccessFlags Access)> Checks { get; } = new();
    public int BatchCalls { get; private set; }

    public bool CanOpenProcess(int pid, ProcessAccessFlags access)
    {
        Checks.Add((pid, access));
        return OpenablePids.Contains(pid);
    }

    public IReadOnlySet<int> FilterOpenable(IReadOnlyList<(int Pid, DateTime StartTimeUtc)> processes, ProcessAccessFlags access)
    {
        BatchCalls++;
        return processes.Select(p => p.Pid).Where(pid => IsAdministrator || OpenablePids.Contains(pid)).ToHashSet();
    }

    public static ThinCaller Admin() => new() { Name = @"TEST\admin", IsAdministrator = true };
}

internal sealed class FakeDetails : IProcessDetailsReader
{
    public Dictionary<int, ProcessTuning> Tuning { get; } = new();
    public Dictionary<int, string?> Users { get; } = new();
    public int UserReads { get; private set; }

    public ProcessTuning ReadTuning(int pid) => Tuning.GetValueOrDefault(pid);

    public string? ReadUserName(int pid)
    {
        UserReads++;
        return Users.GetValueOrDefault(pid);
    }
}

/// <summary>
/// The real dispatcher, snapshot builder and rule service over in-memory stand-ins for the machine: stored rules and
/// settings, processes, Windows' per-process calls and the engine's services.
/// </summary>
internal sealed class ThinHarness
{
    public List<ProcessRule> Rules { get; } = new();
    public List<WatchdogRule> WatchdogRules { get; } = new();
    public List<ProBalanceAction> Actions { get; } = new();
    public AppSettings Stored { get; private set; } = new() { Id = 1 };
    private int _nextId = 100;

    public Mock<IRuleStore> Store { get; } = new();
    public Mock<IProBalanceEngine> ProBalance { get; } = new();
    public Mock<IPowerManager> Power { get; } = new();
    public Mock<ISmartTrimService> SmartTrim { get; } = new();
    public Mock<ICpuLimiter> CpuLimiter { get; } = new();
    public Mock<IProcessController> Controller { get; } = new();
    public Mock<IRuleProcessAccess> Access { get; } = new();
    public FakeProcessMonitor Monitor { get; } = new();
    public FakeProcessTuning Tuning { get; } = new();
    public FakeDetails Details { get; } = new();
    public ActivityLog Manual { get; } = new();
    public ListLogger<IpcDispatcher> Log { get; } = new();
    public WatchdogEventLog WatchdogEvents { get; } = new();
    public FakeLaunchRegistry LaunchRegistry { get; } = new();
    public FakeTimerNative Timer { get; } = new();
    public TimerResolutionService TimerResolution { get; }
    public ForegroundBoostService ForegroundBoost { get; }
    public SnapshotBuilder Snapshots { get; }
    public IpcDispatcher Dispatcher { get; }

    // Power state the mock reports.
    public Guid ActivePlan { get; set; } = Powrprof.Balanced;
    public List<(Guid, string)> Plans { get; } = new()
    {
        (Powrprof.Balanced, "Balanced"), (Powrprof.HighPerformance, "High performance"), (Powrprof.PowerSaver, "Power saver"),
    };

    public long NowMs { get; set; } = 1_000_000;
    public SystemMetrics Metrics { get; set; } = new(37.5, new[] { 10.0, 20.0, 30.0, 40.0 }, 2.0, 61, 16L << 30, 6L << 30, 4);
    public int MetricsReads { get; private set; }

    public ThinHarness()
    {
        Store.Setup(s => s.GetProcessRules()).Returns(() => Rules.ToList());
        Store.Setup(s => s.GetProcessRule(It.IsAny<string>())).Returns((string name) =>
            Rules.FirstOrDefault(r => ProcessExtensions.NormalizeName(r.ProcessName) == ProcessExtensions.NormalizeName(name)));
        Store.Setup(s => s.SaveProcessRule(It.IsAny<ProcessRule>())).Callback<ProcessRule>(r =>
        {
            Rules.RemoveAll(x => x.Id != 0 && x.Id == r.Id);
            if (r.Id == 0) r.Id = _nextId++;
            Rules.Add(r);
        });
        Store.Setup(s => s.DeleteProcessRule(It.IsAny<int>())).Callback<int>(id => Rules.RemoveAll(r => r.Id == id));
        Store.Setup(s => s.GetWatchdogRules()).Returns(() => WatchdogRules.ToList());
        Store.Setup(s => s.AddWatchdogRule(It.IsAny<WatchdogRule>())).Callback<WatchdogRule>(r => { r.Id = _nextId++; WatchdogRules.Add(r); });
        Store.Setup(s => s.UpdateWatchdogRule(It.IsAny<WatchdogRule>())).Callback<WatchdogRule>(r =>
        {
            WatchdogRules.RemoveAll(x => x.Id == r.Id);
            WatchdogRules.Add(r);
        });
        Store.Setup(s => s.DeleteWatchdogRule(It.IsAny<int>())).Callback<int>(id => WatchdogRules.RemoveAll(r => r.Id == id));
        Store.Setup(s => s.GetSettings()).Returns(() => Stored);
        Store.Setup(s => s.UpdateSettings(It.IsAny<AppSettings>())).Callback<AppSettings>(s => Stored = s);
        Store.Setup(s => s.GetRecentActions(It.IsAny<int>())).Returns((int n) => Actions.OrderByDescending(a => a.Timestamp).Take(n).ToList());
        Store.Setup(s => s.ClearProBalanceActions()).Callback(() => Actions.Clear());

        ProBalance.SetupGet(p => p.RestrainedPids).Returns(Array.Empty<int>());
        CpuLimiter.Setup(c => c.GetActiveLimits()).Returns(new Dictionary<int, double>());
        Power.Setup(p => p.GetAvailablePlans()).Returns(() => Plans.Select(p => (p.Item1, p.Item2)).ToList());
        Power.Setup(p => p.GetActivePlan()).Returns(() => ActivePlan);
        Power.Setup(p => p.SetActivePlan(It.IsAny<Guid>())).Callback<Guid>(id => ActivePlan = id);
        Power.SetupGet(p => p.PlanOwnerName).Returns("User");
        SmartTrim.Setup(t => t.TrimMemory()).Returns(new MemoryPurgeReport());
        SmartTrim.Setup(t => t.TrimProcess(It.IsAny<int>())).Returns(new MemoryPurgeReport { ProcessesTrimmed = 1 });
        Access.Setup(a => a.IsCritical(It.IsAny<int>())).Returns(false);
        Access.Setup(a => a.Terminate(It.IsAny<int>())).Returns(true);
        Access.Setup(a => a.SetPriorityClass(It.IsAny<int>(), It.IsAny<System.Diagnostics.ProcessPriorityClass>())).Returns(true);
        Access.Setup(a => a.SetAffinityMask(It.IsAny<int>(), It.IsAny<long>())).Returns(true);

        ForegroundBoost = new ForegroundBoostService(NullLogger<ForegroundBoostService>.Instance, ProBalance.Object, new Mock<IForegroundTracker>().Object);
        TimerResolution = new TimerResolutionService(native: Timer);
        var applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, Monitor, SmartTrim.Object, Power.Object, ProBalance.Object,
            foregroundBoost: ForegroundBoost, timerResolution: TimerResolution);

        Snapshots = new SnapshotBuilder(Monitor, ProBalance.Object, CpuLimiter.Object, Store.Object, Power.Object, Details,
            foregroundBoost: ForegroundBoost, metrics: () => { MetricsReads++; return Metrics; }, clockMs: () => NowMs, engineIsElevated: true);

        var actions = new ProcessActionService(NullLogger<ProcessActionService>.Instance, Tuning, Monitor, Controller.Object);
        var rules = new RuleService(NullLogger<RuleService>.Instance, Store.Object, Monitor, CpuLimiter.Object, ProBalance.Object,
            Access.Object, Manual, launchPriorities: new LaunchPriorityService(registry: LaunchRegistry));
        var feed = new ActivityFeed(Store.Object, Manual, WatchdogEvents);
        var thin = new ThinClientCommands(NullLogger<ThinClientCommands>.Instance, Snapshots, actions, rules, feed, Manual,
            Store.Object, Power.Object, Monitor, Tuning);
        Dispatcher = new IpcDispatcher(Log, Store.Object, applier, ProBalance.Object, Power.Object,
            SmartTrim.Object, CpuLimiter.Object, Controller.Object, Monitor, WatchdogEvents, thin, timerResolution: TimerResolution);
    }

    public CommandResult Send(IIpcCaller caller, string command, int? pid = null, object? argument = null)
    {
        var text = argument switch
        {
            null => null,
            string s => s,
            bool b => b ? "true" : "false",
            int or long => Convert.ToString(argument, System.Globalization.CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(argument),
        };
        var reply = Dispatcher.Dispatch(new CommandMessage { CommandType = command, TargetPid = pid, Argument = text }, caller);
        Assert.Equal(MessageType.CommandResult, reply.Type);
        return JsonSerializer.Deserialize<CommandResult>(reply.Payload)!;
    }

    public CommandResult SendAsAdmin(string command, int? pid = null, object? argument = null) => Send(ThinCaller.Admin(), command, pid, argument);

    public T Data<T>(CommandResult result)
    {
        Assert.True(result.Success, result.Error);
        return JsonSerializer.Deserialize<T>(result.Data!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public ProcessInfo Process(int pid, string name, int? parent = null, DateTime? start = null)
    {
        var info = Monitor.Add(pid, name);
        info.ParentPid = parent;
        info.StartTime = start ?? DateTime.UtcNow.AddMinutes(-pid % 60 - 1);
        info.SessionId = 1;
        return info;
    }
}
