using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Shared.Contracts;

namespace ProGuard.Engine.Services;

/// <summary>The machine-wide figures of a snapshot, read in one go. Replaceable so tests need not touch the machine.</summary>
public sealed record SystemMetrics(
    double CpuPercent,
    IReadOnlyList<double> CoreCpuPercent,
    double? ProcessorQueueLength,
    double MemoryPercent,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    int LogicalProcessors)
{
    /// <summary>Task Manager's CPU figure, the PDH per-core loads and queue length, and GlobalMemoryStatusEx.</summary>
    public static SystemMetrics Read()
    {
        var cpu = SystemInfo.GetOverallCpuPercent();
        var sample = SystemInfo.GetProcessorSample();
        var memory = MEMORYSTATUSEX.Create();
        Kernel32.GlobalMemoryStatusEx(ref memory);
        return new SystemMetrics(cpu, sample.PerCorePercent, sample.QueueLength, memory.dwMemoryLoad,
            (long)memory.ullTotalPhys, (long)memory.ullAvailPhys, SystemInfo.LogicalProcessorCount);
    }
}

/// <summary>
/// Builds the <see cref="EngineSnapshot"/> the UI polls once a second. One build serves every caller: a request within
/// <see cref="MinBuildInterval"/> of the last build gets that build (callers arriving while one is running wait for it
/// rather than starting another). The shared snapshot holds everything; <see cref="GetFor"/> then adjusts a copy to
/// what the asking account may see (<see cref="EngineSnapshot.CallerIsAdministrator"/> and file paths).
/// <para>
/// Cost control: the per-process figures come from <see cref="IProcessMonitor"/>, which refreshes them on its own timer,
/// so a build opens nothing for them. What the monitor doesn't track (I/O and memory priority, efficiency mode) is read
/// through one handle per process; an account name is looked up once per process, when first seen.
/// </para>
/// </summary>
public sealed class SnapshotBuilder
{
    /// <summary>A snapshot younger than this is reused; the UI asks about once a second, so each of its requests gets a fresh build.</summary>
    public static readonly TimeSpan MinBuildInterval = TimeSpan.FromMilliseconds(900);

    private static readonly TimeSpan PlanListLifetime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PlanRetryInterval = TimeSpan.FromSeconds(2);

    private readonly IProcessMonitor _monitor;
    private readonly IProBalanceEngine _proBalance;
    private readonly ICpuLimiter _cpuLimiter;
    private readonly CpuAffinityLimiter? _affinityLimiter;
    private readonly IRuleStore _ruleStore;
    private readonly IPowerManager _power;
    private readonly ForegroundBoostService? _foregroundBoost;
    private readonly IProcessDetailsReader _details;
    private readonly Func<SystemMetrics> _metrics;
    private readonly Func<long> _clockMs;
    private readonly TimeProvider _time;
    private readonly bool _engineIsElevated;
    private readonly DateTime _startedUtc;

    // Everything below is touched only while _buildLock is held.
    private readonly object _buildLock = new();
    private ResponsivenessMeter? _responsiveness;
    private readonly Dictionary<int, (DateTime Start, string? User)> _owners = new();
    private IReadOnlyList<(Guid Id, string Name)> _plans = Array.Empty<(Guid, string)>();
    private long _plansReadMs = long.MinValue / 2;

    private sealed record Built(EngineSnapshot Snapshot, long Ms);
    private volatile Built? _built;

    /// <summary>Wall-clock duration of the most recent build, for logs and tests.</summary>
    public TimeSpan LastBuildDuration { get; private set; }

    /// <summary>How many snapshots were actually built (as opposed to served from the previous build).</summary>
    public int BuildCount => Volatile.Read(ref _buildCount);
    private int _buildCount;

