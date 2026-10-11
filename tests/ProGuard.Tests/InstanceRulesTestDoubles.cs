using System.Collections.Concurrent;
using System.Diagnostics;
using Moq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

/// <summary>In-memory processes for the instance limit, Instance Balancer and startup boost tests.</summary>
internal sealed class FakeRuleProcessAccess : IRuleProcessAccess
{
    public Dictionary<int, int> Session { get; } = new();
    public Dictionary<int, DateTime> Start { get; } = new();
    public Dictionary<int, ProcessPriorityClass> Priority { get; } = new();
    public Dictionary<int, long> Affinity { get; } = new();
    public HashSet<int> Critical { get; } = new();
    public List<int> Terminated { get; } = new();
    public List<(int Pid, long Mask)> AffinityWrites { get; } = new();

    public int? GetSessionId(int pid) => Session.TryGetValue(pid, out var s) ? s : null;
    public DateTime? GetStartTimeUtc(int pid) => Start.TryGetValue(pid, out var t) ? t : null;
    public bool IsCritical(int pid) => Critical.Contains(pid);

    public bool Terminate(int pid)
    {
        Terminated.Add(pid);
        return true;
    }

    public ProcessPriorityClass? GetPriorityClass(int pid) => Priority.TryGetValue(pid, out var p) ? p : null;

    public bool SetPriorityClass(int pid, ProcessPriorityClass priorityClass)
    {
        if (!Priority.ContainsKey(pid)) return false;
        Priority[pid] = priorityClass;
        return true;
    }

    public long? GetAffinityMask(int pid) => Affinity.TryGetValue(pid, out var m) ? m : null;

    public bool SetAffinityMask(int pid, long mask)
    {
        if (!Affinity.ContainsKey(pid) || mask == 0) return false;
        Affinity[pid] = mask;
        AffinityWrites.Add((pid, mask));
        return true;
    }
}

/// <summary>A clock that only moves when the test says so. Timers are never created by these tests.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class FakeProcessMonitor : IProcessMonitor
{
    private readonly ConcurrentDictionary<int, ProcessInfo> _processes = new();

    public event EventHandler<ProcessInfo>? ProcessStarted;
    public event EventHandler<int>? ProcessExited;
#pragma warning disable CS0067 // nothing here refreshes on a timer
    public event EventHandler? Refreshed;
#pragma warning restore CS0067

    public ProcessInfo Add(int pid, string name, double cpu = 0)
    {
        var info = new ProcessInfo { Pid = pid, Name = name, CpuPercent = cpu, Status = ProcessStatus.Running };
        _processes[pid] = info;
        return info;
    }

    public ProcessInfo Start(int pid, string name, double cpu = 0)
    {
        var info = Add(pid, name, cpu);
        ProcessStarted?.Invoke(this, info);
        return info;
    }

    public void Exit(int pid)
    {
        _processes.TryRemove(pid, out _);
        ProcessExited?.Invoke(this, pid);
    }

    public Task<List<ProcessInfo>> GetProcessesAsync() => Task.FromResult(_processes.Values.ToList());
    public IEnumerable<ProcessInfo> GetAllProcesses() => _processes.Values.ToList();
    public ProcessInfo? GetProcess(int pid) => _processes.TryGetValue(pid, out var p) ? p : null;
    public int GetForegroundPid() => 0;
    public TimeSpan PollingInterval => TimeSpan.FromSeconds(1);
    public void SetPollingInterval(TimeSpan interval) { }
}

internal static class FakeRules
{
    /// <summary>A rule store that serves <paramref name="rules"/> (which the test may edit afterwards) by name.</summary>
    public static Mock<IRuleStore> Store(List<ProcessRule> rules)
    {
        var store = new Mock<IRuleStore>();
        store.Setup(s => s.GetProcessRule(It.IsAny<string>())).Returns((string name) =>
            rules.FirstOrDefault(r => ProcessExtensions.NormalizeName(r.ProcessName) == ProcessExtensions.NormalizeName(name)));
        store.Setup(s => s.GetProcessRules()).Returns(() => rules.ToList());
        store.Setup(s => s.GetAllProcessRules()).Returns(() => rules.ToList());
        return store;
    }

    /// <summary>8 logical processors as 4 cores with 2 SMT threads each: 0/1, 2/3, 4/5, 6/7.</summary>
    public static CpuTopology EightThreadsFourCores()
    {
        var topology = new CpuTopology
        {
            LogicalProcessorCount = 8,
            PhysicalCoreCount = 4,
            AllCoresMask = 0xFF,
            PhysicalCoresMask = 0x55,
            SmtSiblingsMask = 0xAA,
        };
        for (int i = 0; i < 8; i++)
            topology.Cores.Add(new CpuCoreInfo { LogicalIndex = i, PhysicalCoreId = i / 2, IsSmtSibling = i % 2 == 1 });
        return topology;
    }
}
