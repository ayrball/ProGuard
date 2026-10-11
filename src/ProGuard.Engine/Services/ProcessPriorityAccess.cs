using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ProGuard.Core.Native;

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
}

public sealed class Win32ProcessPriorityAccess : IProcessPriorityAccess
{
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
}
