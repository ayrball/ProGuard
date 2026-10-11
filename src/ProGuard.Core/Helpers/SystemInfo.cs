using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32;
using ProGuard.Core.Native;

namespace ProGuard.Core.Helpers;

/// <summary>
/// System-wide figures. CPU load comes in two measures:
/// <list type="bullet">
/// <item><see cref="GetOverallCpuPercent"/>: what Task Manager shows (PDH <c>% Processor Utility</c>, which scales busy
/// time by the current clock speed). For display: dashboard, status bar, tray, engine status.</item>
/// <item><see cref="GetProcessorTimePercent"/>: busy time from <c>GetSystemTimes</c>, the same measure as per-process
/// CPU % (CPU time over wall time). The engine's decisions (ProBalance, load switcher) use this, so a system
/// threshold and per-process thresholds compare like with like.</item>
/// </list>
/// </summary>
public static class SystemInfo
{
    private static readonly object _syncLock = new();
    private static ulong _prevIdleTime;
    private static ulong _prevKernelTime;
    private static ulong _prevUserTime;
    private static bool _initialized;
    private static long _lastSampleTick;
    private static double _lastCpuPercent;

    /// <summary>
    /// Shortest span the system CPU figure is measured over. The delta baseline below is shared by every
    /// caller (dashboard, status bar, tray, engine), so without this each caller would reset it for the
    /// others and the percentage would be computed over tiny, irregular intervals. Must stay below the
    /// engine's 500 ms sampling period.
    /// </summary>
    private const long MinSampleIntervalMs = 250;

    // PDH counters, shared by every caller and collected at most every MinSampleIntervalMs for the same reason.
    private static readonly object _countersLock = new();
    private static ProcessorCounters? _counters;
    private static bool _countersOpened;
    private static long _lastCountersTick;
    private static ProcessorSample _lastSample = ProcessorSample.Empty;
    private static string? _cachedCpuName;

    public static int LogicalProcessorCount => Environment.ProcessorCount;
    public static int PhysicalProcessorCount => GetCpuTopology().PhysicalCoreCount;

    private static CpuTopology? _cachedTopology;

