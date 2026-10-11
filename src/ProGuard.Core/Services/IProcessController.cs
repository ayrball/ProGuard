using System.Diagnostics;

namespace ProGuard.Core.Services;

/// <summary>
/// One-off actions on a single process, as requested by the UI over IPC. Every method throws if the process
/// is gone or the action is denied, so the caller can report the failure.
/// </summary>
public interface IProcessController
{
    void SetPriority(int pid, ProcessPriorityClass priority);
    void SetAffinity(int pid, long affinityMask);
    void Terminate(int pid);
    void Suspend(int pid);
    void Resume(int pid);
}
