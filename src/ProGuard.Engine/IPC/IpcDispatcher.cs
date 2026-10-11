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
        IProcessMonitor processMonitor)
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
    }

    /// <summary>Handles one JSON request line and returns the JSON reply line. Never throws.</summary>
    public string HandleLine(string line)
    {
        IpcMessage response;
        try
        {
            // CommandMessage is a superset of IpcMessage, so every request deserializes into it.
            var request = JsonSerializer.Deserialize<CommandMessage>(line, JsonOptions);
            response = request == null ? Result(CommandResult.Fail("Empty request")) : Dispatch(request);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed IPC request");
            response = Result(CommandResult.Fail("Malformed request: " + ex.Message));
        }

        return JsonSerializer.Serialize(response);
    }

    public IpcMessage Dispatch(CommandMessage request)
    {
        try
        {
            return request.Type switch
            {
                MessageType.Ping => new IpcMessage { Type = MessageType.Pong, Payload = "{}" },
                MessageType.GetStatus => new IpcMessage { Type = MessageType.Status, Payload = JsonSerializer.Serialize(BuildStatus()) },
                MessageType.SetSetting => Result(ReplaceSettings(request.Payload)),
                MessageType.Command => Result(ExecuteCommand(request)),
                _ => Result(CommandResult.Fail($"Unsupported message type {request.Type}"))
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IPC request {Type}/{Command} failed", request.Type, request.CommandType);
            return Result(CommandResult.Fail(ex.Message));
        }
    }

    private CommandResult ExecuteCommand(CommandMessage cmd)
    {
        _logger.LogInformation("IPC command {Command} (pid {Pid}, arg {Argument})", cmd.CommandType, cmd.TargetPid, cmd.Argument);

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

    private EngineStatus BuildStatus() => new()
    {
        IsRunning = true,
        ProBalanceEngaged = _proBalance.IsEngaged,
        ActiveInterventions = _proBalance.ActiveInterventions,
        CpuPercent = SystemInfo.GetOverallCpuPercent(),
        RamPercent = SystemInfo.GetMemoryLoadPercent(),
        Uptime = _uptime.Elapsed.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture),
        ActiveRuleCount = _ruleStore.GetProcessRules().Count(),
        ProcessCount = _processMonitor.GetAllProcesses().Count()
    };

    private static IpcMessage Result(CommandResult result) =>
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
