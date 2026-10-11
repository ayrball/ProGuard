using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ProGuard.Core.Native;

namespace ProGuard.Engine.Services;

/// <summary>
/// The system memory readings SmartTrim gates on. Kept behind an interface so the gating rules can be tested
/// without depending on how much memory the test machine happens to be using.
/// </summary>
public interface IMemoryStatus
{
    /// <summary>GlobalMemoryStatusEx's dwMemoryLoad (0-100), or null if the call failed.</summary>
    int? GetMemoryLoadPercent();

    /// <summary>Bytes on the free and zero page lists, or null if neither source could be read.</summary>
    long? GetFreeRamBytes();

    /// <summary>Current size of the system file cache in bytes, or null if unknown.</summary>
    long? GetFileCacheBytes();
}

/// <summary>Outcome of one working-set trim. Byte counts are 0 when the process could not be read.</summary>
public readonly record struct WorkingSetTrimResult(bool Success, long BytesBefore, long BytesAfter, string? Error)
{
    public long BytesReclaimed => Math.Max(0, BytesBefore - BytesAfter);
}

/// <summary>The memory actions SmartTrim takes. Each returns null on success, otherwise a readable error.</summary>
public interface IMemoryOperations
{
    /// <summary>Enables a privilege on the engine's own token. Returns null when it is enabled.</summary>
    string? EnablePrivilege(string privilege);

    WorkingSetTrimResult TrimWorkingSet(int pid);

    /// <summary>NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList). Needs SeProfileSingleProcessPrivilege.</summary>
    string? PurgeStandbyList();

    /// <summary>SetSystemFileCacheSize(-1, -1, 0). Needs SeIncreaseQuotaPrivilege.</summary>
    string? PurgeFileCache();
}

public sealed class Win32MemoryStatus : IMemoryStatus
{
    private const string FreeAndZeroCounter = @"\Memory\Free & Zero Page List Bytes";

    private static readonly long PageSize = Environment.SystemPageSize;

    public int? GetMemoryLoadPercent()
    {
        var mem = MEMORYSTATUSEX.Create();
        return Kernel32.GlobalMemoryStatusEx(ref mem) ? (int)mem.dwMemoryLoad : null;
    }

    /// <summary>
    /// SystemMemoryListInformation when the engine holds SeProfileSingleProcessPrivilege (it does, as SYSTEM, once
    /// SmartTrim has enabled it); otherwise the PDH counter, which needs no privilege.
    /// </summary>
    public long? GetFreeRamBytes() => QueryMemoryLists() is { } lists
        ? (long)(lists.FreePages + lists.ZeroPages) * PageSize
        : QueryPdhFreeAndZero();

    public long? GetFileCacheBytes()
    {
        var status = MemoryApi.NtQuerySystemInformation(MemoryApi.SystemFileCacheInformation,
            out SYSTEM_FILECACHE_INFORMATION info, Marshal.SizeOf<SYSTEM_FILECACHE_INFORMATION>(), out _);
        return status == MemoryApi.STATUS_SUCCESS ? (long)info.CurrentSize : null;
    }

