using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;
using ProGuard.Shared.Messages;

namespace ProGuard.Engine.IPC;

/// <summary>
/// Decides which requests a command-pipe client may make. Every interactive user can open the pipe, because the UI
/// runs unelevated. The engine runs as SYSTEM, so without this check any local user could use it to kill, freeze or
/// re-prioritise other users' processes and system processes.
/// <para>Policy:</para>
/// <list type="bullet">
/// <item>Administrators (an elevated token) and SYSTEM may send anything. The dispatcher's own guards (e.g. never
/// terminating the System process or the engine) still apply.</item>
/// <item>Anyone may read the Watchdog event log (GetLogs), whose notifications every desktop's UI shows. It holds
/// process names and ids, which any user can list anyway, and the rules' own notification text.</item>
/// <item>Anyone may Ping, GetStatus, read the process rules (GetRules), change the engine settings (SetSetting and the ProBalance, SmartTrim and
/// IdleSaver commands), toggle Performance Mode and trim all working sets. These are the machine-wide preferences the
/// UI shows on every desktop, as Process Lasso's GUI does. None of them reads or changes another user's data, and
/// trimming everything is what SmartTrim does anyway once it is enabled.</item>
/// <item>A process action is allowed only when the caller could do it themselves: their own token must be able to open
/// the target with the access that action needs. This rules out other users' processes, elevated processes and
/// protected processes.</item>
/// <item>Non-administrators may not set Realtime priority. Windows itself grants it only with
/// SeIncreaseBasePriorityPrivilege, which standard users lack, and a realtime process can starve the machine.</item>
/// <item>"Purge now" (<see cref="CommandTypes.PurgeMemory"/>) is administrator-only: purging the standby list and the
/// file cache throws away every user's cached data machine-wide, and Windows itself requires
/// SeProfileSingleProcessPrivilege / SeIncreaseQuotaPrivilege for it, which standard users lack.</item>
/// <item>The thin client's reads are open to any user: the snapshot (<see cref="CommandTypes.GetSnapshot"/>, where
/// <c>SnapshotBuilder</c> blanks the file path of every process the caller couldn't open themselves), the process and
/// Watchdog rules, the settings, the power state, the activity feed and the CPU Sets. They hold machine-wide
/// preferences and the process list any user can see anyway.</item>
/// <item>SaveSettings and SetPowerPlan are open to any user, like SetSetting and Performance Mode: they are the same
/// machine-wide preferences, and a power plan the user picks is what Windows would let them pick in Power Options.
/// A plan chosen this way wins over Performance Mode or IdleSaver (see <c>IPowerManager.SetActivePlan</c>).</item>
/// <item>Anyone may save and delete process and Watchdog rules, as the unelevated UI must. The dispatcher then checks the
/// rule itself (<see cref="RuleAuthorization"/>: no system processes, Realtime, Watchdog Launch, or ending processes the
/// caller couldn't end), since that needs the stored rule and the running processes. A rule may be changed or deleted
/// only by someone who could have created it. The legacy SetRule request stays administrator-only.</item>
/// <item>SetEfficiencyMode, SetIoPriority and SetMemoryPriority need the same right as SetPriority. Setting I/O priority
/// High (3) is administrator-only, like Realtime: Windows needs SeIncreaseBasePriorityPrivilege for it.</item>
/// <item>TerminateProcessTree needs the right to end the target; the dispatcher then ends only the descendants the
/// caller could end too, and never the System process or the engine.</item>
/// <item>Clearing the action log (<see cref="CommandTypes.ClearActionLog"/>) is administrator-only: it is the record of
/// what the engine did to every user's processes.</item>
/// <item>Anything else is refused. A new command stays administrator-only until it is added here.</item>
/// </list>
/// </summary>
public static class IpcAuthorization
{
    private static readonly HashSet<string> UnrestrictedCommands = new(StringComparer.Ordinal)
    {
        CommandTypes.EnableProBalance,
        CommandTypes.DisableProBalance,
        CommandTypes.SetSensitivity,
        CommandTypes.ConfigureIdleSaver,
        CommandTypes.ConfigureSmartTrim,
        CommandTypes.EnablePerformanceMode,
        CommandTypes.DisablePerformanceMode,
        CommandTypes.SaveSettings,
        CommandTypes.SetPowerPlan,
        // Reads.
        CommandTypes.GetSnapshot,
        CommandTypes.GetProcessRules,
        CommandTypes.GetWatchdogRules,
        CommandTypes.GetSettings,
        CommandTypes.GetPower,
        CommandTypes.GetActivity,
        CommandTypes.GetCpuSets,
    };

