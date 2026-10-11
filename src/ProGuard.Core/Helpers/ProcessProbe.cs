using System;
using System.Diagnostics;
using System.Text;
using ProGuard.Core.Native;

namespace ProGuard.Core.Helpers;

/// <summary>
/// What <see cref="ProcessProbe.TryQuery"/> could read about one process. Fields the process would not give up
/// keep their defaults (null, <see cref="DateTime.MinValue"/>, zero).
/// </summary>
public readonly record struct ProcessProbeResult(
    int ParentPid,
    TimeSpan CpuTime,
    DateTime StartTimeUtc,
    ProcessPriorityClass? Priority,
    long? AffinityMask,
    string? ImagePath);

/// <summary>
/// Reads the per-process details the process list needs through a single PROCESS_QUERY_LIMITED_INFORMATION
/// handle. <see cref="Process"/> opens a separate (and much more privileged) handle for every property and
/// throws when it is refused, which is most of the system and service processes; this never throws.
/// </summary>
public static class ProcessProbe
{
    public static bool TryQuery(int pid, bool includeImagePath, out ProcessProbeResult result)
    {
        result = default;
        if (pid <= 0) return false;

        var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            int parentPid = 0;
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (Ntdll.NtQueryInformationProcess(handle, 0, ref pbi, System.Runtime.InteropServices.Marshal.SizeOf(pbi), out _) == 0)
            {
                parentPid = pbi.InheritedFromUniqueProcessId.ToInt32();
            }

            var cpuTime = TimeSpan.Zero;
            var startTime = DateTime.MinValue;
            if (Kernel32.GetProcessTimes(handle, out var created, out _, out var kernel, out var user))
            {
                cpuTime = TimeSpan.FromTicks((long)(kernel.ToUInt64() + user.ToUInt64()));
                if (created.ToUInt64() != 0) startTime = DateTime.FromFileTimeUtc((long)created.ToUInt64());
            }

            ProcessPriorityClass? priority = null;
            uint priorityClass = Kernel32.GetPriorityClass(handle);
            if (priorityClass != 0) priority = (ProcessPriorityClass)priorityClass;

            long? affinity = null;
            if (Kernel32.GetProcessAffinityMask(handle, out var processMask, out _)) affinity = processMask.ToInt64();

            string? imagePath = includeImagePath ? QueryImagePath(handle) : null;

            result = new ProcessProbeResult(parentPid, cpuTime, startTime, priority, affinity, imagePath);
            return true;
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>Full path of the process's executable, or null when it cannot be read (System, protected processes).</summary>
    public static string? TryGetImagePath(int pid)
    {
        if (pid <= 0) return null;

        var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            return QueryImagePath(handle);
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    private static string? QueryImagePath(IntPtr handle)
    {
        var buffer = new StringBuilder(1024);
        uint size = (uint)buffer.Capacity;
        if (!Kernel32.QueryFullProcessImageName(handle, 0, buffer, ref size))
        {
            // Long paths (up to 32k characters) do not fit the first buffer.
            buffer = new StringBuilder(32768);
            size = (uint)buffer.Capacity;
            if (!Kernel32.QueryFullProcessImageName(handle, 0, buffer, ref size)) return null;
        }

        return buffer.ToString(0, (int)size);
    }
}
