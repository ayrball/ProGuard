using System;
using System.Linq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;

namespace ProGuard.Engine.IPC;

/// <summary>
/// What a non-administrator may put in a process or Watchdog rule. The engine applies rules as SYSTEM to every user's
/// processes, so a standard user may only create rules whose effects they could cause themselves. A rule may be changed
/// or deleted only by someone who could have created it, so a standard user can't lift an administrator's rule either.
/// <list type="bullet">
/// <item>No rules for session and security processes or ProGuard itself (<see cref="CriticalProcesses"/>).</item>
/// <item>No Realtime priority, as a rule's priority, startup boost or Watchdog action
/// (see <see cref="IpcAuthorization"/>).</item>
/// <item>Rules that end processes (Disallow, an instance limit, Watchdog Terminate or Restart) only for a name whose
/// running instances the caller could end themselves. Instances started later by other users are not checked: the
/// rule names a program, and a standard user can already run that program and close it.</item>
/// <item>No launch-time priorities (<see cref="ProcessRule.ApplyPrioritiesAtCreation"/>): they are written to
/// Image File Execution Options under HKLM, a machine-wide setting only administrators may change, and apply to every
/// user's copy of the program from the moment it is created.</item>
/// <item>No Watchdog Launch actions: the program starts in the session of the process that triggered the rule, as that
/// session's user, so it would let one user run code as another.</item>
/// </list>
/// </summary>
internal static class RuleAuthorization
{
    /// <summary>Null when <paramref name="caller"/> may store <paramref name="rule"/>, otherwise what they may not do (for <see cref="IpcAuthorization.Deny"/>).</summary>
    public static string? CheckProcessRule(ProcessRule rule, IIpcCaller caller, IProcessMonitor monitor)
    {
        if (caller.IsAdministrator) return null;
        if (CriticalProcesses.IsCriticalName(rule.ProcessName)) return $"change rules for {rule.ProcessName}, a system process";
        if (IsRealtime(rule.PriorityClass) || IsRealtime(rule.StartupBoostPriorityClass)) return "set Realtime priority";
        if (rule.ApplyPrioritiesAtCreation) return "apply priorities at launch: they are written to the machine-wide Image File Execution Options";
        if (rule.DisallowExecution || rule.MaxInstances != null) return CheckCanEnd(rule.ProcessName, caller, monitor);
        return null;
    }

    /// <inheritdoc cref="CheckProcessRule"/>
    public static string? CheckWatchdogRule(WatchdogRule rule, IIpcCaller caller, IProcessMonitor monitor)
    {
        if (caller.IsAdministrator) return null;
        if (CriticalProcesses.IsCriticalName(rule.TargetProcessName)) return $"change rules for {rule.TargetProcessName}, a system process";
        switch (rule.ActionType)
        {
            case WatchdogAction.Launch:
                return "add, change or delete Watchdog Launch actions: the program would start as whichever user ran the triggering process";
            case WatchdogAction.SetPriority when WatchdogArguments.TryParsePriority(rule.ActionArgument, out var priority)
                                                  && priority == System.Diagnostics.ProcessPriorityClass.RealTime:
                return "set Realtime priority";
            case WatchdogAction.Terminate:
            case WatchdogAction.Restart:
                return CheckCanEnd(rule.TargetProcessName, caller, monitor);
            default:
                return null;
        }
    }

    private static bool IsRealtime(int? priorityClass) => priorityClass == (int)System.Diagnostics.ProcessPriorityClass.RealTime;

    private static string? CheckCanEnd(string processName, IIpcCaller caller, IProcessMonitor monitor)
    {
        var key = ProcessExtensions.NormalizeName(processName);
        foreach (var p in monitor.GetAllProcesses().Where(p => ProcessExtensions.NormalizeName(p.Name) == key))
        {
            if (!caller.CanOpenProcess(p.Pid, ProcessAccessFlags.Terminate))
                return $"add, change or delete a rule that ends {processName}: a running instance (pid {p.Pid}) belongs to another user, is elevated or protected";
        }
        return null;
    }
}
