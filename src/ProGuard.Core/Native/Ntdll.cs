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

    [DllImport("ntdll.dll")]
    public static extern int NtSetInformationProcess(IntPtr processHandle, int processInformationClass, ref int processInformation, int processInformationLength);
}
