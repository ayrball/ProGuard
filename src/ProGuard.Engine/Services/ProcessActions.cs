using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;
using ProGuard.Engine.Native;

namespace ProGuard.Engine.Services;

/// <summary>Memory figures from GetProcessMemoryInfo.</summary>
public readonly record struct ProcessMemory(long WorkingSetBytes, long PrivateBytes);

/// <summary>What it takes to start a process again the way it was started: read from its PEB while it runs.</summary>
public sealed record ProcessLaunchInfo(string ExecutablePath, string CommandLine, string? WorkingDirectory, int SessionId);

/// <summary>
/// The process queries and changes the Watchdog and Keep Running make, behind an interface so tests can drive every
/// action without touching real processes. Setters throw when the process is gone or the change is refused; getters
/// return null instead.
/// </summary>
public interface IProcessActions
{
    ProcessPriorityClass? GetPriority(int pid);
    void SetPriority(int pid, ProcessPriorityClass priority);
    long? GetAffinity(int pid);
    void SetAffinity(int pid, long mask);
    /// <summary>IO_PRIORITY_HINT: 0 very low .. 3 high.</summary>
    int? GetIoPriority(int pid);
    void SetIoPriority(int pid, int ioPriority);
    void TrimWorkingSet(int pid);
    void Terminate(int pid);
    bool IsRunning(int pid);
    DateTime? GetStartTimeUtc(int pid);
    ProcessMemory? GetMemory(int pid);
    int? GetSessionId(int pid);
    ProcessLaunchInfo? GetLaunchInfo(int pid);
}

public class Win32ProcessActions : IProcessActions
{
    public ProcessPriorityClass? GetPriority(int pid) =>
        Query(pid, ProcessAccessFlags.QueryLimitedInformation, h =>
        {
            var value = Kernel32.GetPriorityClass(h);
            return value == 0 ? (ProcessPriorityClass?)null : (ProcessPriorityClass)value;
        });

    public void SetPriority(int pid, ProcessPriorityClass priority) =>
        Change(pid, ProcessAccessFlags.SetInformation, h => Kernel32.SetPriorityClass(h, (uint)priority));

    public long? GetAffinity(int pid) =>
        Query(pid, ProcessAccessFlags.QueryLimitedInformation, h =>
            Kernel32.GetProcessAffinityMask(h, out var mask, out _) ? mask.ToInt64() : (long?)null);

    public void SetAffinity(int pid, long mask)
    {
        if (mask == 0) throw new ArgumentOutOfRangeException(nameof(mask), "Affinity mask must select at least one CPU.");
        Change(pid, ProcessAccessFlags.SetInformation, h => Kernel32.SetProcessAffinityMask(h, new IntPtr(mask)));
    }

    public int? GetIoPriority(int pid) =>
        Query(pid, ProcessAccessFlags.QueryInformation, h =>
        {
            int value = 0;
            return LaunchNative.NtQueryInformationProcess(h, Ntdll.ProcessIoPriority, ref value, sizeof(int), out _) == 0 ? value : (int?)null;
        });

    public void SetIoPriority(int pid, int ioPriority) =>
        Change(pid, ProcessAccessFlags.SetInformation, h =>
        {
            int value = ioPriority;
            var status = Ntdll.NtSetInformationProcess(h, Ntdll.ProcessIoPriority, ref value, sizeof(int));
            if (status == ProcessRules.Win32ProcessTuning.StatusPrivilegeNotHeld)
                throw new InvalidOperationException(ProcessRules.Win32ProcessTuning.PrivilegeNotHeldMessage);
            if (status != 0) throw new InvalidOperationException($"NtSetInformationProcess(ProcessIoPriority) failed: NTSTATUS 0x{status:X8}");
            return true;
        });

    public void TrimWorkingSet(int pid) =>
        Change(pid, ProcessAccessFlags.SetQuota | ProcessAccessFlags.QueryLimitedInformation, Kernel32.EmptyWorkingSet);

    public void Terminate(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill();
    }