    public SnapshotBuilder(
        IProcessMonitor monitor,
        IProBalanceEngine proBalance,
        ICpuLimiter cpuLimiter,
        IRuleStore ruleStore,
        IPowerManager power,
        IProcessDetailsReader details,
        CpuAffinityLimiter? affinityLimiter = null,
        ForegroundBoostService? foregroundBoost = null,
        Func<SystemMetrics>? metrics = null,
        Func<long>? clockMs = null,
        TimeProvider? time = null,
        bool? engineIsElevated = null)
    {
        _monitor = monitor;
        _proBalance = proBalance;
        _cpuLimiter = cpuLimiter;
        _ruleStore = ruleStore;
        _power = power;
        _details = details;
        _affinityLimiter = affinityLimiter;
        _foregroundBoost = foregroundBoost;
        _metrics = metrics ?? SystemMetrics.Read;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _time = time ?? TimeProvider.System;
        _engineIsElevated = engineIsElevated ?? IsProcessElevated();
        _startedUtc = _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>The shared snapshot, built at most once per <see cref="MinBuildInterval"/>. Callers must not change it.</summary>
    public EngineSnapshot GetShared()
    {
        var built = _built;
        if (built != null && _clockMs() - built.Ms < MinBuildInterval.TotalMilliseconds) return built.Snapshot;

        lock (_buildLock)
        {
            built = _built;
            if (built != null && _clockMs() - built.Ms < MinBuildInterval.TotalMilliseconds) return built.Snapshot;

            var started = Stopwatch.GetTimestamp();
            var snapshot = Build();
            LastBuildDuration = Stopwatch.GetElapsedTime(started);
            Interlocked.Increment(ref _buildCount);
            _built = new Built(snapshot, _clockMs());
            return snapshot;
        }
    }

    /// <summary>
    /// The snapshot as <paramref name="caller"/> may see it: a copy of the shared build with
    /// <see cref="EngineSnapshot.CallerIsAdministrator"/> set and, for a non-administrator, <see cref="ProcessSnapshot.FilePath"/>
    /// cleared on every process the caller couldn't open with query rights themselves.
    /// </summary>
    public EngineSnapshot GetFor(IIpcCaller caller)
    {
        var shared = GetShared();
        var copy = DtoMapper.Map<EngineSnapshot, EngineSnapshot>(shared);
        copy.CallerIsAdministrator = caller.IsAdministrator;
        if (caller.IsAdministrator) return copy;

        copy.Processes = Redact(shared.Processes, caller);
        return copy;
    }

    private static List<ProcessSnapshot> Redact(List<ProcessSnapshot> processes, IIpcCaller caller)
    {
        var withPath = processes.Where(p => p.FilePath != null).Select(p => (p.Pid, p.StartTimeUtc)).ToList();
        var visible = withPath.Count == 0
            ? new HashSet<int>()
            : caller.FilterOpenable(withPath, ProcessAccessFlags.QueryLimitedInformation);

        var result = new List<ProcessSnapshot>(processes.Count);
        foreach (var p in processes)
            result.Add(p.FilePath == null || visible.Contains(p.Pid) ? p : WithoutPath(p));
        return result;
    }

    private static ProcessSnapshot WithoutPath(ProcessSnapshot p)
    {
        var copy = DtoMapper.Map<ProcessSnapshot, ProcessSnapshot>(p);
        copy.FilePath = null;
        return copy;
    }

    private EngineSnapshot Build()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var metrics = _metrics();
        _responsiveness ??= new ResponsivenessMeter(metrics.LogicalProcessors);
        if (metrics.ProcessorQueueLength is { } queue) _responsiveness.Add(queue);

        var rules = _ruleStore.GetProcessRules()?.ToList() ?? new List<ProcessRule>();
        var ruleNames = new HashSet<string>(rules.Select(r => ProcessExtensions.NormalizeName(r.ProcessName)), StringComparer.Ordinal);
        var restrained = new HashSet<int>(_proBalance.RestrainedPids ?? Array.Empty<int>());
        var caps = _cpuLimiter.GetActiveLimits();
        var affinityLimited = new HashSet<int>(_affinityLimiter?.GetConstrainedPids() ?? Array.Empty<int>());
        var foregroundPid = _monitor.GetForegroundPid();
        var (activePlanId, activePlanName) = ActivePlan();

        var enabled = _proBalance.IsEnabled;
        var snapshot = new EngineSnapshot
        {
            TakenAtUtc = now,
            EngineUptime = now - _startedUtc,
            CpuPercent = metrics.CpuPercent,
            CoreCpuPercent = metrics.CoreCpuPercent.ToList(),
            ResponsivenessPercent = _responsiveness.Percent,
            MemoryPercent = metrics.MemoryPercent,
            TotalMemoryBytes = metrics.TotalMemoryBytes,
            AvailableMemoryBytes = metrics.AvailableMemoryBytes,
            LogicalProcessors = metrics.LogicalProcessors,
            ProBalanceEnabled = enabled,
            ProBalancePaused = enabled && _proBalance.IsPausedForIdle,
            ProBalanceRestrained = restrained.Count,
            ProBalanceRestraintsTotal = _proBalance.TotalRestraints,
            ForegroundBoostEnabled = _foregroundBoost?.IsEnabled ?? true,
            ForegroundPid = foregroundPid,
            PerformanceModeActive = _power.IsPerformanceModeActive,
            PowerPlanOwner = string.IsNullOrEmpty(_power.PlanOwnerName) ? "User" : _power.PlanOwnerName,
            ActivePowerPlanId = activePlanId,
            ActivePowerPlanName = activePlanName,
            EngineIsElevated = _engineIsElevated,
            ProcessRuleCount = rules.Count,
            WatchdogRuleCount = _ruleStore.GetWatchdogRules()?.Count() ?? 0,
        };

        var processes = _monitor.GetAllProcesses().ToList();
        snapshot.Processes = new List<ProcessSnapshot>(processes.Count);
        var seen = new HashSet<int>(processes.Count);
        foreach (var p in processes)
        {
            seen.Add(p.Pid);
            var tuning = _details.ReadTuning(p.Pid);
            snapshot.Processes.Add(new ProcessSnapshot
            {
                Pid = p.Pid,
                Name = p.Name,
                FilePath = string.IsNullOrEmpty(p.FilePath) ? null : p.FilePath,
                ParentPid = p.ParentPid ?? 0,
                SessionId = p.SessionId,
                UserName = OwnerOf(p),
                StartTimeUtc = p.StartTime,
                CpuPercent = p.CpuPercent,
                WorkingSetBytes = p.WorkingSetBytes,
                PrivateBytes = p.PrivateBytes,
                ThreadCount = p.ThreadCount,
                Priority = p.PriorityClass,
                IoPriority = tuning.IoPriority,
                MemoryPriority = tuning.MemoryPriority,
                AffinityMask = p.AffinityMask,
                IsSystem = p.SessionId == 0,
                IsForeground = p.Pid == foregroundPid,
                IsSuspended = p.Status == ProcessStatus.Suspended,
                IsEfficiencyMode = tuning.EfficiencyMode == true,
                IsRestrained = restrained.Contains(p.Pid),
                IsAffinityLimited = affinityLimited.Contains(p.Pid),
                CpuLimitPercent = caps.TryGetValue(p.Pid, out var cap) ? cap : null,
                HasRule = ruleNames.Contains(ProcessExtensions.NormalizeName(p.Name)),
            });
        }
        snapshot.Processes.Sort((a, b) => a.Pid.CompareTo(b.Pid));

        foreach (var pid in _owners.Keys.Where(pid => !seen.Contains(pid)).ToList())
            _owners.Remove(pid);

        return snapshot;
    }

