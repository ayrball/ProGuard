using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ProGuard.Core.Native;
using ProGuard.Engine.Services.ProcessRules;

namespace ProGuard.Engine.Services;

/// <summary>
/// The live-process reads and writes ProBalance needs. Kept behind an interface so the restraint timing
/// rules can be tested against fake processes instead of real ones.
/// </summary>
public interface IProcessPriorityAccess
{
    /// <summary>Current priority class, or null if the process is gone or can't be queried.</summary>
    ProcessPriorityClass? GetPriorityClass(int pid);

    /// <summary>Returns false if the process is gone or access was denied.</summary>
    bool SetPriorityClass(int pid, ProcessPriorityClass priorityClass);

    /// <summary>PID of the process that created this one, or null if unknown.</summary>
    int? GetParentPid(int pid);

    /// <summary>Terminal Services session; 0 is the services session. Null if unknown.</summary>
    int? GetSessionId(int pid);

    /// <summary>Process affinity mask, or null if the process is gone, denied, or spans several processor groups.</summary>
    long? GetAffinityMask(int pid);
    bool SetAffinityMask(int pid, long mask);

    /// <summary>IO_PRIORITY_HINT (0 very low, 1 low, 2 normal, 3 high), or null if it can't be read.</summary>
    int? GetIoPriority(int pid);
    bool SetIoPriority(int pid, int priority);

    /// <summary>EcoQoS (ProcessPowerThrottling EXECUTION_SPEED), or null if it can't be read.</summary>
    EfficiencyModeState? GetEfficiencyMode(int pid);
    /// <summary><see cref="EfficiencyModeState.NotSet"/> hands the decision back to Windows.</summary>
    bool SetEfficiencyMode(int pid, EfficiencyModeState state);
}

public sealed class Win32ProcessPriorityAccess : IProcessPriorityAccess
{
    private readonly Win32ProcessTuning _tuning = new();

    public ProcessPriorityClass? GetPriorityClass(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.PriorityClass;
        }
        catch { return null; }
    }

    public bool SetPriorityClass(int pid, ProcessPriorityClass priorityClass)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.PriorityClass = priorityClass;
            return true;
        }
        catch { return false; }
    }

    public int? GetParentPid(int pid)
    {
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            // ProcessBasicInformation = 0
            var status = Ntdll.NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            return status == 0 ? pbi.InheritedFromUniqueProcessId.ToInt32() : null;
        }
        finally { Kernel32.CloseHandle(handle); }
    }

    public int? GetSessionId(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.SessionId;
        }
        catch { return null; }
    }

    public long? GetAffinityMask(int pid)
    {
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            // Both masks come back zero when the process has threads in more than one processor group.
            return Kernel32.GetProcessAffinityMask(handle, out var mask, out _) && mask != IntPtr.Zero ? mask.ToInt64() : null;
        }
        finally { Kernel32.CloseHandle(handle); }
    }

    public bool SetAffinityMask(int pid, long mask)
    {
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.SetInformation | ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        try { return Kernel32.SetProcessAffinityMask(handle, new IntPtr(mask)); }
        finally { Kernel32.CloseHandle(handle); }
    }

    public int? GetIoPriority(int pid) => _tuning.TryGetIoPriority(pid, out var priority) ? priority : null;

    public bool SetIoPriority(int pid, int priority)
    {
        try { _tuning.SetIoPriority(pid, priority); return true; }
        catch { return false; }
    }

    public EfficiencyModeState? GetEfficiencyMode(int pid)
    {
        if (!_tuning.TryGetEfficiencyMode(pid, out var controlled, out var enabled)) return null;
        return !controlled ? EfficiencyModeState.NotSet : enabled ? EfficiencyModeState.On : EfficiencyModeState.Off;
    }

    public bool SetEfficiencyMode(int pid, EfficiencyModeState state)
    {
        try
        {
            if (state == EfficiencyModeState.NotSet) _tuning.ResetEfficiencyMode(pid);
            else _tuning.SetEfficiencyMode(pid, state == EfficiencyModeState.On);
            return true;
        }
        catch { return false; }
    }
}
