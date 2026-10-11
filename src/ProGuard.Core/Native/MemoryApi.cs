using System;
using System.Runtime.InteropServices;

namespace ProGuard.Core.Native;

/// <summary>
/// SYSTEM_MEMORY_LIST_INFORMATION, returned by NtQuerySystemInformation(SystemMemoryListInformation): 22 ULONG_PTR page
/// counts (zero, free, modified, modified-no-write, bad, standby by priority 0-7, repurposed by priority 0-7, modified
/// page-file). Read from a raw buffer by index rather than marshalled, so the layout doesn't need unsafe fixed arrays.
/// The query needs SeProfileSingleProcessPrivilege.
/// </summary>
public readonly struct MemoryListCounts
{
    public const int FieldCount = 22;
    public static int Size => FieldCount * IntPtr.Size;

    public ulong ZeroPages { get; init; }
    public ulong FreePages { get; init; }
    public ulong ModifiedPages { get; init; }
    /// <summary>The standby list: the sum of PageCountByPriority[0..7].</summary>
    public ulong StandbyPages { get; init; }

    public static MemoryListCounts Read(IntPtr buffer)
    {
        ulong At(int index) => (ulong)Marshal.ReadIntPtr(buffer, index * IntPtr.Size);
        ulong standby = 0;
        for (var i = 0; i < 8; i++) standby += At(5 + i);
        return new MemoryListCounts { ZeroPages = At(0), FreePages = At(1), ModifiedPages = At(2), StandbyPages = standby };
    }
}

/// <summary>SYSTEM_FILECACHE_INFORMATION, returned by NtQuerySystemInformation(SystemFileCacheInformation). Sizes are bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_FILECACHE_INFORMATION
{
    public nuint CurrentSize;
    public nuint PeakSize;
    public uint PageFaultCount;
    public nuint MinimumWorkingSet;
    public nuint MaximumWorkingSet;
    public nuint CurrentSizeIncludingTransitionInPages;
    public nuint PeakSizeIncludingTransitionInPages;
    public uint TransitionRePurposeCount;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct PROCESS_MEMORY_COUNTERS
{
    public uint cb;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
}

[StructLayout(LayoutKind.Sequential)]
public struct LUID
{
    public uint LowPart;
    public int HighPart;
}

/// <summary>TOKEN_PRIVILEGES with room for exactly one privilege, which is all AdjustTokenPrivileges calls here need.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TOKEN_PRIVILEGES_SINGLE
{
    public uint PrivilegeCount;
    public LUID Luid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
public struct PDH_FMT_COUNTERVALUE_LARGE
{
    public uint CStatus;
    public long LargeValue;
}

/// <summary>
/// System memory-management APIs used by SmartTrim: the memory lists (standby purge), the file cache, per-process
/// working sets, token privileges and the PDH memory counters.
/// </summary>
public static class MemoryApi
{
    // SYSTEM_INFORMATION_CLASS
    public const int SystemFileCacheInformation = 21;
    public const int SystemMemoryListInformation = 80;
    public const int SystemFileCacheInformationEx = 81;

    // SYSTEM_MEMORY_LIST_COMMAND, the value passed to NtSetSystemInformation(SystemMemoryListInformation).
    public const int MemoryEmptyWorkingSets = 2;
    public const int MemoryFlushModifiedList = 3;
    public const int MemoryPurgeStandbyList = 4;
    public const int MemoryPurgeLowPriorityStandbyList = 5;

    /// <summary>Needed to query or change the memory lists.</summary>
    public const string SeProfileSingleProcessPrivilege = "SeProfileSingleProcessPrivilege";
    /// <summary>Needed to change the system file cache size.</summary>
    public const string SeIncreaseQuotaPrivilege = "SeIncreaseQuotaPrivilege";

    public const int STATUS_SUCCESS = 0;
    public const int STATUS_PRIVILEGE_NOT_HELD = unchecked((int)0xC0000061);
    public const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);

    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x0002;
    public const int ERROR_NOT_ALL_ASSIGNED = 1300;

    public const uint PDH_FMT_LARGE = 0x00000400;

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int systemInformationClass, out SYSTEM_FILECACHE_INFORMATION systemInformation, int systemInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtSetSystemInformation(int systemInformationClass, ref int systemInformation, int systemInformationLength);

    [DllImport("ntdll.dll")]
    public static extern int RtlNtStatusToDosError(int status);

    /// <summary>Passing <see cref="nuint.MaxValue"/> (SIZE_T -1) for both sizes flushes the file cache.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetSystemFileCacheSize(nuint minimumFileCacheSize, nuint maximumFileCacheSize, uint flags);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS counters, uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES_SINGLE newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    /// <summary>English counter path, so it works whatever the display language.</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE_LARGE value);

    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr query);
}