    // The account never changes for a process, so it is looked up once per (pid, start time).
    private string? OwnerOf(ProcessInfo p)
    {
        if (_owners.TryGetValue(p.Pid, out var known) && known.Start == p.StartTime) return known.User;
        var user = _details.ReadUserName(p.Pid);
        _owners[p.Pid] = (p.StartTime, user);
        return user;
    }

    private (Guid Id, string Name) ActivePlan()
    {
        var id = _power.GetActivePlan();
        if (id == Guid.Empty) return (id, string.Empty);

        // The plan list changes rarely, so it is re-read every PlanListLifetime; a plan missing from it (installed
        // since) triggers an early re-read, but not on every build if it never turns up.
        var age = _clockMs() - _plansReadMs;
        if (age > PlanListLifetime.TotalMilliseconds) ReadPlans();
        var name = NameOf(id);
        if (name == null && age > PlanRetryInterval.TotalMilliseconds)
        {
            ReadPlans();
            name = NameOf(id);
        }
        return (id, name ?? PowerPlans.WellKnownName(id) ?? id.ToString());
    }

    private void ReadPlans()
    {
        try { _plans = _power.GetAvailablePlans()?.ToList() ?? new List<(Guid, string)>(); }
        catch (Exception) { /* keep the old list */ }
        _plansReadMs = _clockMs();
    }

    private string? NameOf(Guid id)
    {
        foreach (var (planId, name) in _plans)
            if (planId == id) return name;
        return null;
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
