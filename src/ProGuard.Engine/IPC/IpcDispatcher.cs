using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.Engine.IPC;

/// <summary>
/// Turns one IPC request into calls on the engine services and builds the reply. Kept separate from
/// <see cref="PipeServer"/> so the routing can be tested without a named pipe.
/// </summary>
public class IpcDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<IpcDispatcher> _logger;
    private readonly IRuleStore _ruleStore;
    private readonly SettingsApplier _settingsApplier;
    private readonly IProBalanceEngine _proBalance;
    private readonly IPowerManager _powerManager;
    private readonly ISmartTrimService _smartTrim;
    private readonly ICpuLimiter _cpuLimiter;
    private readonly IProcessController _processController;
    private readonly IProcessMonitor _processMonitor;
    private readonly WatchdogEventLog? _watchdogEvents;
    private readonly IUserIdleSource? _userIdle;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    // Settings commands read-modify-write the stored settings; concurrent clients must not lose each other's changes.
    private readonly object _settingsSync = new();

    public IpcDispatcher(
        ILogger<IpcDispatcher> logger,
        IRuleStore ruleStore,
        SettingsApplier settingsApplier,
        IProBalanceEngine proBalance,
        IPowerManager powerManager,
        ISmartTrimService smartTrim,
        ICpuLimiter cpuLimiter,
        IProcessController processController,
        IProcessMonitor processMonitor,
        WatchdogEventLog? watchdogEvents = null,
        IUserIdleSource? userIdle = null)
    {
        _logger = logger;
        _ruleStore = ruleStore;
        _settingsApplier = settingsApplier;
        _proBalance = proBalance;
        _powerManager = powerManager;
        _smartTrim = smartTrim;
        _cpuLimiter = cpuLimiter;
        _processController = processController;
        _processMonitor = processMonitor;
        _watchdogEvents = watchdogEvents;
        _userIdle = userIdle;
    }

    /// <summary>Handles a request from code inside the engine, which needs no authorization.</summary>
    public string HandleLine(string line) => HandleLine(line, IpcCaller.InProcess);

    /// <summary>Handles one JSON request line from <paramref name="caller"/> and returns the JSON reply line. Never throws.</summary>
    public string HandleLine(string line, IIpcCaller caller)
    {
        IpcMessage response;
        try
        {
            // CommandMessage is a superset of IpcMessage, so every request deserializes into it.
            var request = JsonSerializer.Deserialize<CommandMessage>(line, JsonOptions);
            response = request == null ? Result(CommandResult.Fail("Empty request")) : Dispatch(request, caller);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed IPC request");
            response = Result(CommandResult.Fail("Malformed request: " + ex.Message));
        }

        return JsonSerializer.Serialize(response);
    }

    /// <summary>Dispatches a request from code inside the engine, which needs no authorization.</summary>
    public IpcMessage Dispatch(CommandMessage request) => Dispatch(request, IpcCaller.InProcess);

    public IpcMessage Dispatch(CommandMessage request, IIpcCaller caller)
    {
        try
        {
            var denied = IpcAuthorization.Check(request, caller);
            if (denied != null)
            {
                _logger.LogWarning("Refused IPC request {Type}/{Command} (pid {Pid}) from {Caller}", request.Type, request.CommandType, request.TargetPid, caller.Name);
                return Result(denied);
            }

            return request.Type switch
            {
                MessageType.Ping => new IpcMessage { Type = MessageType.Pong, Payload = "{}" },
                MessageType.GetStatus => new IpcMessage { Type = MessageType.Status, Payload = JsonSerializer.Serialize(BuildStatus()) },
                MessageType.SetSetting => Result(ReplaceSettings(request.Payload)),
                MessageType.Command => Result(ExecuteCommand(request, caller)),
                MessageType.GetRules => new IpcMessage { Type = MessageType.RuleList, Payload = JsonSerializer.Serialize(_ruleStore.GetProcessRules().Select(RuleDtoMapper.ToDto).ToList()) },
                MessageType.SetRule => Result(SaveRule(request.Payload)),
                MessageType.GetLogs => GetWatchdogEvents(request.Payload),
                _ => Result(CommandResult.Fail($"Unsupported message type {request.Type}"))
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IPC request {Type}/{Command} failed", request.Type, request.CommandType);
            return Result(CommandResult.Fail(ex.Message));
        }
    }

    private CommandResult ExecuteCommand(CommandMessage cmd, IIpcCaller caller)
    {
        _logger.LogInformation("IPC command {Command} (pid {Pid}, arg {Argument}) from {Caller}", cmd.CommandType, cmd.TargetPid, cmd.Argument, caller.Name);

        switch (cmd.CommandType)
        {
            case CommandTypes.SetPriority:
                _processController.SetPriority(RequirePid(cmd), ParsePriority(cmd.Argument));
                return CommandResult.Ok();

            case CommandTypes.SetAffinity:
                _processController.SetAffinity(RequirePid(cmd), ParseMask(cmd.Argument));
                return CommandResult.Ok();

            case CommandTypes.SetCpuLimit:
                var percent = ParseDouble(cmd.Argument);
                if (percent <= 0 || percent > 100) return CommandResult.Fail("CPU limit must be in (0, 100]");
                _cpuLimiter.AddLimit(RequirePid(cmd), percent);
                return CommandResult.Ok();

            case CommandTypes.RemoveCpuLimit:
                _cpuLimiter.RemoveLimit(RequirePid(cmd));
                return CommandResult.Ok();

            case CommandTypes.TerminateProcess:
                _processController.Terminate(RequireForeignPid(cmd));
                return CommandResult.Ok();

            case CommandTypes.SuspendProcess:
                _processController.Suspend(RequireForeignPid(cmd));
                return CommandResult.Ok();

            case CommandTypes.ResumeProcess:
                _processController.Resume(RequirePid(cmd));
                return CommandResult.Ok();

            case CommandTypes.TrimWorkingSet:
                if (cmd.TargetPid is int trimPid) _smartTrim.TrimProcess(trimPid);
                else _smartTrim.TrimMemory();
                return CommandResult.Ok();

            case CommandTypes.PurgeMemory:
                var report = _smartTrim.PurgeNow() ?? new MemoryPurgeReport();
                var data = JsonSerializer.Serialize(report);
                // Partial success (e.g. trimmed, but the standby purge lacked a privilege) is a success; the report says what failed.
                return report.Errors.Count > 0 && !report.AnyAction
                    ? new CommandResult { Success = false, Error = report.Summary(), Data = data }
                    : CommandResult.Ok(data);

            case CommandTypes.EnableProBalance:
                return UpdateSettings(s => s.ProBalanceEnabled = true);

            case CommandTypes.DisableProBalance:
                return UpdateSettings(s => s.ProBalanceEnabled = false);

            case CommandTypes.SetSensitivity:
                var level = ParseInt(cmd.Argument);
                if (level < 1 || level > 10) return CommandResult.Fail("Sensitivity must be between 1 and 10");
                return UpdateSettings(s => s.ProBalanceSensitivity = level);

            case CommandTypes.ConfigureIdleSaver:
                var idle = ParseArgument<IdleSaverArgument>(cmd.Argument);
                if (idle.PlanGuid != null && !Guid.TryParse(idle.PlanGuid, out _)) return CommandResult.Fail("Invalid power plan GUID");
                return UpdateSettings(s =>
                {
                    s.IdleSaverEnabled = idle.Enabled;
                    s.IdleTimeoutMinutes = Math.Max(1, idle.TimeoutMinutes);
                    s.IdlePowerPlanGuid = idle.PlanGuid;
                });

            case CommandTypes.ConfigureSmartTrim:
                var trim = ParseArgument<SmartTrimArgument>(cmd.Argument);
                return UpdateSettings(s =>
                {
                    s.SmartTrimEnabled = trim.Enabled;
                    s.SmartTrimIntervalSeconds = trim.IntervalSeconds;
                    s.SmartTrimMemoryThresholdMB = trim.ThresholdMb;
                });

            case CommandTypes.EnablePerformanceMode:
                _powerManager.EnablePerformanceMode(string.IsNullOrWhiteSpace(cmd.Argument) || ParseBool(cmd.Argument));
                return CommandResult.Ok();

            case CommandTypes.DisablePerformanceMode:
                _powerManager.EnablePerformanceMode(false);
                return CommandResult.Ok();

            case CommandTypes.SaveProcessRule:
                return SaveProcessRule(ParseArgument<ProcessRule>(cmd.Argument), caller);

            case CommandTypes.DeleteProcessRule:
                return DeleteProcessRule(ParseInt(cmd.Argument), caller);

            case CommandTypes.SaveWatchdogRule:
                return SaveWatchdogRule(ParseArgument<WatchdogRule>(cmd.Argument), caller);

            case CommandTypes.DeleteWatchdogRule:
                return DeleteWatchdogRule(ParseInt(cmd.Argument), caller);

            case CommandTypes.ClearActionLog:
                _ruleStore.ClearProBalanceActions();
                return CommandResult.Ok();

            default:
                return CommandResult.Fail($"Unknown command '{cmd.CommandType}'");
        }
    }

    /// <summary>Saves a changed copy of the stored settings, then applies it to the services.</summary>
    private CommandResult UpdateSettings(Action<AppSettings> change)
    {
        lock (_settingsSync)
        {
            var settings = _ruleStore.GetSettings().Clone();
            change(settings);
            _ruleStore.UpdateSettings(settings);
            _settingsApplier.Apply(settings);
        }
        return CommandResult.Ok();
    }

    /// <summary>
    /// Applies a JSON object of <see cref="AppSettings"/> properties: only the properties it names change, so two UI
    /// pages saving their own settings can't undo each other's changes. A whole serialized AppSettings works too.
    /// </summary>
    private CommandResult ReplaceSettings(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) throw new ArgumentException("Missing AppSettings argument");
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Settings must be a JSON object");

        var changes = new List<(PropertyInfo Property, object? Value)>();
        foreach (var element in document.RootElement.EnumerateObject())
        {
            var property = typeof(AppSettings).GetProperty(element.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanWrite || property.Name == nameof(AppSettings.Id)) continue; // the row keeps its key
            changes.Add((property, element.Value.Deserialize(property.PropertyType, JsonOptions)));
        }

        return UpdateSettings(s =>
        {
            foreach (var (property, value) in changes) property.SetValue(s, value);
        });
    }

    private CommandResult SaveProcessRule(ProcessRule rule, IIpcCaller caller)
    {
        rule.ProcessName = rule.ProcessName?.Trim() ?? "";
        if (rule.ProcessName.Length == 0) return CommandResult.Fail("A rule needs a process name");
        var invalid = ValidateRuleOptions(rule.IoPriority, rule.MemoryPriority, rule.GpuPriority, rule.GpuPreference, rule.CpuSetIds);
        if (invalid != null) return invalid;
        if (rule.PriorityClass is int pc && !Enum.IsDefined(typeof(ProcessPriorityClass), pc)) return CommandResult.Fail("Invalid priority class");
        if (rule.AffinityMask == 0) return CommandResult.Fail("Affinity mask must select at least one CPU");

        // The rule this replaces must be one the caller could have made, or they could lift an administrator's rule.
        var existing = (rule.Id != 0 ? _ruleStore.GetProcessRules().FirstOrDefault(r => r.Id == rule.Id) : null)
                       ?? _ruleStore.GetProcessRule(rule.ProcessName);
        var denied = Authorize(caller, RuleAuthorization.CheckProcessRule(rule, caller, _processMonitor))
                     ?? (existing == null ? null : Authorize(caller, RuleAuthorization.CheckProcessRule(existing, caller, _processMonitor)));
        if (denied != null) return denied;

        _ruleStore.SaveProcessRule(rule);
        _logger.LogInformation("Process rule {Name} ({Id}) saved by {Caller}", rule.ProcessName, rule.Id, caller.Name);
        return CommandResult.Ok(rule.Id.ToString(CultureInfo.InvariantCulture));
    }

    private CommandResult DeleteProcessRule(int id, IIpcCaller caller)
    {
        var existing = _ruleStore.GetProcessRules().FirstOrDefault(r => r.Id == id);
        if (existing == null) return CommandResult.Ok(); // already gone
        var denied = Authorize(caller, RuleAuthorization.CheckProcessRule(existing, caller, _processMonitor));
        if (denied != null) return denied;

        _ruleStore.DeleteProcessRule(id);
        _logger.LogInformation("Process rule {Name} ({Id}) deleted by {Caller}", existing.ProcessName, id, caller.Name);
        return CommandResult.Ok();
    }

    private CommandResult SaveWatchdogRule(WatchdogRule rule, IIpcCaller caller)
    {
        rule.TargetProcessName = rule.TargetProcessName?.Trim() ?? "";
        if (rule.TargetProcessName.Length == 0) return CommandResult.Fail("A rule needs a process name");
        if (!Enum.IsDefined(rule.ConditionType) || !Enum.IsDefined(rule.ActionType) || !Enum.IsDefined(rule.MemoryMetric))
            return CommandResult.Fail("Unknown Watchdog condition or action");

        WatchdogRule? existing = null;
        if (rule.Id != 0)
        {
            existing = _ruleStore.GetWatchdogRules().FirstOrDefault(r => r.Id == rule.Id);
            if (existing == null) return CommandResult.Fail($"Watchdog rule {rule.Id} no longer exists");
        }
        var denied = Authorize(caller, RuleAuthorization.CheckWatchdogRule(rule, caller, _processMonitor))
                     ?? (existing == null ? null : Authorize(caller, RuleAuthorization.CheckWatchdogRule(existing, caller, _processMonitor)));
        if (denied != null) return denied;

        if (existing == null) _ruleStore.AddWatchdogRule(rule);
        else _ruleStore.UpdateWatchdogRule(rule);
        _logger.LogInformation("Watchdog rule {Id} for {Name} saved by {Caller}", rule.Id, rule.TargetProcessName, caller.Name);
        return CommandResult.Ok(rule.Id.ToString(CultureInfo.InvariantCulture));
    }

    private CommandResult DeleteWatchdogRule(int id, IIpcCaller caller)
    {
        var existing = _ruleStore.GetWatchdogRules().FirstOrDefault(r => r.Id == id);
        if (existing == null) return CommandResult.Ok();
        var denied = Authorize(caller, RuleAuthorization.CheckWatchdogRule(existing, caller, _processMonitor));
        if (denied != null) return denied;

        _ruleStore.DeleteWatchdogRule(id);
        _logger.LogInformation("Watchdog rule {Id} for {Name} deleted by {Caller}", id, existing.TargetProcessName, caller.Name);
        return CommandResult.Ok();
    }

    private CommandResult? Authorize(IIpcCaller caller, string? refusal)
    {
        if (refusal == null) return null;
        _logger.LogWarning("Refused a rule change from {Caller}: may not {What}", caller.Name, refusal);
        return IpcAuthorization.Deny(caller, refusal);
    }

    private static CommandResult? ValidateRuleOptions(int? io, int? memory, int? gpu, int? gpuPreference, string? cpuSetIds)
    {
        if (io is int i && !ProcessRuleOptions.IsValid(ProcessRuleOptions.IoPriorities, i)) return CommandResult.Fail("Invalid I/O priority");
        if (memory is int m && !ProcessRuleOptions.IsValid(ProcessRuleOptions.MemoryPriorities, m, ProcessRuleOptions.MinMemoryPriority)) return CommandResult.Fail("Invalid memory priority");
        if (gpu is int g && !ProcessRuleOptions.IsValid(ProcessRuleOptions.GpuPriorities, g)) return CommandResult.Fail("Invalid GPU priority");
        if (gpuPreference is int p && !ProcessRuleOptions.IsValid(ProcessRuleOptions.GpuPreferences, p)) return CommandResult.Fail("Invalid GPU preference");
        if (cpuSetIds != null && !CpuSets.TryParseIds(cpuSetIds, out _)) return CommandResult.Fail("Invalid CPU Set IDs");
        return null;
    }

    /// <summary>
    /// Creates or updates the process rule named in a <see cref="RuleDto"/>. Only the fields the DTO carries change;
    /// the rest of an existing rule is kept. The engine applies it to processes as they start (or on the next
    /// re-apply check).
    /// </summary>
    private CommandResult SaveRule(string payload)
    {
        var dto = ParseArgument<RuleDto>(payload);
        if (string.IsNullOrWhiteSpace(dto.Name)) return CommandResult.Fail("A rule needs a process name");
        var invalid = ValidateRuleOptions(dto.IoPriority, dto.MemoryPriority, dto.GpuPriority, dto.GpuPreference, dto.CpuSetIds);
        if (invalid != null) return invalid;

        // Copy rather than mutate the cached rule: other threads are reading it.
        var rule = new ProcessRule();
        var existing = _ruleStore.GetProcessRule(dto.Name);
        if (existing != null)
        {
            foreach (var property in typeof(ProcessRule).GetProperties().Where(p => p.CanWrite))
                property.SetValue(rule, property.GetValue(existing));
        }
        RuleDtoMapper.CopyTo(dto, rule);
        _ruleStore.SaveProcessRule(rule);
        return CommandResult.Ok();
    }

    /// <summary>Watchdog and Keep Running events after the client's cursor; the UI shows the ones marked Notify.</summary>
    private IpcMessage GetWatchdogEvents(string payload)
    {
        var query = string.IsNullOrWhiteSpace(payload) ? new WatchdogEventQuery() : ParseArgument<WatchdogEventQuery>(payload);
        var list = _watchdogEvents?.GetSince(query.After) ?? new WatchdogEventList();
        return new IpcMessage { Type = MessageType.LogList, Payload = JsonSerializer.Serialize(list) };
    }

    private EngineStatus BuildStatus() => new()
    {
        IsRunning = true,
        ProBalanceEngaged = _proBalance.IsEngaged,
        ActiveInterventions = _proBalance.ActiveInterventions,
        CpuPercent = SystemInfo.GetOverallCpuPercent(),
        RamPercent = SystemInfo.GetMemoryLoadPercent(),
        Uptime = _uptime.Elapsed.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture),
        ActiveRuleCount = _ruleStore.GetProcessRules().Count(),
        ProcessCount = _processMonitor.GetAllProcesses().Count(),
        ForegroundPid = _processMonitor.GetForegroundPid(),
        UserIdleSeconds = _userIdle?.GetIdleTime()?.TotalSeconds,
        PerformanceModeActive = _powerManager.IsPerformanceModeActive,
    };

    internal static IpcMessage Result(CommandResult result) =>
        new() { Type = MessageType.CommandResult, Payload = JsonSerializer.Serialize(result) };

    private static int RequirePid(CommandMessage cmd) =>
        cmd.TargetPid is int pid && pid > 0 ? pid : throw new ArgumentException($"{cmd.CommandType} needs a TargetPid");

    // Killing or freezing the System process or the engine itself would take the machine or ProGuard down.
    private static int RequireForeignPid(CommandMessage cmd)
    {
        var pid = RequirePid(cmd);
        if (pid == 4 || pid == Environment.ProcessId) throw new InvalidOperationException($"Refusing to {cmd.CommandType} process {pid}");
        return pid;
    }

    private static ProcessPriorityClass ParsePriority(string? value)
    {
        if (Enum.TryParse<ProcessPriorityClass>(value, ignoreCase: true, out var priority) && Enum.IsDefined(priority))
            return priority;
        throw new ArgumentException($"Unknown priority '{value}'");
    }

    private static long ParseMask(string? value)
    {
        var text = value?.Trim() ?? "";
        var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask)
            : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask);
        return parsed ? mask : throw new ArgumentException($"Invalid affinity mask '{value}'");
    }

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new ArgumentException($"Expected an integer, got '{value}'");

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new ArgumentException($"Expected a number, got '{value}'");

    private static bool ParseBool(string? value) =>
        bool.TryParse(value, out var v) ? v : throw new ArgumentException($"Expected true or false, got '{value}'");

    private static T ParseArgument<T>(string? json) where T : class =>
        (string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, JsonOptions))
        ?? throw new ArgumentException($"Missing {typeof(T).Name} argument");
}
