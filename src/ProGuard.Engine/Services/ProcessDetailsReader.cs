using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ProGuard.Core.Native;

namespace ProGuard.Engine.Services;

/// <summary>
/// The per-process settings the snapshot shows that the process monitor doesn't track. Null = the process won't say.
/// </summary>
/// <param name="IoPriority">IO_PRIORITY_HINT 0-3.</param>
/// <param name="MemoryPriority">MEMORY_PRIORITY 1-5.</param>
/// <param name="EfficiencyMode">EcoQoS: true when the process (or its parent) turned it on.</param>
public readonly record struct ProcessTuning(int? IoPriority, int? MemoryPriority, bool? EfficiencyMode);

/// <summary>Reads what <see cref="SnapshotBuilder"/> needs beyond <see cref="Core.Models.ProcessInfo"/>, behind an interface so tests can fake it.</summary>
public interface IProcessDetailsReader
{
    /// <summary>I/O priority, memory priority and efficiency mode of a process, through one handle. Never throws.</summary>
    ProcessTuning ReadTuning(int pid);

    /// <summary>DOMAIN\user the process runs as, or null when its token can't be read. Never throws.</summary>
    string? ReadUserName(int pid);
}

/// <summary>
/// The real reader. Each call opens the process once with PROCESS_QUERY_LIMITED_INFORMATION (enough for all three
/// settings on Windows 10 and later) and closes it again; that is a few microseconds per process.
/// </summary>
public sealed class Win32ProcessDetailsReader : IProcessDetailsReader
{
    private readonly ConcurrentDictionary<string, string?> _accountNames = new(StringComparer.Ordinal);

    public ProcessTuning ReadTuning(int pid)
    {
        if (pid <= 4) return default;
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return default;

        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            int? io = null;
            var ioValue = 0;
            if (ProcessTuningNative.NtQueryInformationProcess(handle, Ntdll.ProcessIoPriority, ref ioValue, sizeof(int), out _) == 0)
                io = ioValue;

            int? memory = null;
            if (ProcessTuningNative.GetProcessInformation(handle, ProcessTuningNative.ProcessMemoryPriority, buffer, (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>()))
                memory = (int)Marshal.PtrToStructure<MEMORY_PRIORITY_INFORMATION>(buffer).MemoryPriority;

            bool? eco = null;
            Marshal.StructureToPtr(new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_CURRENT_VERSION
            }, buffer, false);
            if (ProcessTuningNative.GetProcessInformation(handle, ProcessTuningNative.ProcessPowerThrottling, buffer, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
            {
                var state = Marshal.PtrToStructure<PROCESS_POWER_THROTTLING_STATE>(buffer);
                const uint speed = PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                eco = (state.ControlMask & speed) != 0 && (state.StateMask & speed) != 0;
            }

            return new ProcessTuning(io, memory, eco);
        }
        catch (Exception)
        {
            return default;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Kernel32.CloseHandle(handle);
        }
    }

    public string? ReadUserName(int pid)
    {
        if (pid < 0) return null;
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!ProcessTuningNative.OpenProcessToken(handle, ProcessTuningNative.TOKEN_QUERY, out var token)) return null;
            string? sid;
            try
            {
                using var identity = new WindowsIdentity(token);
                sid = identity.User?.Value;
            }
            finally
            {
                Kernel32.CloseHandle(token);
            }
            return sid == null ? null : _accountNames.GetOrAdd(sid, ResolveAccount);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    // A handful of distinct accounts own every process on a machine, so each SID is looked up once.
    private static string? ResolveAccount(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception)
        {
            return sid; // better than nothing for an account that no longer resolves
        }
    }
}
