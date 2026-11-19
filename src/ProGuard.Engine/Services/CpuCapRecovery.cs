using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Data;
using ProGuard.Core.Native;

namespace ProGuard.Engine.Services;

/// <summary>
/// One hard CPU cap <see cref="CpuLimiter"/> has in force: enough to find the job again, and lift its cap, after the
/// engine died without doing so.
/// </summary>
/// <param name="Pid">The capped process.</param>
/// <param name="CreationTime">Its creation time as a FILETIME, so a process that later took over the pid isn't mistaken for it.</param>
/// <param name="JobName">The job object's name, random per cap (<see cref="CpuCapNative.NewJobName"/>).</param>
/// <param name="RemoteHandle">The job handle held inside the capped process, which keeps that name alive; 0 if none.</param>
internal sealed record CpuCapRecord(int Pid, ulong CreationTime, string JobName, long RemoteHandle);

internal interface ICpuCapRecoveryStore
{
    IReadOnlyList<CpuCapRecord> Load();
    void Save(IReadOnlyCollection<CpuCapRecord> caps);
}

/// <summary>
/// Keeps the caps in <c>cpu-cap-recovery.json</c> in the engine's data folder, next to <c>power-recovery.json</c> and
/// written the same way (<see cref="FilePowerPlanRecoveryStore"/>): a file only SYSTEM and administrators can change,
/// replaced in one rename. The worst a planted record could do is lift a cap at start: it names a job by a random name
/// that only a cap the engine created has, and nothing is lifted unless that job opens.
/// </summary>
internal sealed class FileCpuCapRecoveryStore : ICpuCapRecoveryStore
{
    private readonly string _path;
    private readonly ILogger _logger;

    public FileCpuCapRecoveryStore(ILogger? logger = null)
        : this(Path.Combine(ProGuardDbContext.DataDirectory, "cpu-cap-recovery.json"), logger)
    {
    }

    internal FileCpuCapRecoveryStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<CpuCapRecord> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<CpuCapRecord>();
            return JsonSerializer.Deserialize<List<CpuCapRecord>>(File.ReadAllText(_path)) ?? new List<CpuCapRecord>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Can't read the CPU caps to recover from {Path}, ignoring it", _path);
            return Array.Empty<CpuCapRecord>();
        }
    }

    public void Save(IReadOnlyCollection<CpuCapRecord> caps)
    {
        try
        {
            if (caps.Count == 0)
            {
                File.Delete(_path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                JsonSerializer.Serialize(stream, caps);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Can't save the CPU caps to recover to {Path}", _path);
        }
    }
}

/// <summary>Keeps the records in memory: for tests.</summary>
internal sealed class MemoryCpuCapRecoveryStore : ICpuCapRecoveryStore
{
    public List<CpuCapRecord> Caps { get; } = new();
    public IReadOnlyList<CpuCapRecord> Load() => Caps.ToList();
    public void Save(IReadOnlyCollection<CpuCapRecord> caps) { Caps.Clear(); Caps.AddRange(caps); }
}

/// <summary>
/// The job and handle calls behind recoverable caps. A job object loses its name once its last handle closes, and an
/// unnamed job can never be opened again: closing the engine's handle (which a crash does) leaves the process capped
/// for the rest of its life, and putting it in a new job only nests it under the old one's tighter cap. So each cap's
/// job gets a random name and a DACL for SYSTEM, Administrators and the engine's own account, and a SYNCHRONIZE-only
/// handle to it is placed inside the capped process itself, which keeps the name alive exactly as long as the cap
/// matters. That handle lets the process do nothing with its job but wait on it.
/// </summary>
internal static class CpuCapNative
{
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint JOB_OBJECT_QUERY = 0x0004;
    private const uint JOB_OBJECT_SET_ATTRIBUTES = 0x0002;
    private const uint DUPLICATE_CLOSE_SOURCE = 0x1;
    private const uint DUPLICATE_SAME_ACCESS = 0x2;
    private const int ERROR_ALREADY_EXISTS = 183;
    private const uint SDDL_REVISION_1 = 1;

    public const string JobNamePrefix = "ProGuard-CpuCap-";

    public static string NewJobName() => JobNamePrefix + Guid.NewGuid().ToString("N");

    /// <summary>A new job with <paramref name="name"/>; zero, with nothing created, when the name is already taken.</summary>
    public static IntPtr CreateNamedJob(string name)
    {
        using var self = WindowsIdentity.GetCurrent();
        var sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)" + (self.User is { } user ? $"(A;;GA;;;{user.Value})" : "");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, out var descriptor, out _))
            return IntPtr.Zero;
        try
        {
            var attributes = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = descriptor };
            var size = Marshal.SizeOf<SECURITY_ATTRIBUTES>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(attributes, pointer, false);
                var job = Kernel32.CreateJobObject(pointer, name);
                if (job != IntPtr.Zero && Marshal.GetLastWin32Error() == ERROR_ALREADY_EXISTS)
                {
                    Kernel32.CloseHandle(job); // someone else's object of that name: never ours to use
                    return IntPtr.Zero;
                }
                return job;
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        finally { LocalFree(descriptor); }
    }

    /// <summary>Opens a cap's job by name, to lift the cap; zero when it no longer exists.</summary>
    public static IntPtr OpenJob(string name) => OpenJobObjectW(JOB_OBJECT_SET_ATTRIBUTES | JOB_OBJECT_QUERY, false, name);

    /// <summary>Places a SYNCHRONIZE-only handle to <paramref name="job"/> in process <paramref name="pid"/>; returns its value there, or 0.</summary>
    public static long PlaceHandleIn(int pid, IntPtr job)
    {
        var process = Kernel32.OpenProcess(ProcessAccessFlags.DuplicateHandle, false, pid);
        if (process == IntPtr.Zero) return 0;
        try
        {
            return DuplicateHandle(GetCurrentProcess(), job, process, out var remote, SYNCHRONIZE, false, 0) ? remote.ToInt64() : 0;
        }
        finally { Kernel32.CloseHandle(process); }
    }

    /// <summary>
    /// Closes the handle <see cref="PlaceHandleIn"/> left in process <paramref name="pid"/>, but only if it still refers to
    /// <paramref name="job"/>: the process may have closed it, and its value may now be one of the process's own handles.
    /// </summary>
    public static void RemoveHandleFrom(int pid, long remote, IntPtr job)
    {
        if (remote == 0) return;
        var process = Kernel32.OpenProcess(ProcessAccessFlags.DuplicateHandle, false, pid);
        if (process == IntPtr.Zero) return;
        try
        {
            if (!DuplicateHandle(process, new IntPtr(remote), GetCurrentProcess(), out var local, 0, false, DUPLICATE_SAME_ACCESS)) return;
            bool same;
            try { same = CompareObjectHandles(local, job); }
            finally { Kernel32.CloseHandle(local); }
            if (same) DuplicateHandle(process, new IntPtr(remote), IntPtr.Zero, out _, 0, false, DUPLICATE_CLOSE_SOURCE);
        }
        finally { Kernel32.CloseHandle(process); }
    }

    /// <summary>The process's creation time as a FILETIME, or null when it can't be opened (gone).</summary>
    public static ulong? CreationTimeOf(int pid)
    {
        var process = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            return Kernel32.GetProcessTimes(process, out var created, out _, out _, out _) ? created.ToUInt64() : null;
        }
        finally { Kernel32.CloseHandle(process); }
    }

    /// <summary>The job's hard cap in hundredths of a percent, or null when it has none (or can't be read).</summary>
    public static uint? CpuRateOf(IntPtr job)
    {
        var length = Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!QueryInformationJobObject(job, Kernel32.JobObjectCpuRateControlInformation, buffer, (uint)length, out _)) return null;
            var info = Marshal.PtrToStructure<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>(buffer);
            return (info.ControlFlags & Kernel32.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE) != 0 ? info.CpuRate : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length, out uint returned);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernelbase.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CompareObjectHandles(IntPtr first, IntPtr second);
}