    /// <summary>Allowed here; <see cref="IpcDispatcher"/> checks the rule's contents with <see cref="RuleAuthorization"/>.</summary>
    internal static readonly IReadOnlySet<string> RuleCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        CommandTypes.SaveProcessRule,
        CommandTypes.DeleteProcessRule,
        CommandTypes.SaveWatchdogRule,
        CommandTypes.DeleteWatchdogRule,
    };

    /// <summary>The access each process command needs, i.e. what the caller would need to do it without ProGuard.</summary>
    internal static readonly IReadOnlyDictionary<string, ProcessAccessFlags> ProcessCommandAccess = new Dictionary<string, ProcessAccessFlags>(StringComparer.Ordinal)
    {
        [CommandTypes.SetPriority] = ProcessAccessFlags.SetInformation,
        [CommandTypes.SetAffinity] = ProcessAccessFlags.SetInformation,
        // CPU limits put the process in a job object: AssignProcessToJobObject needs both of these.
        [CommandTypes.SetCpuLimit] = ProcessAccessFlags.SetQuota | ProcessAccessFlags.Terminate,
        [CommandTypes.RemoveCpuLimit] = ProcessAccessFlags.SetQuota | ProcessAccessFlags.Terminate,
        [CommandTypes.TerminateProcess] = ProcessAccessFlags.Terminate,
        [CommandTypes.SetEfficiencyMode] = ProcessAccessFlags.SetInformation,
        [CommandTypes.SetIoPriority] = ProcessAccessFlags.SetInformation,
        [CommandTypes.SetMemoryPriority] = ProcessAccessFlags.SetInformation,
        // The target itself; the descendants are checked one by one when the tree is walked.
        [CommandTypes.TerminateProcessTree] = ProcessAccessFlags.Terminate,
        [CommandTypes.SuspendProcess] = ProcessAccessFlags.SuspendResume,
        [CommandTypes.ResumeProcess] = ProcessAccessFlags.SuspendResume,
        // EmptyWorkingSet.
        [CommandTypes.TrimWorkingSet] = ProcessAccessFlags.SetQuota | ProcessAccessFlags.QueryLimitedInformation,
    };

    /// <summary>Commands only administrators may send. Anything not listed elsewhere is refused anyway; these are named so that stays deliberate.</summary>
    internal static readonly IReadOnlySet<string> AdministratorOnlyCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        CommandTypes.PurgeMemory,
        CommandTypes.ClearActionLog,
    };

    /// <summary>Returns null when <paramref name="caller"/> may make <paramref name="request"/>, otherwise the error to reply with.</summary>
    public static CommandResult? Check(CommandMessage request, IIpcCaller caller)
    {
        if (caller.IsAdministrator) return null;

        switch (request.Type)
        {
            case MessageType.Ping:
            case MessageType.GetStatus:
            case MessageType.SetSetting:
            case MessageType.GetRules: // rule names and settings, no data about anyone's processes
            case MessageType.GetLogs:
                return null;
            case MessageType.Command:
                return CheckCommand(request, caller);
            default:
                return Deny(caller, $"send {request.Type} requests");
        }
    }

    private static CommandResult? CheckCommand(CommandMessage cmd, IIpcCaller caller)
    {
        if (AdministratorOnlyCommands.Contains(cmd.CommandType)) return Deny(caller, CommandTypes.Describe(cmd.CommandType));
        if (UnrestrictedCommands.Contains(cmd.CommandType) || RuleCommands.Contains(cmd.CommandType)) return null;
        if (cmd.CommandType == CommandTypes.TrimWorkingSet && cmd.TargetPid == null) return null;

        if (!ProcessCommandAccess.TryGetValue(cmd.CommandType, out var access))
            return Deny(caller, CommandTypes.Describe(cmd.CommandType));

        // A missing or invalid pid is the dispatcher's to report; there is nothing to act on.
        if (cmd.TargetPid is not int pid || pid <= 0) return null;

        if (cmd.CommandType == CommandTypes.SetPriority
            && Enum.TryParse<ProcessPriorityClass>(cmd.Argument, ignoreCase: true, out var priority)
            && priority == ProcessPriorityClass.RealTime)
            return Deny(caller, "set Realtime priority");

        if (cmd.CommandType == CommandTypes.SetIoPriority
            && int.TryParse(cmd.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var io)
            && io == WatchdogArguments.IoHigh)
            return Deny(caller, "set High I/O priority");

        return caller.CanOpenProcess(pid, access)
            ? null
            : Deny(caller, $"{CommandTypes.Describe(cmd.CommandType)} process {pid}: it belongs to another user, is elevated or protected, or is not running");
    }

    internal static CommandResult Deny(IIpcCaller caller, string what) =>
        CommandResult.Fail($"Access denied: {caller.Name} may not {what}. Run ProGuard as administrator to do this.");
}
