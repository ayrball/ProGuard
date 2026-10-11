using System;
using System.Runtime.InteropServices;

namespace ProGuard.Core.Native;

/// <summary>
/// P/Invokes for the persistent per-process defaults that <see cref="Kernel32"/> and <see cref="Ntdll"/> don't already
/// declare: the query side of I/O, memory and power-throttling settings, priority boost, CPU Sets and GPU scheduling.
/// </summary>
public static class ProcessTuningNative
{
    // PROCESS_INFORMATION_CLASS (GetProcessInformation / SetProcessInformation).
    public const int ProcessMemoryPriority = 0;
    public const int ProcessPowerThrottling = 4;

    public const uint TOKEN_QUERY = 0x0008;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>ProcessIoPriority (33) read back as a 4-byte IO_PRIORITY_HINT.</summary>
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref int processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessInformation(IntPtr hProcess, int processInformationClass, IntPtr processInformation, uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessPriorityBoost(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] bool disablePriorityBoost);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessPriorityBoost(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool disablePriorityBoost);

    /// <summary>Fills a buffer of variable-size SYSTEM_CPU_SET_INFORMATION records. Pass IntPtr.Zero/0 to get the size.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    /// <summary>A null/empty id list clears the process's default CPU Sets.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDefaultCpuSets(IntPtr process, uint[]? cpuSetIds, uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessDefaultCpuSets(IntPtr process, uint[]? cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

    /// <summary>Returns an NTSTATUS. Priority is a D3DKMT_SCHEDULINGPRIORITYCLASS (0 Idle .. 5 Realtime).</summary>
    [DllImport("gdi32.dll")]
    public static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr hProcess, int priority);

    [DllImport("gdi32.dll")]
    public static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr hProcess, out int priority);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    /// <summary>Token of the user logged on to <paramref name="sessionId"/>. Requires SeTcbPrivilege, i.e. LocalSystem.</summary>
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
}
