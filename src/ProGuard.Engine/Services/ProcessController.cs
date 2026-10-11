using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// Applies one-off process actions requested over IPC. Runs in the engine, which usually has more rights
/// than the UI (it runs as a service), so it can act on processes the UI cannot open.
/// </summary>
public class ProcessController : IProcessController
{
    private readonly ILogger<ProcessController> _logger;

    public ProcessController(ILogger<ProcessController> logger)
    {
        _logger = logger;
    }

    public void SetPriority(int pid, ProcessPriorityClass priority)
    {
        using var p = Process.GetProcessById(pid);
        p.PriorityClass = priority;
        _logger.LogInformation("Set priority of {Name} ({Pid}) to {Priority}", p.ProcessName, pid, priority);
    }

    public void SetAffinity(int pid, long affinityMask)
    {
        if (affinityMask == 0) throw new ArgumentOutOfRangeException(nameof(affinityMask), "Affinity mask must select at least one CPU.");

        using var p = Process.GetProcessById(pid);
        p.ProcessorAffinity = new IntPtr(affinityMask);
        _logger.LogInformation("Set affinity of {Name} ({Pid}) to 0x{Mask:X}", p.ProcessName, pid, affinityMask);
    }

    public void Terminate(int pid)
    {
        using var p = Process.GetProcessById(pid);
        p.Kill();
        _logger.LogInformation("Terminated {Name} ({Pid})", p.ProcessName, pid);
    }

    public void Suspend(int pid) => SuspendOrResume(pid, suspend: true);

    public void Resume(int pid) => SuspendOrResume(pid, suspend: false);

    private void SuspendOrResume(int pid, bool suspend)
    {
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.SuspendResume, false, pid);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var status = suspend ? Ntdll.NtSuspendProcess(handle) : Ntdll.NtResumeProcess(handle);
            if (status != 0) throw new InvalidOperationException($"NTSTATUS 0x{status:X8}");
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }

        _logger.LogInformation("{Action} process {Pid}", suspend ? "Suspended" : "Resumed", pid);
    }
}
