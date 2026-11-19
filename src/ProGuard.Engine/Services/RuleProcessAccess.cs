using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;

namespace ProGuard.Engine.Services;

/// <summary>
/// The live-process reads and writes the per-rule features (instance limits, Instance Spread, startup boost)
/// need. Kept behind an interface so their rules can be tested against fake processes.
/// Every method returns null/false instead of throwing when the process is gone or access is denied.
/// </summary>
public interface IRuleProcessAccess
{
    /// <summary>Terminal Services session; 0 is the services session.</summary>
    int? GetSessionId(int pid);

    DateTime? GetStartTimeUtc(int pid);

    /// <summary>
    /// True if ending the process would bug-check Windows (<c>IsProcessCritical</c>), and also when the process
    /// can't be queried, so callers never terminate something they know nothing about.
    /// </summary>
    bool IsCritical(int pid);

    bool Terminate(int pid);

    ProcessPriorityClass? GetPriorityClass(int pid);
    bool SetPriorityClass(int pid, ProcessPriorityClass priorityClass);

    long? GetAffinityMask(int pid);
    bool SetAffinityMask(int pid, long mask);
}

public sealed class Win32RuleProcessAccess : IRuleProcessAccess
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    public int? GetSessionId(int pid) => With(pid, p => (int?)p.SessionId);

    public DateTime? GetStartTimeUtc(int pid) => With(pid, p => (DateTime?)p.StartTime.ToUniversalTime());

    public bool IsCritical(int pid)
    {
        if (pid <= 4) return true; // Idle and System

        var handle = Kernel32.OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return true;
        try
        {
            return !IsProcessCritical(handle, out var critical) || critical;
        }
        finally { Kernel32.CloseHandle(handle); }
    }

    public bool Terminate(int pid) => With(pid, p => { p.Kill(); return true; });

    public ProcessPriorityClass? GetPriorityClass(int pid) => With(pid, p => (ProcessPriorityClass?)p.PriorityClass);

    public bool SetPriorityClass(int pid, ProcessPriorityClass priorityClass) =>
        With(pid, p => { p.PriorityClass = priorityClass; return true; });

    public long? GetAffinityMask(int pid) => With(pid, p => (long?)p.ProcessorAffinity.ToInt64());

    // Process.ProcessorAffinity is SetProcessAffinityMask.
    public bool SetAffinityMask(int pid, long mask) =>
        mask != 0 && With(pid, p => { p.ProcessorAffinity = new IntPtr(mask); return true; });

    private static T? With<T>(int pid, Func<Process, T> action)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return action(p);
        }
        catch { return default; }
    }
}

/// <summary>
/// Programs ProGuard treats specially by name. Two tiers:
/// <list type="bullet">
/// <item><see cref="IsCriticalName"/>: processes the per-rule features never end, limit, spread or boost, whatever a
/// rule says and whoever wrote it, and that ending a process tree skips. Session, logon and security processes whose
/// loss logs the user off, breaks UAC or sign-in, or takes services down with them.</item>
/// <item><see cref="IsAdministratorOnlyRuleTarget"/>: those, plus the shell, the service and installer hosts, consoles
/// and script hosts, and the shell's helper processes. Standard users may not write rules for them at all
/// (<c>RuleAuthorization</c>); administrators may, and their rules act as before. These are left out of the first tier
/// on purpose: an administrator may legitimately limit RuntimeBroker or end a tree that contains cmd and conhost, and
/// such a process is often elevated or another user's, which is what a standard user must not reach.</item>
/// </list>
/// </summary>
public static class CriticalProcesses
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "system", "idle", "registry", "memory compression", "secure system", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "lsaiso", "dwm", "fontdrvhost", "proguard.engine", "proguard.ui", "proguard.sessionagent",
        // Sign-in, UAC and the session shell host: ending them logs the user off or breaks elevation prompts.
        "consent", "logonui", "userinit", "sihost",
        // Hosts many services at once: ending one by name takes unrelated services down.
        "svchost",
        // Windows Security's service.
        "securityhealthservice",
    };

    private static readonly HashSet<string> AdministratorOnlyNames = new(StringComparer.Ordinal)
    {
        // The shell and its helpers.
        "explorer", "startmenuexperiencehost", "shellexperiencehost", "searchhost", "runtimebroker", "taskhostw", "ctfmon",
        // Service, installer and management hosts, which run as SYSTEM or elevated.
        "spoolsv", "msiexec", "mmc",
        // Consoles and script hosts, often running elevated for the same user.
        "cmd", "conhost", "powershell", "pwsh",
    };

    /// <summary>True for session/security processes and ProGuard itself, by name.</summary>
    public static bool IsCriticalName(string? processName) => Names.Contains(ProcessExtensions.NormalizeName(processName));

    /// <summary>True for the programs only administrators may write rules for: <see cref="IsCriticalName"/> and the shell, host and console processes above.</summary>
    public static bool IsAdministratorOnlyRuleTarget(string? processName)
    {
        var name = ProcessExtensions.NormalizeName(processName);
        return Names.Contains(name) || AdministratorOnlyNames.Contains(name);
    }
}
