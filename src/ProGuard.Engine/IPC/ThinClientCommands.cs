using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ActivityKind = ProGuard.Shared.Contracts.ActivityKind; // System.Diagnostics has one too

namespace ProGuard.Engine.IPC;

/// <summary>
/// The commands the thin client added: snapshot, rules, power, activity, CPU Sets and the extra process actions.
/// <see cref="IpcDispatcher"/> has already authorized the request by command and process; this class does the work and
/// the checks that need the data (a rule's contents, a process tree). Settings stay in the dispatcher, which owns the
/// settings lock.
/// </summary>
public sealed class ThinClientCommands
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<ThinClientCommands> _logger;
    private readonly SnapshotBuilder _snapshots;
    private readonly ProcessActionService _actions;
    private readonly RuleService _rules;
    private readonly ActivityFeed _feed;
    private readonly ActivityLog _manualLog;
    private readonly IRuleStore _ruleStore;
    private readonly IPowerManager _power;
    private readonly IProcessMonitor _monitor;
    private readonly IProcessTuningNative _native;
    private readonly CpuTuningService? _cpuTuning;

    public ThinClientCommands(
        ILogger<ThinClientCommands> logger,
        SnapshotBuilder snapshots,
        ProcessActionService actions,
        RuleService rules,
        ActivityFeed feed,
        ActivityLog manualLog,
        IRuleStore ruleStore,
        IPowerManager power,
        IProcessMonitor monitor,
        IProcessTuningNative native,
        CpuTuningService? cpuTuning = null)
    {
        _cpuTuning = cpuTuning;
        _logger = logger;
        _snapshots = snapshots;
        _actions = actions;
        _rules = rules;
        _feed = feed;
        _manualLog = manualLog;
        _ruleStore = ruleStore;
        _power = power;
        _monitor = monitor;
        _native = native;
    }

    /// <summary>Runs <paramref name="cmd"/> if it is one of the thin client's commands; false for any other.</summary>
    public bool TryExecute(CommandMessage cmd, IIpcCaller caller, out CommandResult result)
    {
        switch (cmd.CommandType)
        {
            case CommandTypes.GetSnapshot:
                result = CommandResult.Ok(JsonSerializer.Serialize(_snapshots.GetFor(caller)));
                return true;

            case CommandTypes.SetEfficiencyMode:
            {
                var pid = RequirePid(cmd);
                var enabled = ParseBool(cmd.Argument);
                _actions.SetEfficiencyMode(pid, enabled);
                Record(pid, enabled ? "Turned efficiency mode on." : "Turned efficiency mode off.");
                result = CommandResult.Ok();
                return true;
            }
            case CommandTypes.SetIoPriority:
            {
                var pid = RequirePid(cmd);
                var priority = ParseInt(cmd.Argument);
                _actions.SetIoPriority(pid, priority);
                Record(pid, $"Set I/O priority to {ProcessRuleOptions.Label(ProcessRuleOptions.IoPriorities, priority)}.");
                result = CommandResult.Ok();
                return true;
            }
            case CommandTypes.SetMemoryPriority:
            {
                var pid = RequirePid(cmd);
                var priority = ParseInt(cmd.Argument);
                _actions.SetMemoryPriority(pid, priority);
                Record(pid, $"Set memory priority to {ProcessRuleOptions.Label(ProcessRuleOptions.MemoryPriorities, priority)}.");
                result = CommandResult.Ok();
                return true;
            }
            case CommandTypes.TerminateProcessTree:
            {
                var pid = RequireForeignPid(cmd);
                var name = _monitor.GetProcess(pid)?.Name;
                var ended = _actions.TerminateTree(pid, caller);
                Record(pid, ended == 1 ? "Ended the process." : $"Ended the process and {ended - 1} of its descendants.", name);
                result = CommandResult.Ok(ended.ToString(CultureInfo.InvariantCulture));
                return true;
            }

            case CommandTypes.GetProcessRules:
                result = _rules.GetProcessRules();
                return true;
            case CommandTypes.SaveProcessRule:
                result = _rules.SaveProcessRule(ParseArgument<ProcessRuleDto>(cmd.Argument), caller);
                return true;
            case CommandTypes.DeleteProcessRule:
                result = _rules.DeleteProcessRule(ParseInt(cmd.Argument), caller);
                return true;

            case CommandTypes.GetWatchdogRules:
                result = _rules.GetWatchdogRules();
                return true;
            case CommandTypes.SaveWatchdogRule:
                result = _rules.SaveWatchdogRule(ParseArgument<WatchdogRuleDto>(cmd.Argument), caller);
                return true;
            case CommandTypes.DeleteWatchdogRule:
                result = _rules.DeleteWatchdogRule(ParseInt(cmd.Argument), caller);
                return true;

            case CommandTypes.GetPower:
                result = CommandResult.Ok(JsonSerializer.Serialize(GetPower()));
                return true;
            case CommandTypes.SetPowerPlan:
                result = SetPowerPlan(cmd.Argument);
                return true;
            case CommandTypes.SetPowerMode:
                result = SetPowerMode(cmd.Argument);
                return true;

            case CommandTypes.GetActivity:
            {
                var count = string.IsNullOrWhiteSpace(cmd.Argument) ? ActivityFeed.DefaultCount : ParseInt(cmd.Argument);
                if (count < 1) { result = CommandResult.Fail("The number of entries must be at least 1"); return true; }
                result = CommandResult.Ok(JsonSerializer.Serialize(_feed.GetRecent(count)));
                return true;
            }

            case CommandTypes.GetCpuSets:
                result = CommandResult.Ok(JsonSerializer.Serialize(_native.GetSystemCpuSets().Select(s => new CpuSetDto
                {
                    Id = (int)s.Id,
                    Group = s.Group,
                    LogicalProcessorIndex = s.LogicalProcessorIndex,
                    CoreIndex = s.CoreIndex,
                    NumaNodeIndex = s.NumaNodeIndex,
                    EfficiencyClass = s.EfficiencyClass,
                    IsParked = s.Parked,
                }).ToList()));
                return true;

            case CommandTypes.GetCpuTuning:
                result = GetCpuTuning(cmd.Argument);
                return true;
            case CommandTypes.SetCpuTuning:
                result = SetCpuTuning(ParseArgument<CpuTuningArgument>(cmd.Argument), caller);
                return true;

            case CommandTypes.ClearActionLog:
                _feed.Clear();
                _logger.LogInformation("Action log cleared by {Caller}", caller.Name);
                result = CommandResult.Ok();
                return true;

            default:
                result = CommandResult.Fail($"Unknown command '{cmd.CommandType}'");
                return false;
        }
    }

    /// <summary>Notes something a user did, for the activity feed.</summary>
    public void Record(int pid, string message, string? name = null)
    {
        name ??= _monitor.GetProcess(pid)?.Name ?? string.Empty;
        _manualLog.Add(ActivityKind.Manual, name, pid, message);
    }

    /// <summary>Notes a memory action in the feed, as a memory event rather than something the user did to a process.</summary>
    public void RecordMemory(int? pid, string message)
    {
        var name = pid is int p ? _monitor.GetProcess(p)?.Name ?? string.Empty : string.Empty;
        _manualLog.Add(ActivityKind.Memory, name, pid ?? 0, message);
    }

    // === Power ===

    private PowerStateDto GetPower()
    {
        var mode = _power.GetPowerMode();
        return new PowerStateDto
        {
            Plans = _power.GetAvailablePlans().Select(p => new PowerPlanDto { Id = p.Id, Name = p.Name }).ToList(),
            ActivePlanId = _power.GetActivePlan(),
            PerformanceMode = _power.IsPerformanceModeActive,
            PerformanceModeAutomatic = _power.IsPerformanceModeAutomatic,
            Owner = string.IsNullOrEmpty(_power.PlanOwnerName) ? "User" : _power.PlanOwnerName,
            PowerModeSupported = mode != null,
            PowerModeId = mode ?? Guid.Empty,
            PowerModes = PowerModes.Offered.Select(m => new PowerModeDto { Id = m.Id, Name = m.Name }).ToList(),
        };
    }

    /// <summary>
    /// Moves the Windows power-mode slider, as anyone may in Windows' own settings. Only the modes the slider offers are
    /// accepted. Wins over Performance Mode the way <see cref="SetPowerPlan"/> does.
    /// </summary>
    private CommandResult SetPowerMode(string? argument)
    {
        if (!Guid.TryParse(argument?.Trim(), out var id) || PowerModes.Offered.All(m => m.Id != id)) return CommandResult.Fail("Invalid power mode");
        if (_power.GetPowerMode() == null) return CommandResult.Fail("This version of Windows has no power mode setting.");
        return _power.SetPowerMode(id)
            ? CommandResult.Ok()
            : CommandResult.Fail($"Windows wouldn't switch the power mode to {PowerModes.Describe(id)}.");
    }

    /// <summary>
    /// The user's choice of plan: applied through the power manager's arbitration (<see cref="IPowerManager.SetActivePlan"/>),
    /// so it becomes the plan the machine returns to. If Performance Mode, IdleSaver or the load switcher holds the plan,
    /// the user's choice wins: it is applied now and the feature does not take it back or restore over it (Performance
    /// Mode stays on, but the snapshot then reports the plan's owner as "User").
    /// </summary>
    private CommandResult SetPowerPlan(string? argument)
    {
        if (!Guid.TryParse(argument?.Trim(), out var id) || id == Guid.Empty) return CommandResult.Fail("Invalid power plan GUID");
        var plan = _power.GetAvailablePlans().Where(p => p.Id == id).Select(p => p.Name).FirstOrDefault();
        if (plan == null) return CommandResult.Fail("That power plan isn't installed on this PC.");

        _power.SetActivePlan(id);
        if (_power.GetActivePlan() != id) return CommandResult.Fail($"Windows wouldn't switch to the {plan} power plan.");
        return CommandResult.Ok();
    }

    // === CPU tuning ===

    private CommandResult GetCpuTuning(string? argument)
    {
        if (_cpuTuning == null) return CommandResult.Fail("This engine doesn't manage CPU tuning");
        Guid? plan = null;
        if (!string.IsNullOrWhiteSpace(argument))
        {
            if (!Guid.TryParse(argument.Trim(), out var id)) return CommandResult.Fail("Invalid power plan GUID");
            plan = id;
        }
        return CommandResult.Ok(JsonSerializer.Serialize(_cpuTuning.Get(plan)));
    }

    /// <summary>Administrators only (<see cref="IpcAuthorization"/>): power plans are the whole machine's.</summary>
    private CommandResult SetCpuTuning(CpuTuningArgument argument, IIpcCaller caller)
    {
        if (_cpuTuning == null) return CommandResult.Fail("This engine doesn't manage CPU tuning");
        var change = _cpuTuning.Set(argument);
        Record(0, change.Summary, string.Empty);
        _logger.LogInformation("CPU tuning changed by {Caller}: {Summary}", caller.Name, change.Summary);
        var data = JsonSerializer.Serialize(change.Tuning);
        return change.Error == null ? CommandResult.Ok(data) : new CommandResult { Success = false, Error = change.Error, Data = data };
    }

    // === Arguments ===

    private static int RequirePid(CommandMessage cmd) =>
        cmd.TargetPid is int pid && pid > 0 ? pid : throw new ArgumentException($"{cmd.CommandType} needs a TargetPid");

    // Ending the System process or the engine itself would take the machine or ProGuard down.
    private static int RequireForeignPid(CommandMessage cmd)
    {
        var pid = RequirePid(cmd);
        if (pid == 4 || pid == Environment.ProcessId) throw new InvalidOperationException($"Refusing to {cmd.CommandType} process {pid}");
        return pid;
    }

    private static int ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new ArgumentException($"Expected an integer, got '{value}'");

    private static bool ParseBool(string? value) =>
        bool.TryParse(value?.Trim(), out var v) ? v : throw new ArgumentException($"Expected true or false, got '{value}'");

    private static T ParseArgument<T>(string? json) where T : class
    {
        try
        {
            return (string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Json))
                   ?? throw new ArgumentException($"Missing {typeof(T).Name} argument");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The {typeof(T).Name} argument is malformed: {ex.Message}");
        }
    }
}
