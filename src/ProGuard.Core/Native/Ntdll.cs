using System;
using System.Runtime.InteropServices;

namespace ProGuard.Core.Native;

[StructLayout(LayoutKind.Sequential)]
public struct PROCESS_BASIC_INFORMATION
{
    public IntPtr Reserved1;
    public IntPtr PebBaseAddress;
    public IntPtr Reserved2_0;
    public IntPtr Reserved2_1;
    public IntPtr UniqueProcessId;
    public IntPtr InheritedFromUniqueProcessId;
}

public static class Ntdll
{
    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    // PROCESSINFOCLASS.ProcessIoPriority = 33 (0x21); takes a 4-byte IO_PRIORITY_HINT (0 VeryLow .. 3 High).
    public const int ProcessIoPriority = 33;

    /// <summary>
    /// The system timer's resolutions, in 100-ns units. As NT names them, the "minimum" resolution is the coarsest
    /// interval (typically 156250 = 15.625 ms) and the "maximum" the finest (typically 5000 = 0.5 ms).
    /// </summary>
    [DllImport("ntdll.dll")]
    public static extern int NtQueryTimerResolution(out uint minimumResolution, out uint maximumResolution, out uint currentResolution);

    /// <summary>Asks for (<paramref name="setResolution"/> true) or withdraws this process's timer resolution request.</summary>
    [DllImport("ntdll.dll")]
    public static extern int NtSetTimerResolution(uint desiredResolution, [MarshalAs(UnmanagedType.U1)] bool setResolution, out uint currentResolution);

    [DllImport("ntdll.dll")]
    public static extern int NtSetInformationProcess(IntPtr processHandle, int processInformationClass, ref int processInformation, int processInformationLength);
}
