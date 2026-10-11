using System.Collections.Generic;
using ProGuard.Core.Helpers;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary>
/// The Windows calls behind the persistent per-process defaults, by pid, so the appliers can be tested without
/// touching real processes. Setters throw (usually <see cref="System.ComponentModel.Win32Exception"/>) on failure;
/// getters return false when the value can't be read.
/// </summary>
public interface IProcessTuningNative
{
    /// <summary>IO_PRIORITY_HINT via NtQueryInformationProcess / NtSetInformationProcess(ProcessIoPriority = 33).</summary>
    bool TryGetIoPriority(int pid, out int priority);
    void SetIoPriority(int pid, int priority);

    /// <summary>MEMORY_PRIORITY_INFORMATION via Get/SetProcessInformation(ProcessMemoryPriority).</summary>
    bool TryGetMemoryPriority(int pid, out int priority);
    void SetMemoryPriority(int pid, int priority);

    /// <summary>
    /// ProcessPowerThrottling EXECUTION_SPEED. Reads false for both "explicitly off" and "left to Windows", so
    /// <paramref name="controlled"/> says whether the process sets it at all.
    /// </summary>
    bool TryGetEfficiencyMode(int pid, out bool controlled, out bool enabled);
    /// <summary>Always sets the EXECUTION_SPEED control bit, so false explicitly turns EcoQoS off rather than resetting it.</summary>
    void SetEfficiencyMode(int pid, bool enabled);

    bool TryGetPriorityBoostDisabled(int pid, out bool disabled);
    void SetPriorityBoostDisabled(int pid, bool disabled);

    bool TryGetDefaultCpuSets(int pid, out IReadOnlyList<uint> ids);
    /// <summary>An empty list clears the process's default CPU Sets.</summary>
    void SetDefaultCpuSets(int pid, IReadOnlyList<uint> ids);
    IReadOnlyList<CpuSetInfo> GetSystemCpuSets();

    /// <summary>D3DKMT_SCHEDULINGPRIORITYCLASS via D3DKMTGet/SetProcessSchedulingPriorityClass.</summary>
    bool TryGetGpuPriority(int pid, out int priority);
    void SetGpuPriority(int pid, int priority);

    /// <summary>Full Win32 path of the process image, or null.</summary>
    string? GetImagePath(int pid);
    /// <summary>SID (S-1-5-...) of the user the process runs as, or null if its token can't be read.</summary>
    string? GetProcessOwnerSid(int pid);
    /// <summary>SID of the user logged on to the physical console, or null if nobody is.</summary>
    string? GetConsoleUserSid();

    /// <summary>Raw <c>HKU\&lt;sid&gt;\Software\Microsoft\DirectX\UserGpuPreferences</c> value for <paramref name="exePath"/>, or null if absent.</summary>
    string? ReadUserGpuPreference(string sid, string exePath);
    /// <summary>Throws if the user's registry hive isn't loaded.</summary>
    void WriteUserGpuPreference(string sid, string exePath, string value);
}
