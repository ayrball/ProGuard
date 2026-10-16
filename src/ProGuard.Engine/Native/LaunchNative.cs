using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProGuard.Engine.Native;

/// <summary>
/// P/Invokes used only by the Watchdog, Keep Running, the session agent supervisor and <see cref="Services.UserSessionLauncher"/>: launching into a
/// user's session, reading another process's command line, and the queries the shared Core wrappers don't have.
/// </summary>
internal static class LaunchNative
{
    public const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;

    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_DEFAULT_ERROR_MODE = 0x04000000;

    public const uint INVALID_SESSION = 0xFFFFFFFF;
    public const int ERROR_NO_TOKEN = 1008;

    public const uint SYNCHRONIZE = 0x00100000;
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint WAIT_OBJECT_0 = 0;

    /// <summary>WTS_TYPE_CLASS.WTSTypeSessionInfoLevel1, for freeing what WTSEnumerateSessionsEx returned.</summary>
    public const int WTSTypeSessionInfoLevel1 = 2;
    /// <summary>WTS_CONNECTSTATE_CLASS.WTSActive: a user is signed in and connected (console or remote).</summary>
    public const int WTSActive = 0;

    /// <summary>PROCESSINFOCLASS.ProcessCommandLineInformation (Windows 8.1+): a UNICODE_STRING followed by its text.</summary>
    public const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WTS_SESSION_INFO_1
    {
        public int ExecEnvId;
        public int State;
        public int SessionId;
        public IntPtr pSessionName;
        public IntPtr pHostName;
        public IntPtr pUserName;
        public IntPtr pDomainName;
        public IntPtr pFarmName;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSEnumerateSessionsEx(IntPtr server, ref int level, int filter, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemoryExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSFreeMemoryEx(int typeClass, IntPtr memory, int count);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcess(string? applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, IntPtr size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool K32GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS_EX counters, uint size);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref int information, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);
}