    public bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; } // its exit status is off limits, but it exists
    }

    public DateTime? GetStartTimeUtc(int pid) =>
        ProcessProbe.TryQuery(pid, includeImagePath: false, out var probe) && probe.StartTimeUtc != DateTime.MinValue
            ? probe.StartTimeUtc
            : null;

    public ProcessMemory? GetMemory(int pid) =>
        Query(pid, ProcessAccessFlags.QueryLimitedInformation, h =>
        {
            var size = (uint)Marshal.SizeOf<LaunchNative.PROCESS_MEMORY_COUNTERS_EX>();
            return LaunchNative.K32GetProcessMemoryInfo(h, out var counters, size)
                ? new ProcessMemory((long)counters.WorkingSetSize.ToUInt64(), (long)counters.PrivateUsage.ToUInt64())
                : (ProcessMemory?)null;
        });

    public int? GetSessionId(int pid) =>
        Kernel32.ProcessIdToSessionId((uint)pid, out var session) ? (int)session : null;

    public ProcessLaunchInfo? GetLaunchInfo(int pid)
    {
        var path = ProcessProbe.TryGetImagePath(pid);
        if (string.IsNullOrEmpty(path)) return null;
        if (GetSessionId(pid) is not int session) return null;

        var (commandLine, directory) = ProcessCommandLine.Read(pid);
        if (string.IsNullOrWhiteSpace(commandLine)) commandLine = WatchdogArguments.BuildCommandLine(path, null);
        if (string.IsNullOrWhiteSpace(directory)) directory = Path.GetDirectoryName(path);
        return new ProcessLaunchInfo(path, commandLine, directory, session);
    }

    private static T? Query<T>(int pid, ProcessAccessFlags access, Func<IntPtr, T?> query)
    {
        if (pid <= 0) return default;
        var handle = Kernel32.OpenProcess(access, false, pid);
        if (handle == IntPtr.Zero) return default;
        try { return query(handle); }
        finally { Kernel32.CloseHandle(handle); }
    }

    private static void Change(int pid, ProcessAccessFlags access, Func<IntPtr, bool> change)
    {
        var handle = Kernel32.OpenProcess(access, false, pid);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open process {pid}");
        try
        {
            if (!change(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Kernel32.CloseHandle(handle); }
    }
}

/// <summary>
/// Reads another process's command line and current directory from its PEB (RTL_USER_PROCESS_PARAMETERS). That needs
/// PROCESS_VM_READ, which the engine has as SYSTEM; when it is refused, the command line still comes from
/// ProcessCommandLineInformation, which needs only limited query access.
/// </summary>
internal static class ProcessCommandLine
{
    // x64 layouts. A WOW64 process has a native 64-bit PEB and parameter block as well, so these hold for it too.
    private const int PebProcessParametersOffset = 0x20;
    private const int ParametersCurrentDirectoryOffset = 0x38;
    private const int ParametersCommandLineOffset = 0x70;
    private const int MaxUnicodeStringBytes = 0xFFFE;

    public static (string? CommandLine, string? WorkingDirectory) Read(int pid)
    {
        if (Environment.Is64BitProcess)
        {
            var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryInformation | ProcessAccessFlags.VirtualMemoryRead, false, pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var fromPeb = ReadFromPeb(handle);
                    if (fromPeb.CommandLine != null) return fromPeb;
                }
                finally { Kernel32.CloseHandle(handle); }
            }
        }

        return (ReadWithLimitedAccess(pid), null);
    }

    private static (string? CommandLine, string? WorkingDirectory) ReadFromPeb(IntPtr process)
    {
        var pbi = new PROCESS_BASIC_INFORMATION();
        if (Ntdll.NtQueryInformationProcess(process, 0, ref pbi, Marshal.SizeOf(pbi), out _) != 0 || pbi.PebBaseAddress == IntPtr.Zero)
            return (null, null);

        var parameters = ReadPointer(process, pbi.PebBaseAddress + PebProcessParametersOffset);
        if (parameters == IntPtr.Zero) return (null, null); // not set up yet: the process is still initialising

        var commandLine = ReadUnicodeString(process, parameters + ParametersCommandLineOffset);
        var directory = ReadUnicodeString(process, parameters + ParametersCurrentDirectoryOffset);
        return (commandLine, directory);
    }

    private static string? ReadWithLimitedAccess(int pid)
    {
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        var size = 16 + MaxUnicodeStringBytes + 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (LaunchNative.NtQueryInformationProcess(handle, LaunchNative.ProcessCommandLineInformation, buffer, size, out _) != 0) return null;
            var length = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return length == 0 || text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Kernel32.CloseHandle(handle);
        }
    }

    private static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var bytes = new byte[IntPtr.Size];
        return LaunchNative.ReadProcessMemory(process, address, bytes, (IntPtr)bytes.Length, out _)
            ? new IntPtr(BitConverter.ToInt64(bytes, 0))
            : IntPtr.Zero;
    }

    /// <summary>UNICODE_STRING on x64: USHORT Length, USHORT MaximumLength, 4 bytes of padding, PWSTR Buffer.</summary>
    private static string? ReadUnicodeString(IntPtr process, IntPtr address)
    {
        var header = new byte[16];
        if (!LaunchNative.ReadProcessMemory(process, address, header, (IntPtr)header.Length, out _)) return null;

        int length = BitConverter.ToUInt16(header, 0);
        var text = new IntPtr(BitConverter.ToInt64(header, 8));
        if (length == 0 || length > MaxUnicodeStringBytes || text == IntPtr.Zero) return null;

        var chars = new byte[length];
        return LaunchNative.ReadProcessMemory(process, text, chars, (IntPtr)length, out _) ? Encoding.Unicode.GetString(chars) : null;
    }
}
