using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
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
/// <item>SetPowerMode is open to any user too: it is the Windows power-mode slider, which every user may move in Windows'
/// own settings.</item>
/// <item>Anyone may save and delete process and Watchdog rules, as the unelevated UI must. The dispatcher then checks the
/// rule itself (<see cref="RuleAuthorization"/>: no system processes, Realtime, Watchdog Launch, or rules for programs whose
/// running instances the caller couldn't change or end themselves), since that needs the stored rule and the running processes. A rule may be changed or deleted
/// only by someone who could have created it. The legacy SetRule request stays administrator-only.</item>
/// <item>SetEfficiencyMode, SetIoPriority and SetMemoryPriority need the same right as SetPriority. Setting I/O priority
/// High (3) is administrator-only, like Realtime: Windows needs SeIncreaseBasePriorityPrivilege for it.</item>
/// <item>TerminateProcessTree needs the right to end the target; the dispatcher then ends only the descendants the
/// caller could end too, and never the System process or the engine.</item>
/// <item>Clearing the action log (<see cref="CommandTypes.ClearActionLog"/>) is administrator-only: it is the record of
/// what the engine did to every user's processes.</item>
/// <item>The system timer resolution is administrator-only to change (<see cref="CommandTypes.SetTimerResolution"/>, and
/// <see cref="AppSettings.SetTimerResolutionAtStartup"/> through SaveSettings or SetSetting): it is machine-wide, costs
/// every user battery life, and the GlobalTimerResolutionRequests switch it can set lives under HKLM. Reading it
/// (<see cref="CommandTypes.GetTimerResolution"/>) is open to anyone, as the rest of the System page is.</item>
/// <item>Unparking cores while ProBalance restrains (<see cref="AppSettings.ProBalanceDisableCoreParking"/>) is
/// administrator-only: it rewrites the active power plan's processor settings for everyone.</item>
/// <item>CPU tuning (<see cref="CommandTypes.SetCpuTuning"/>) is administrator-only too: it rewrites a power plan's core
/// parking and frequency settings, which are machine-wide (Windows itself needs elevation to write them), and shows
/// hidden settings in every user's Power Options. Reading them (<see cref="CommandTypes.GetCpuTuning"/>) is open to
/// anyone, as powercfg /query is.</item>
/// <item>Settings listed in <see cref="AdministratorOnlySettings"/> may be saved by a standard user only unchanged: a
/// SaveSettings or SetSetting that changes one of them is refused as a whole (<see cref="CheckSettingsChange"/>).</item>
/// <item>Config profiles: listing, saving, renaming, deleting and exporting them is open to any user (a profile changes
/// nothing until it is applied, and the rules and settings it holds are readable by anyone anyway). Switching to one and
/// importing a config are open here too, and <c>ConfigProfileService</c> then checks every rule it would add, change or
/// remove with <see cref="RuleAuthorization"/> and the settings with <see cref="CheckSettingsChange"/>, as the caller.</item>
/// <item>The log viewer's commands (<see cref="CommandTypes.GetLogEntries"/>, <see cref="CommandTypes.ExportLogs"/>) are open
/// to any user, but what they read depends on the caller (<see cref="MayReadEngineLog"/>). The action log stays readable by
/// everyone, as it already is through GetActivity. The engine's text log is administrators-only: it is free text that
/// records other users' process names and ids, the account behind every pipe request, file paths and exception
/// details. It isn't redacted for standard users instead, because a free-text line can't be cleaned reliably (a user
/// name or a path can turn up anywhere in a message or a stack trace). A standard user simply gets the action log alone,
/// and the reply says the engine log was left out.</item>
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
        CommandTypes.SetPowerMode,
        // Reads.
        CommandTypes.GetSnapshot,
        CommandTypes.GetProcessRules,
        CommandTypes.GetWatchdogRules,
        CommandTypes.GetSettings,
        CommandTypes.GetPower,
        CommandTypes.GetActivity,
        CommandTypes.GetCpuSets,
        CommandTypes.GetTimerResolution,
        CommandTypes.GetCpuTuning,
        // What they return is limited by MayReadEngineLog.
        CommandTypes.GetLogEntries,
        CommandTypes.ExportLogs,
        // Config profiles: bookkeeping and reading.
        CommandTypes.ListProfiles,
        CommandTypes.SaveProfile,
        CommandTypes.RenameProfile,
        CommandTypes.DeleteProfile,
        CommandTypes.ExportConfig,
    };

    /// <summary>Allowed here; <c>ConfigProfileService</c> checks every rule and setting they would apply, as the caller.</summary>
    internal static readonly IReadOnlySet<string> ConfigCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        CommandTypes.SwitchProfile,
        CommandTypes.ImportConfig,
    };

    /// <summary>Whether <paramref name="caller"/> may read the engine's text log (see the policy above). The action log is open to all.</summary>
    public static bool MayReadEngineLog(IIpcCaller caller) => caller.IsAdministrator;

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
        CommandTypes.SetTimerResolution,
        CommandTypes.SetCpuTuning,
    };

    /// <summary>
    /// <see cref="AppSettings"/> properties only administrators may change. The settings commands themselves stay open,
    /// so a standard user can save everything else; this is checked against the stored values.
    /// </summary>
    internal static readonly IReadOnlyList<string> AdministratorOnlySettings = new[]
    {
        nameof(AppSettings.SetTimerResolutionAtStartup),
        // Rewrites the active power plan's hidden processor settings, machine-wide (powercfg needs elevation for it too).
        nameof(AppSettings.ProBalanceDisableCoreParking),
    };

    /// <summary>
    /// Null when <paramref name="caller"/> may replace <paramref name="stored"/> with <paramref name="incoming"/>,
    /// otherwise the refusal naming the first administrator-only setting it would change.
    /// </summary>
    public static CommandResult? CheckSettingsChange(AppSettings stored, AppSettings incoming, IIpcCaller caller)
    {
        if (caller.IsAdministrator) return null;
        foreach (var name in AdministratorOnlySettings)
        {
            var property = typeof(AppSettings).GetProperty(name)!;
            if (!Equals(property.GetValue(stored), property.GetValue(incoming)))
                return Deny(caller, "change " + DescribeSetting(name));
        }
        return null;
    }

    private static string DescribeSetting(string setting) => setting switch
    {
        nameof(AppSettings.SetTimerResolutionAtStartup) => "the system timer resolution",
        nameof(AppSettings.ProBalanceDisableCoreParking) => "whether ProBalance unparks cores (it edits the power plan)",
        _ => System.Text.RegularExpressions.Regex.Replace(setting, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant(),
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
        if (UnrestrictedCommands.Contains(cmd.CommandType) || RuleCommands.Contains(cmd.CommandType) || ConfigCommands.Contains(cmd.CommandType)) return null;
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
