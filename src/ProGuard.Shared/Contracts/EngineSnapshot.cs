using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ProGuard.Shared.Contracts;

/// <summary>
/// Everything the UI shows that changes from second to second, in one round trip
/// (<see cref="Messages.CommandTypes.GetSnapshot"/>).
/// </summary>
public sealed class EngineSnapshot
{
    public DateTime TakenAtUtc { get; set; }
    public TimeSpan EngineUptime { get; set; }

    /// <summary>Whole-machine CPU load as Task Manager shows it, 0-100.</summary>
    public double CpuPercent { get; set; }

    /// <summary>Load of each logical processor, 0-100, in processor order. Empty when unavailable.</summary>
    public List<double> CoreCpuPercent { get; set; } = new();

    /// <summary>
    /// Whether Windows has each logical processor parked right now (PDH <c>Parking Status</c>), in the same order as
    /// <see cref="CoreCpuPercent"/>. Empty when unavailable.
    /// </summary>
    public List<bool> CoreParked { get; set; } = new();

    /// <summary>Process Lasso's "responsiveness" (from the processor queue length), 0-100; null before the first reading.</summary>
    public double? ResponsivenessPercent { get; set; }

    /// <summary>Physical memory in use, 0-100.</summary>
    public double MemoryPercent { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long AvailableMemoryBytes { get; set; }
    public int LogicalProcessors { get; set; }

    public bool ProBalanceEnabled { get; set; }

    /// <summary>ProBalance is enabled but paused because the user is idle (DisableProBalanceWhenSysIdle).</summary>
    public bool ProBalancePaused { get; set; }

    /// <summary>Processes ProBalance is holding at a lowered priority right now.</summary>
    public int ProBalanceRestrained { get; set; }

    /// <summary>Restraints ProBalance started since the engine started.</summary>
    public int ProBalanceRestraintsTotal { get; set; }

    public bool ForegroundBoostEnabled { get; set; }

    /// <summary>The foreground process the UI last reported; 0 if unknown.</summary>
    public int ForegroundPid { get; set; }

    public bool PerformanceModeActive { get; set; }

    /// <summary>Who set the active power plan: "User", "PerformanceMode", "IdleSaver", "LoadSwitcher", "StartWith".</summary>
    public string PowerPlanOwner { get; set; } = string.Empty;
    public Guid ActivePowerPlanId { get; set; }
    public string ActivePowerPlanName { get; set; } = string.Empty;

    /// <summary>The engine itself runs elevated (as a service it always does).</summary>
    public bool EngineIsElevated { get; set; }

    /// <summary>
    /// The client that asked for this snapshot is an administrator. When false the UI shows rule and watchdog editing,
    /// Purge now and Realtime priority as needing administrator rights, because the engine will refuse them.
    /// </summary>
    public bool CallerIsAdministrator { get; set; }

    public int ProcessRuleCount { get; set; }
    public int WatchdogRuleCount { get; set; }

    public List<ProcessSnapshot> Processes { get; set; } = new();
}

public sealed class ProcessSnapshot
{
    public int Pid { get; set; }

    /// <summary>Executable name without ".exe", as Windows reports it (e.g. "chrome").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Full path of the executable. Null when Windows won't reveal it, or when the caller couldn't open the process
    /// itself (another user's or a protected process): the engine doesn't disclose more than the caller could see.
    /// </summary>
    public string? FilePath { get; set; }

    public int ParentPid { get; set; }
    public int SessionId { get; set; }

    /// <summary>DOMAIN\user of the owner; null when unknown.</summary>
    public string? UserName { get; set; }

    /// <summary><see cref="DateTime.MinValue"/> when unknown.</summary>
    public DateTime StartTimeUtc { get; set; }

    /// <summary>Share of the whole machine's CPU, 0-100.</summary>
    public double CpuPercent { get; set; }

    public long WorkingSetBytes { get; set; }
    public long PrivateBytes { get; set; }
    public int ThreadCount { get; set; }

    /// <summary>Null when the process won't reveal it.</summary>
    public ProcessPriorityClass? Priority { get; set; }

    /// <summary>IO_PRIORITY_HINT 0-3 (Very Low .. High); null when unknown.</summary>
    public int? IoPriority { get; set; }

    /// <summary>MEMORY_PRIORITY 1-5 (Very Low .. Normal); null when unknown.</summary>
    public int? MemoryPriority { get; set; }

    /// <summary>Bit i set = may run on logical processor i.</summary>
    public long AffinityMask { get; set; }

    /// <summary>Runs in session 0 (services and the OS itself).</summary>
    public bool IsSystem { get; set; }

    public bool IsForeground { get; set; }
    public bool IsSuspended { get; set; }
    public bool IsEfficiencyMode { get; set; }

    /// <summary>ProBalance has lowered this process's priority and will restore it when the load drops.</summary>
    public bool IsRestrained { get; set; }

    /// <summary>The CPU Limiter has taken cores away from it for now.</summary>
    public bool IsAffinityLimited { get; set; }

    /// <summary>Active hard CPU cap (job object), percent of the whole machine; null when unlimited.</summary>
    public double? CpuLimitPercent { get; set; }

    /// <summary>A persistent <see cref="ProcessRuleDto"/> matches this process.</summary>
    public bool HasRule { get; set; }
}
