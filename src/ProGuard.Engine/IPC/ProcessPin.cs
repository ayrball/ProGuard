using System;
using ProGuard.Core.Native;

namespace ProGuard.Engine.IPC;

/// <summary>
/// An engine handle to a process, held while a request about it is checked and carried out. Windows never gives a
/// process id to a new process while any handle to the old one is open, so for as long as the pin is held the id names
/// the process that was checked: the client's rights are checked against it (impersonating the client) and the action
/// then reopens it by id as SYSTEM, and both reach the same process. Without it, the process could exit between the two
/// and a privileged process reuse its id, and the engine would act on that one with the client's permission.
/// </summary>
internal sealed class ProcessPin : IDisposable
{
    private IntPtr _handle;

    private ProcessPin(IntPtr handle, DateTime? startTimeUtc)
    {
        _handle = handle;
        StartTimeUtc = startTimeUtc;
    }

    /// <summary>When the pinned process started, if Windows says.</summary>
    public DateTime? StartTimeUtc { get; }

    /// <summary>Pins process <paramref name="pid"/>, or returns null when it isn't running (or can't be opened at all).</summary>
    public static ProcessPin? Open(int pid)
    {
        if (pid <= 0) return null;
        var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation | ProcessAccessFlags.Synchronize, false, pid);
        if (handle == IntPtr.Zero) return null;
        DateTime? started = null;
        if (Kernel32.GetProcessTimes(handle, out var created, out _, out _, out _))
            started = DateTime.FromFileTimeUtc((long)created.ToUInt64());
        return new ProcessPin(handle, started);
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero) Kernel32.CloseHandle(handle);
    }
}