    public static CpuTopology GetCpuTopology()
    {
        if (_cachedTopology != null) return _cachedTopology;

        int logicalCount = Math.Min(Environment.ProcessorCount, 64);
        long allCoresMask = logicalCount >= 64 ? -1L : (1L << logicalCount) - 1;

        var topology = new CpuTopology
        {
            LogicalProcessorCount = logicalCount,
            AllCoresMask = allCoresMask
        };

        try
        {
            uint returnLength = 0;
            Kernel32.GetLogicalProcessorInformation(IntPtr.Zero, ref returnLength);
            if (returnLength > 0)
            {
                IntPtr buffer = Marshal.AllocHGlobal((int)returnLength);
                try
                {
                    if (Kernel32.GetLogicalProcessorInformation(buffer, ref returnLength))
                    {
                        int structSize = Marshal.SizeOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();
                        int count = (int)returnLength / structSize;
                        long physicalMask = 0;
                        long smtMask = 0;
                        int physicalCoreIndex = 0;

                        var coreMap = new Dictionary<int, (int PhysicalId, bool IsSibling)>();

                        for (int i = 0; i < count; i++)
                        {
                            IntPtr ptr = IntPtr.Add(buffer, i * structSize);
                            var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>(ptr);

                            if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                            {
                                ulong coreMask = (ulong)info.ProcessorMask;
                                bool isFirst = true;

                                for (int bit = 0; bit < 64; bit++)
                                {
                                    if ((coreMask & (1UL << bit)) != 0 && bit < logicalCount)
                                    {
                                        if (isFirst)
                                        {
                                            physicalMask |= (1L << bit);
                                            coreMap[bit] = (physicalCoreIndex, false);
                                            isFirst = false;
                                        }
                                        else
                                        {
                                            smtMask |= (1L << bit);
                                            coreMap[bit] = (physicalCoreIndex, true);
                                        }
                                    }
                                }

                                physicalCoreIndex++;
                            }
                        }

                        if (physicalCoreIndex > 0 && physicalMask != 0)
                        {
                            topology.PhysicalCoreCount = physicalCoreIndex;
                            topology.PhysicalCoresMask = physicalMask;
                            topology.SmtSiblingsMask = smtMask;

                            for (int i = 0; i < logicalCount; i++)
                            {
                                if (coreMap.TryGetValue(i, out var mapping))
                                {
                                    topology.Cores.Add(new CpuCoreInfo
                                    {
                                        LogicalIndex = i,
                                        PhysicalCoreId = mapping.PhysicalId,
                                        IsSmtSibling = mapping.IsSibling
                                    });
                                }
                                else
                                {
                                    bool isSibling = (i % 2) != 0;
                                    topology.Cores.Add(new CpuCoreInfo
                                    {
                                        LogicalIndex = i,
                                        PhysicalCoreId = i / 2,
                                        IsSmtSibling = isSibling
                                    });
                                }
                            }

                            _cachedTopology = topology;
                            return topology;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch { }

        // Fallback if API fails or running under virtualization without CPUID masking
        int fallbackPhysical = Math.Max(1, logicalCount / 2);
        long fallbackPhysMask = 0;
        long fallbackSmtMask = 0;

        for (int i = 0; i < logicalCount; i++)
        {
            bool isSibling = (i % 2) != 0 && logicalCount > 1;
            if (!isSibling) fallbackPhysMask |= (1L << i);
            else fallbackSmtMask |= (1L << i);

            topology.Cores.Add(new CpuCoreInfo
            {
                LogicalIndex = i,
                PhysicalCoreId = i / 2,
                IsSmtSibling = isSibling
            });
        }

        topology.PhysicalCoreCount = fallbackPhysical;
        topology.PhysicalCoresMask = fallbackPhysMask;
        topology.SmtSiblingsMask = fallbackSmtMask;

        _cachedTopology = topology;
        return topology;
    }

    public static long TotalPhysicalMemoryBytes
    {
        get
        {
            var mem = MEMORYSTATUSEX.Create();
            if (Kernel32.GlobalMemoryStatusEx(ref mem))
            {
                return (long)mem.ullTotalPhys;
            }
            return 8L * 1024 * 1024 * 1024;
        }
    }

    public static string CpuName
    {
        get
        {
            if (_cachedCpuName != null) return _cachedCpuName;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var name = key?.GetValue("ProcessorNameString") as string;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _cachedCpuName = name.Trim();
                    return _cachedCpuName;
                }
            }
            catch { }

            _cachedCpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Multi-Core Processor";
            return _cachedCpuName;
        }
    }

    /// <summary>
    /// System CPU % as Task Manager shows it: PDH <c>\Processor Information(_Total)\% Processor Utility</c>, capped
    /// at 100. Falls back to <see cref="GetProcessorTimePercent"/> when that counter can't be read.
    /// </summary>
    public static double GetOverallCpuPercent() =>
        TaskManagerCpuPercent(GetProcessorSample().UtilityPercent) ?? GetProcessorTimePercent();

    /// <summary>The display figure for an uncapped <c>% Processor Utility</c> reading, or null if there is none.</summary>
    public static double? TaskManagerCpuPercent(double? utility) =>
        utility is { } u && double.IsFinite(u) ? Math.Clamp(Math.Round(u, 1), 0.0, 100.0) : null;

    /// <summary>
    /// The latest reading of the PDH processor counters (utility, per-core % Processor Time, processor queue length).
    /// Empty if PDH is unavailable, and without CPU figures until a second collection.
    /// </summary>
    public static ProcessorSample GetProcessorSample()
    {
        lock (_countersLock)
        {
            if (!_countersOpened)
            {
                _countersOpened = true;
                _counters = ProcessorCounters.TryOpen();
                _lastCountersTick = Environment.TickCount64;
                return _lastSample;
            }
            if (_counters == null || Environment.TickCount64 - _lastCountersTick < MinSampleIntervalMs) return _lastSample;

            _lastSample = _counters.Collect();
            _lastCountersTick = Environment.TickCount64;
            return _lastSample;
        }
    }

    /// <summary>Per-logical-CPU <c>% Processor Time</c>, in CPU order; empty if PDH is unavailable.</summary>
    public static IReadOnlyList<double> GetPerCoreCpuPercent() => GetProcessorSample().PerCorePercent;

    /// <summary>Threads waiting for a CPU (<c>\System\Processor Queue Length</c>), or null if unavailable.</summary>
    public static double? GetProcessorQueueLength() => GetProcessorSample().QueueLength;

    /// <summary>
    /// Busy share of all CPUs' time since the previous sample, from <c>GetSystemTimes</c>: the engine's decision input
    /// (see the class summary). Not frequency-scaled, so it can differ from Task Manager.
    /// </summary>
    public static double GetProcessorTimePercent()
    {
        lock (_syncLock)
        {
            if (_initialized && Environment.TickCount64 - _lastSampleTick < MinSampleIntervalMs)
            {
                return _lastCpuPercent;
            }

            if (!Kernel32.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            {
                return 0.0;
            }

            ulong curIdle = idleTime.ToUInt64();
            ulong curKernel = kernelTime.ToUInt64();
            ulong curUser = userTime.ToUInt64();

            if (!_initialized)
            {
                _prevIdleTime = curIdle;
                _prevKernelTime = curKernel;
                _prevUserTime = curUser;
                _initialized = true;
                _lastSampleTick = Environment.TickCount64;
                _lastCpuPercent = 0.0;
                return 0.0;
            }

            ulong diffIdle = curIdle - _prevIdleTime;
            ulong diffKernel = curKernel - _prevKernelTime;
            ulong diffUser = curUser - _prevUserTime;

            _prevIdleTime = curIdle;
            _prevKernelTime = curKernel;
            _prevUserTime = curUser;
            _lastSampleTick = Environment.TickCount64;

            ulong totalSystem = diffKernel + diffUser;
            if (totalSystem == 0 || totalSystem < diffIdle)
            {
                _lastCpuPercent = 0.0;
                return 0.0;
            }

            ulong totalUsage = totalSystem - diffIdle;
            double percent = (totalUsage * 100.0) / totalSystem;
            _lastCpuPercent = Math.Clamp(Math.Round(percent, 1), 0.0, 100.0);
            return _lastCpuPercent;
        }
    }

    public static double GetAvailableMemoryMB()
    {
        var mem = MEMORYSTATUSEX.Create();
        if (Kernel32.GlobalMemoryStatusEx(ref mem))
        {
            return Math.Round(mem.ullAvailPhys / (1024.0 * 1024.0), 1);
        }
        return 0.0;
    }

    public static double GetMemoryLoadPercent()
    {
        var mem = MEMORYSTATUSEX.Create();
        if (Kernel32.GlobalMemoryStatusEx(ref mem))
        {
            return mem.dwMemoryLoad;
        }
        return 0.0;
    }
}

public class CpuCoreInfo
{
    public int LogicalIndex { get; set; }
    public int PhysicalCoreId { get; set; }
    public bool IsSmtSibling { get; set; }
    public string DisplayName => IsSmtSibling 
        ? $"CPU {LogicalIndex} (SMT Sibling, Core {PhysicalCoreId})" 
        : $"CPU {LogicalIndex} (Physical Core {PhysicalCoreId})";
}

public class CpuTopology
{
    public int PhysicalCoreCount { get; set; }
    public int LogicalProcessorCount { get; set; }
    public bool IsSmtSupported => LogicalProcessorCount > PhysicalCoreCount;
    public long PhysicalCoresMask { get; set; }
    public long SmtSiblingsMask { get; set; }
    public long AllCoresMask { get; set; }
    public List<CpuCoreInfo> Cores { get; set; } = new();
}