    internal static MemoryListCounts? QueryMemoryLists()
    {
        var buffer = Marshal.AllocHGlobal(MemoryListCounts.Size);
        try
        {
            var status = MemoryApi.NtQuerySystemInformation(MemoryApi.SystemMemoryListInformation, buffer, MemoryListCounts.Size, out _);
            return status == MemoryApi.STATUS_SUCCESS ? MemoryListCounts.Read(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // A raw byte counter needs one sample, so the query can be opened, read and closed in one go.
    internal static long? QueryPdhFreeAndZero()
    {
        if (MemoryApi.PdhOpenQuery(null, IntPtr.Zero, out var query) != 0) return null;
        try
        {
            if (MemoryApi.PdhAddEnglishCounter(query, FreeAndZeroCounter, IntPtr.Zero, out var counter) != 0) return null;
            if (MemoryApi.PdhCollectQueryData(query) != 0) return null;
            if (MemoryApi.PdhGetFormattedCounterValue(counter, MemoryApi.PDH_FMT_LARGE, IntPtr.Zero, out var value) != 0) return null;
            return value.LargeValue;
        }
        finally
        {
            MemoryApi.PdhCloseQuery(query);
        }
    }
}

public sealed class Win32MemoryOperations : IMemoryOperations
{
    public string? EnablePrivilege(string privilege)
    {
        if (!MemoryApi.OpenProcessToken(MemoryApi.GetCurrentProcess(), MemoryApi.TOKEN_ADJUST_PRIVILEGES | MemoryApi.TOKEN_QUERY, out var token))
            return $"{privilege}: OpenProcessToken failed ({Win32Message(Marshal.GetLastWin32Error())}){ImpersonationNote()}";
        try
        {
            if (!MemoryApi.LookupPrivilegeValue(null, privilege, out var luid))
                return $"{privilege}: LookupPrivilegeValue failed ({Win32Message(Marshal.GetLastWin32Error())})";

            var state = new TOKEN_PRIVILEGES_SINGLE { PrivilegeCount = 1, Luid = luid, Attributes = MemoryApi.SE_PRIVILEGE_ENABLED };
            var ok = MemoryApi.AdjustTokenPrivileges(token, false, ref state, (uint)Marshal.SizeOf<TOKEN_PRIVILEGES_SINGLE>(), IntPtr.Zero, IntPtr.Zero);
            // AdjustTokenPrivileges "succeeds" for a privilege the token doesn't hold; only the last error says so.
            var error = Marshal.GetLastWin32Error();
            if (!ok) return $"{privilege}: AdjustTokenPrivileges failed ({Win32Message(error)})";
            if (error == MemoryApi.ERROR_NOT_ALL_ASSIGNED)
                return $"{privilege} is not held by {Environment.UserName}'s token (run the engine as SYSTEM or elevated)";
            return null;
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }

    public WorkingSetTrimResult TrimWorkingSet(int pid)
    {
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.SetQuota | ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return new WorkingSetTrimResult(false, 0, 0, $"OpenProcess failed ({Win32Message(Marshal.GetLastWin32Error())})");
        try
        {
            var before = WorkingSet(handle);
            // Equivalent to SetProcessWorkingSetSize(h, -1, -1), which is what Process Lasso calls.
            if (!Kernel32.EmptyWorkingSet(handle))
                return new WorkingSetTrimResult(false, before, before, $"EmptyWorkingSet failed ({Win32Message(Marshal.GetLastWin32Error())})");
            return new WorkingSetTrimResult(true, before, WorkingSet(handle), null);
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    public string? PurgeStandbyList()
    {
        var command = MemoryApi.MemoryPurgeStandbyList;
        var status = MemoryApi.NtSetSystemInformation(MemoryApi.SystemMemoryListInformation, ref command, sizeof(int));
        return status == MemoryApi.STATUS_SUCCESS ? null : "Standby list purge failed: " + NtStatusMessage(status, MemoryApi.SeProfileSingleProcessPrivilege);
    }

    public string? PurgeFileCache()
    {
        if (MemoryApi.SetSystemFileCacheSize(nuint.MaxValue, nuint.MaxValue, 0)) return null;
        var error = Marshal.GetLastWin32Error();
        return "File cache purge failed: " + (error == 1314 // ERROR_PRIVILEGE_NOT_HELD
            ? $"{MemoryApi.SeIncreaseQuotaPrivilege} is not enabled"
            : Win32Message(error));
    }

    private static long WorkingSet(IntPtr handle) =>
        MemoryApi.GetProcessMemoryInfo(handle, out var counters, (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>())
            ? (long)counters.WorkingSetSize
            : 0;

    private static string NtStatusMessage(int status, string privilege) => status switch
    {
        MemoryApi.STATUS_PRIVILEGE_NOT_HELD => $"{privilege} is not enabled",
        MemoryApi.STATUS_ACCESS_DENIED => "access denied",
        _ => $"NTSTATUS 0x{status:X8} ({Win32Message(MemoryApi.RtlNtStatusToDosError(status))})"
    };

    private static string Win32Message(int error) => $"{new Win32Exception(error).Message}, error {error}";

    /// <summary>
    /// " (the thread is impersonating X)" when it is: then Windows checks the client's token, not the engine's, and
    /// SYSTEM's own token and privileges are out of reach.
    /// </summary>
    private static string ImpersonationNote()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            return identity.ImpersonationLevel == TokenImpersonationLevel.None ? "" : $" (the thread is impersonating {identity.Name})";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
