using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
    private readonly ThinClientCommands? _thinClient;
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
        ThinClientCommands? thinClient = null,
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
        _thinClient = thinClient;
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
                var priorityPid = RequirePid(cmd);
                var priority = ParsePriority(cmd.Argument);
                _processController.SetPriority(priorityPid, priority);
                _thinClient?.Record(priorityPid, $"Set priority to {priority}.");
                return CommandResult.Ok();

            case CommandTypes.SetAffinity:
                var affinityPid = RequirePid(cmd);
                var mask = ParseMask(cmd.Argument);
                _processController.SetAffinity(affinityPid, mask);
                _thinClient?.Record(affinityPid, $"Set affinity to 0x{mask:X}.");
                return CommandResult.Ok();

            case CommandTypes.SetCpuLimit:
                var percent = ParseDouble(cmd.Argument);
                if (percent <= 0 || percent > 100) return CommandResult.Fail("CPU limit must be in (0, 100]");
                var limitPid = RequirePid(cmd);
                _cpuLimiter.AddLimit(limitPid, percent);
                _thinClient?.Record(limitPid, $"Capped CPU use at {percent:0.#}%.");
                return CommandResult.Ok();

            case CommandTypes.RemoveCpuLimit:
                var unlimitPid = RequirePid(cmd);
                _cpuLimiter.RemoveLimit(unlimitPid);
                _thinClient?.Record(unlimitPid, "Removed the CPU cap.");
                return CommandResult.Ok();

            case CommandTypes.TerminateProcess:
                var terminatePid = RequireForeignPid(cmd);
                var terminateName = _processMonitor.GetProcess(terminatePid)?.Name; // gone once it has ended
                _processController.Terminate(terminatePid);
                _thinClient?.Record(terminatePid, "Ended the process.", terminateName);
                return CommandResult.Ok();

            case CommandTypes.SuspendProcess:
                var suspendPid = RequireForeignPid(cmd);
                _processController.Suspend(suspendPid);
                _thinClient?.Record(suspendPid, "Suspended the process.");
                return CommandResult.Ok();

            case CommandTypes.ResumeProcess:
                var resumePid = RequirePid(cmd);
                _processController.Resume(resumePid);
                _thinClient?.Record(resumePid, "Resumed the process.");
                return CommandResult.Ok();

            case CommandTypes.TrimWorkingSet:
                return Trim(cmd);

            case CommandTypes.PurgeMemory:
                var report = _smartTrim.PurgeNow() ?? new MemoryPurgeReport();
                var data = JsonSerializer.Serialize(ToDto(report));
                _thinClient?.RecordMemory(null, report.Summary() + ".");
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

            case CommandTypes.GetSettings:
                return CommandResult.Ok(JsonSerializer.Serialize(DtoMapper.Map<AppSettings, EngineSettingsDto>(_ruleStore.GetSettings())));

            case CommandTypes.SaveSettings:
                return SaveSettings(ParseArgument<EngineSettingsDto>(cmd.Argument));

            default:
                if (_thinClient != null && _thinClient.TryExecute(cmd, caller, out var thinResult)) return thinResult;
                return CommandResult.Fail($"Unknown command '{cmd.CommandType}'");
        }
    }

    /// <summary>
    /// Trims one process or every eligible one. Data = bytes reclaimed. A single process Windows wouldn't let go of is a
    /// failure with its reason; "trim all" succeeds with whatever it managed.
    /// </summary>
    private CommandResult Trim(CommandMessage cmd)
    {
        var single = cmd.TargetPid is int;
        var report = (cmd.TargetPid is int pid ? _smartTrim.TrimProcess(pid) : _smartTrim.TrimMemory()) ?? new MemoryPurgeReport();
        if (single && report.ProcessesTrimmed == 0 && report.Errors.Count > 0)
            return CommandResult.Fail($"Could not trim process {cmd.TargetPid}: {report.Errors[0]}");

        _thinClient?.RecordMemory(cmd.TargetPid, single
            ? $"Trimmed the working set ({MemoryPurgeReport.FormatMb(report.WorkingSetBytesReclaimed)} reclaimed)."
            : report.Summary() + ".");
        return CommandResult.Ok(report.WorkingSetBytesReclaimed.ToString(CultureInfo.InvariantCulture));
    }

    private static MemoryPurgeResultDto ToDto(MemoryPurgeReport report)
    {
        var dto = DtoMapper.Map<MemoryPurgeReport, MemoryPurgeResultDto>(report);
        dto.Errors = report.Errors.ToList();
        dto.Summary = report.Summary();
        return dto;
    }

    /// <summary>
    /// Stores the settings the client sent over the stored ones (keeping the stored row's Id) and applies them. The DTO
    /// carries every setting, so it replaces them all; clients send back a copy of what GetSettings gave them.
    /// </summary>
    private CommandResult SaveSettings(EngineSettingsDto dto)
    {
        if (ValidateSettings(dto) is { } invalid) return CommandResult.Fail(invalid);

        AppSettings stored;
        lock (_settingsSync)
        {
            var settings = _ruleStore.GetSettings().Clone();
            DtoMapper.CopyTo(dto, settings); // never touches Id: the DTO has none
            _ruleStore.UpdateSettings(settings);
            _settingsApplier.Apply(settings);
            stored = settings;
        }
        _thinClient?.Record(0, "Saved the settings.", string.Empty);
        return CommandResult.Ok(JsonSerializer.Serialize(DtoMapper.Map<AppSettings, EngineSettingsDto>(stored)));
    }

    private static string? ValidateSettings(EngineSettingsDto d)
    {
        if (d.ProBalanceSensitivity < 1 || d.ProBalanceSensitivity > 10) return "Sensitivity must be between 1 and 10";
        if (!Percent(d.ProBalanceCpuThreshold) || !Percent(d.ProBalanceRecoveryThreshold)) return "ProBalance thresholds must be between 0% and 100%";
        if (d.ProBalanceDisableIfSysIdleMs < 0) return "The ProBalance idle time can't be negative";
        if (d.PollingIntervalMs < (int)ProcessMonitor.MinPollingInterval.TotalMilliseconds || d.PollingIntervalMs > (int)ProcessMonitor.MaxPollingInterval.TotalMilliseconds)
            return "The refresh interval must be between 0.25 and 60 seconds";
        if (d.SmartTrimIntervalSeconds < 1 || d.SmartTrimMemoryThresholdMB < 0 || d.StandbyPurgeFreeRamThresholdMB < 0) return "Invalid SmartTrim setting";
        if (d.SmartTrimMinimumRamLoadPercent < 0 || d.SmartTrimMinimumRamLoadPercent > 100) return "The SmartTrim memory load must be between 0% and 100%";
        if (d.IdleTimeoutMinutes < 1) return "The idle timeout must be at least 1 minute";
        if (!Percent(d.LoadSwitcherLowThreshold) || !Percent(d.LoadSwitcherHighThreshold)) return "Load switcher thresholds must be between 0% and 100%";
        if (d.LoadSwitcherAveragingWindowSamples < 1 || d.LoadSwitcherMinimumTimeInPlanMs < 0) return "Invalid load switcher setting";
        if (string.IsNullOrWhiteSpace(d.Theme)) return "The theme can't be empty";
        foreach (var guid in new[] { d.IdlePowerPlanGuid, d.PerformanceModePlanGuid, d.LoadSwitcherLowPlanGuid, d.LoadSwitcherHighPlanGuid, d.StartWithPowerPlanGuid })
            if (!string.IsNullOrEmpty(guid) && !Guid.TryParse(guid, out _)) return "Invalid power plan GUID";
        return null;
    }

    private static bool Percent(double value) => value >= 0 && value <= 100;

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

    private CommandResult ReplaceSettings(string payload)
    {
        var incoming = ParseArgument<AppSettings>(payload);
        return UpdateSettings(s =>
        {
            var id = s.Id;
            foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.CanWrite))
                property.SetValue(s, property.GetValue(incoming));
            s.Id = id; // the stored row keeps its key whatever the client sent
        });
    }

    /// <summary>
    /// Creates or updates the process rule named in a <see cref="ProcessRuleDto"/>. The DTO carries every field, so
    /// it replaces the stored rule (keeping its Id). The engine applies it to processes as they start (or on the next
    /// re-apply check).
    /// </summary>
    private CommandResult SaveRule(string payload)
    {
        var dto = ParseArgument<ProcessRuleDto>(payload);
        if (string.IsNullOrWhiteSpace(dto.ProcessName)) return CommandResult.Fail("A rule needs a process name");
        if (ProcessExtensions.ProgramNameProblem(dto.ProcessName) is { } badName) return CommandResult.Fail(badName);
        if (dto.IoPriority is int io && !ProcessRuleOptions.IsValid(ProcessRuleOptions.IoPriorities, io)) return CommandResult.Fail("Invalid I/O priority");
        if (dto.MemoryPriority is int mem && !ProcessRuleOptions.IsValid(ProcessRuleOptions.MemoryPriorities, mem, ProcessRuleOptions.MinMemoryPriority)) return CommandResult.Fail("Invalid memory priority");
        if (dto.GpuPriority is int gpu && !ProcessRuleOptions.IsValid(ProcessRuleOptions.GpuPriorities, gpu)) return CommandResult.Fail("Invalid GPU priority");
        if (dto.GpuPreference is int pref && !ProcessRuleOptions.IsValid(ProcessRuleOptions.GpuPreferences, pref)) return CommandResult.Fail("Invalid GPU preference");
        if (dto.CpuSetIds != null && !CpuSets.TryParseIds(dto.CpuSetIds, out _)) return CommandResult.Fail("Invalid CPU Set IDs");

        // Copy rather than mutate the cached rule: other threads are reading it.
        var rule = new ProcessRule();
        var existing = _ruleStore.GetProcessRule(dto.ProcessName);
        if (existing != null)
        {
            foreach (var property in typeof(ProcessRule).GetProperties().Where(p => p.CanWrite))
                property.SetValue(rule, property.GetValue(existing));
        }
        RuleDtoMapper.CopyTo(dto, rule);
        if (existing != null) rule.Id = existing.Id; // the stored row keeps its key whatever the client sent
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
