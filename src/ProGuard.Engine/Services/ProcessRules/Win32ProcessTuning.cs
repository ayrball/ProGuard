using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary>The real <see cref="IProcessTuningNative"/>: opens the process for each call and closes it again.</summary>
public sealed class Win32ProcessTuning : IProcessTuningNative
{
    internal const string UserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private const ProcessAccessFlags Query = ProcessAccessFlags.QueryInformation | ProcessAccessFlags.QueryLimitedInformation;
    private const ProcessAccessFlags Set = ProcessAccessFlags.SetInformation | ProcessAccessFlags.QueryLimitedInformation;

    // === I/O priority ===

    public bool TryGetIoPriority(int pid, out int priority)
    {
        priority = 0;
        var value = 0;
        var ok = TryWithProcess(pid, Query, h => ProcessTuningNative.NtQueryInformationProcess(h, Ntdll.ProcessIoPriority, ref value, sizeof(int), out _) == 0);
        priority = value;
        return ok;
    }

    public void SetIoPriority(int pid, int priority) =>
        WithProcess(pid, Set, h => CheckStatus(Ntdll.NtSetInformationProcess(h, Ntdll.ProcessIoPriority, ref priority, sizeof(int)), "NtSetInformationProcess(ProcessIoPriority)"));

    // === Memory priority ===

    public bool TryGetMemoryPriority(int pid, out int priority)
    {
        var info = new MEMORY_PRIORITY_INFORMATION();
        var ok = TryWithProcess(pid, Query, h => GetInformation(h, ProcessTuningNative.ProcessMemoryPriority, ref info));
        priority = (int)info.MemoryPriority;
        return ok;
    }

