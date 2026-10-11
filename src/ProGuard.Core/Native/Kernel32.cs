using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ProGuard.Core.Native;

[Flags]
public enum ProcessAccessFlags : uint
{
    All = 0x001FFFFF, // PROCESS_ALL_ACCESS (Vista+)
    Terminate = 0x00000001,
    CreateThread = 0x00000002,
    VirtualMemoryOperation = 0x00000008,
    VirtualMemoryRead = 0x00000010,
    VirtualMemoryWrite = 0x00000020,
    DuplicateHandle = 0x00000040,
    CreateProcess = 0x000000080,
    SetQuota = 0x00000100,
    SetInformation = 0x00000200,
    QueryInformation = 0x00000400,
    QueryLimitedInformation = 0x00001000,
    Synchronize = 0x00100000,
    SuspendResume = 0x00000800,
    SetLimitedInformation = 0x00002000
}

[StructLayout(LayoutKind.Sequential)]
public struct PROCESS_POWER_THROTTLING_STATE
{
    public uint Version;
    public uint ControlMask;
    public uint StateMask;

    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
}

[StructLayout(LayoutKind.Sequential)]
public struct MEMORY_PRIORITY_INFORMATION
{
    public uint MemoryPriority;
}

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_INFO
{
    public ushort wProcessorArchitecture;
    public ushort wReserved;
    public uint dwPageSize;
    public IntPtr lpMinimumApplicationAddress;
    public IntPtr lpMaximumApplicationAddress;
    public IntPtr dwActiveProcessorMask;
    public uint dwNumberOfProcessors;
    public uint dwProcessorType;
    public uint dwAllocationGranularity;
    public ushort wProcessorLevel;
    public ushort wProcessorRevision;
}

/// <summary>
/// Native layout is { DWORD ControlFlags; union { DWORD CpuRate; DWORD Weight; struct { WORD MinRate, MaxRate; }; } }
/// = 8 bytes. SetInformationJobObject rejects any other length, so the union must overlap.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
{
    [FieldOffset(0)] public uint ControlFlags;
    /// <summary>With HARD_CAP: percent of total CPU * 100 (1..10000).</summary>
    [FieldOffset(4)] public uint CpuRate;
    [FieldOffset(4)] public uint Weight;
}

[StructLayout(LayoutKind.Sequential)]
public struct FILETIME
{
    public uint dwLowDateTime;
    public uint dwHighDateTime;

    public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;

    public static MEMORYSTATUSEX Create()
    {
        var result = new MEMORYSTATUSEX();
        result.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        return result;
    }
}

public enum LOGICAL_PROCESSOR_RELATIONSHIP
{
    RelationProcessorCore,
    RelationNumaNode,
    RelationCache,
    RelationProcessorPackage,
    RelationGroup,
    RelationAll = 0xffff
}

[StructLayout(LayoutKind.Explicit)]
public struct CACHE_DESCRIPTOR
{
    [FieldOffset(0)] public byte Level;
    [FieldOffset(1)] public byte Associativity;
    [FieldOffset(2)] public ushort LineSize;
    [FieldOffset(4)] public uint Size;
    [FieldOffset(8)] public int Type;
}

[StructLayout(LayoutKind.Explicit)]
public struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_UNION
{
    [FieldOffset(0)] public byte ProcessorCoreFlags;
    [FieldOffset(0)] public uint NumaNodeNumber;
    [FieldOffset(0)] public CACHE_DESCRIPTOR Cache;
    [FieldOffset(0)] public ulong Reserved1;
    [FieldOffset(8)] public ulong Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
{
    public UIntPtr ProcessorMask;
    public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
    public SYSTEM_LOGICAL_PROCESSOR_INFORMATION_UNION Information;
}

public static class Kernel32
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetLogicalProcessorInformation(IntPtr Buffer, ref uint ReturnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessAffinityMask(IntPtr hProcess, IntPtr dwProcessAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessAffinityMask(IntPtr hProcess, out IntPtr lpProcessAffinityMask, out IntPtr lpSystemAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(IntPtr hProcess, out FILETIME lpCreationTime, out FILETIME lpExitTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryProcessCycleTime(IntPtr ProcessHandle, out ulong CycleTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    public static extern void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    /// <summary>JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation (8 is BasicAndIoAccounting, which is query-only).</summary>
    public const int JobObjectCpuRateControlInformation = 15;
    public const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
    public const uint JOB_OBJECT_CPU_RATE_CONTROL_WEIGHT_BASED = 0x2;
    public const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

    public const int ProcessPowerThrottling = 4;
    public const int ProcessMemoryPriority = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LocalFree(IntPtr hMem);
}