    public void SetMemoryPriority(int pid, int priority)
    {
        var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = (uint)priority };
        WithProcess(pid, Set, h => SetInformation(h, Kernel32.ProcessMemoryPriority, info, "SetProcessInformation(ProcessMemoryPriority)"));
    }

    // === Efficiency mode (EcoQoS) ===

    public bool TryGetEfficiencyMode(int pid, out bool controlled, out bool enabled)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_CURRENT_VERSION };
        var ok = TryWithProcess(pid, Query, h => GetInformation(h, ProcessTuningNative.ProcessPowerThrottling, ref state));
        const uint speed = PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
        controlled = (state.ControlMask & speed) != 0;
        enabled = controlled && (state.StateMask & speed) != 0;
        return ok;
    }

    public void SetEfficiencyMode(int pid, bool enabled)
    {
        const uint speed = PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = speed,
            StateMask = enabled ? speed : 0,
        };
        WithProcess(pid, Set, h => SetInformation(h, Kernel32.ProcessPowerThrottling, state, "SetProcessInformation(ProcessPowerThrottling)"));
    }

    /// <summary>Clears the EXECUTION_SPEED control bit, so Windows decides again (not part of <see cref="IProcessTuningNative"/>).</summary>
    public void ResetEfficiencyMode(int pid)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_CURRENT_VERSION };
        WithProcess(pid, Set, h => SetInformation(h, Kernel32.ProcessPowerThrottling, state, "SetProcessInformation(ProcessPowerThrottling)"));
    }

    // === Dynamic priority boost ===

    public bool TryGetPriorityBoostDisabled(int pid, out bool disabled)
    {
        var value = false;
        var ok = TryWithProcess(pid, ProcessAccessFlags.QueryLimitedInformation, h => ProcessTuningNative.GetProcessPriorityBoost(h, out value));
        disabled = value;
        return ok;
    }

    // Windows applies the process setting to the threads that already exist as well as to new ones.
    public void SetPriorityBoostDisabled(int pid, bool disabled) =>
        WithProcess(pid, Set, h => CheckWin32(ProcessTuningNative.SetProcessPriorityBoost(h, disabled), "SetProcessPriorityBoost"));

    // === CPU Sets ===

    public bool TryGetDefaultCpuSets(int pid, out IReadOnlyList<uint> ids)
    {
        IReadOnlyList<uint> result = Array.Empty<uint>();
        var ok = TryWithProcess(pid, ProcessAccessFlags.QueryLimitedInformation, h =>
        {
            if (ProcessTuningNative.GetProcessDefaultCpuSets(h, null, 0, out var count)) return true; // none assigned
            if (Marshal.GetLastWin32Error() != ProcessTuningNative.ERROR_INSUFFICIENT_BUFFER) return false;

            var buffer = new uint[count];
            if (!ProcessTuningNative.GetProcessDefaultCpuSets(h, buffer, count, out count)) return false;
            result = buffer.AsSpan(0, (int)count).ToArray();
            return true;
        });
        ids = result;
        return ok;
    }

    public void SetDefaultCpuSets(int pid, IReadOnlyList<uint> ids)
    {
        var array = ids.Count == 0 ? null : new List<uint>(ids).ToArray();
        WithProcess(pid, ProcessAccessFlags.SetLimitedInformation, h =>
            CheckWin32(ProcessTuningNative.SetProcessDefaultCpuSets(h, array, (uint)(array?.Length ?? 0)), "SetProcessDefaultCpuSets"));
    }

    public IReadOnlyList<CpuSetInfo> GetSystemCpuSets() => CpuSets.GetSystemCpuSets();

    // === GPU scheduling priority ===

    public bool TryGetGpuPriority(int pid, out int priority)
    {
        var value = 0;
        var ok = TryWithProcess(pid, Query, h => ProcessTuningNative.D3DKMTGetProcessSchedulingPriorityClass(h, out value) == 0);
        priority = value;
        return ok;
    }

    public void SetGpuPriority(int pid, int priority) =>
        WithProcess(pid, Set, h => CheckStatus(ProcessTuningNative.D3DKMTSetProcessSchedulingPriorityClass(h, priority), "D3DKMTSetProcessSchedulingPriorityClass"));

    // === Identity and GPU adapter preference ===

    public string? GetImagePath(int pid)
    {
        string? path = null;
        TryWithProcess(pid, ProcessAccessFlags.QueryLimitedInformation, h =>
        {
            var builder = new StringBuilder(1024);
            uint size = (uint)builder.Capacity;
            if (!Kernel32.QueryFullProcessImageName(h, 0, builder, ref size)) return false;
            path = builder.ToString();
            return true;
        });
        return path;
    }

    public string? GetProcessOwnerSid(int pid)
    {
        string? sid = null;
        TryWithProcess(pid, ProcessAccessFlags.QueryLimitedInformation, h =>
        {
            if (!ProcessTuningNative.OpenProcessToken(h, ProcessTuningNative.TOKEN_QUERY, out var token)) return false;
            sid = SidOfToken(token);
            return sid != null;
        });
        return sid;
    }

    public string? GetConsoleUserSid()
    {
        var session = Kernel32.WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return null;
        return ProcessTuningNative.WTSQueryUserToken(session, out var token) ? SidOfToken(token) : null;
    }

    public string? ReadUserGpuPreference(string sid, string exePath)
    {
        using var key = Registry.Users.OpenSubKey($@"{sid}\{UserGpuPreferencesKey}");
        return key?.GetValue(exePath) as string;
    }

    public void WriteUserGpuPreference(string sid, string exePath, string value)
    {
        // HKU only holds the hives of users with a running process or a loaded profile; creating the SID key
        // ourselves would write into a volatile key Windows never reads.
        using var hive = Registry.Users.OpenSubKey(sid) ?? throw new InvalidOperationException($"the registry hive of {sid} is not loaded");
        using var key = Registry.Users.CreateSubKey($@"{sid}\{UserGpuPreferencesKey}", writable: true);
        key.SetValue(exePath, value, RegistryValueKind.String);
    }

    // === Helpers ===

    private static string? SidOfToken(IntPtr token)
    {
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value;
        }
        catch { return null; }
        finally { Kernel32.CloseHandle(token); }
    }

    private static IntPtr Open(int pid, ProcessAccessFlags access)
    {
        var h = Kernel32.OpenProcess(access, false, pid);
        if (h != IntPtr.Zero) return h;
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"OpenProcess({pid}) failed: {new Win32Exception(error).Message}");
    }

    private static void WithProcess(int pid, ProcessAccessFlags access, Action<IntPtr> action)
    {
        var h = Open(pid, access);
        try { action(h); }
        finally { Kernel32.CloseHandle(h); }
    }

    private static bool TryWithProcess(int pid, ProcessAccessFlags access, Func<IntPtr, bool> read)
    {
        var h = Kernel32.OpenProcess(access, false, pid);
        if (h == IntPtr.Zero && (access & ProcessAccessFlags.QueryInformation) != 0)
            h = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return read(h); }
        catch { return false; }
        finally { Kernel32.CloseHandle(h); }
    }

    private static bool GetInformation<T>(IntPtr h, int infoClass, ref T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            if (!ProcessTuningNative.GetProcessInformation(h, infoClass, ptr, (uint)size)) return false;
            value = Marshal.PtrToStructure<T>(ptr);
            return true;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static void SetInformation<T>(IntPtr h, int infoClass, T value, string what) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            CheckWin32(Kernel32.SetProcessInformation(h, infoClass, ptr, (uint)size), what);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static void CheckWin32(bool ok, string what)
    {
        if (ok) return;
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{what} failed: {new Win32Exception(error).Message}");
    }

    private static void CheckStatus(int status, string what)
    {
        if (status == 0) return;
        // High I/O priority needs SeIncreaseBasePriorityPrivilege, which the engine only has when it runs elevated.
        if (status == StatusPrivilegeNotHeld) throw new Win32Exception(PrivilegeNotHeldMessage);
        throw new Win32Exception($"{what} failed with NTSTATUS 0x{status:X8}");
    }

    internal const int StatusPrivilegeNotHeld = unchecked((int)0xC0000061);

    internal const string PrivilegeNotHeldMessage =
        "Windows refused: this needs the engine to run as administrator (it lacks the \"increase scheduling priority\" right).";
}
