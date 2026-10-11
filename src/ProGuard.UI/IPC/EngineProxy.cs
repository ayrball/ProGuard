using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProGuard.Core.Models;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.UI.IPC;

/// <summary>
/// Typed calls to the engine service. Command methods return the engine's <see cref="CommandResult"/>, or null
/// when the engine isn't running or didn't answer, so callers can fall back to doing the work themselves.
/// </summary>
/// <remarks>
/// Settings the UI saves to the shared database already reach the engine through its settings reload, so the
/// settings commands here are for callers that want the change applied immediately and confirmed.
/// </remarks>
public class EngineProxy : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly PipeClient _pipeClient;

    public event EventHandler<bool>? ConnectionStatusChanged;

    public EngineProxy() : this(new PipeClient()) { }

    public EngineProxy(PipeClient pipeClient)
    {
        _pipeClient = pipeClient;
        _pipeClient.ConnectionChanged += (_, connected) => ConnectionStatusChanged?.Invoke(this, connected);
    }

    public bool IsConnected => _pipeClient.IsConnected;

    public Task<bool> InitializeAsync(CancellationToken cancellationToken = default) => _pipeClient.ConnectAsync(cancellationToken);

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _pipeClient.SendAsync(new IpcMessage { Type = MessageType.Ping }, cancellationToken).ConfigureAwait(false);
        return reply?.Type == MessageType.Pong;
    }

    /// <summary>The engine's current status, or null when it can't be reached.</summary>
    public async Task<EngineStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _pipeClient.SendAsync(new IpcMessage { Type = MessageType.GetStatus }, cancellationToken).ConfigureAwait(false);
        return reply?.Type == MessageType.Status ? Deserialize<EngineStatus>(reply.Payload) : null;
    }

    /// <summary>
    /// Watchdog and Keep Running events newer than <paramref name="after"/> (see <see cref="WatchdogEventQuery"/>), or
    /// null when the engine can't be reached.
    /// </summary>
    public async Task<WatchdogEventList?> GetWatchdogEventsAsync(long? after, CancellationToken cancellationToken = default)
    {
        var request = new IpcMessage { Type = MessageType.GetLogs, Payload = JsonSerializer.Serialize(new WatchdogEventQuery { After = after }) };
        var reply = await _pipeClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return reply?.Type == MessageType.LogList ? Deserialize<WatchdogEventList>(reply.Payload) : null;
    }

    // ---------- Process commands ----------

    public Task<CommandResult?> SetPriorityAsync(int pid, ProcessPriorityClass priority, CancellationToken cancellationToken = default) =>
        SendCommandAsync(CommandTypes.SetPriority, pid, priority.ToString(), cancellationToken);

    public Task<CommandResult?> SetAffinityAsync(int pid, long affinityMask, CancellationToken cancellationToken = default) =>
        SendCommandAsync(CommandTypes.SetAffinity, pid, "0x" + affinityMask.ToString("X", CultureInfo.InvariantCulture), cancellationToken);

    public Task<CommandResult?> SetCpuLimitAsync(int pid, double maxPercent, CancellationToken cancellationToken = default) =>
        SendCommandAsync(CommandTypes.SetCpuLimit, pid, maxPercent.ToString(CultureInfo.InvariantCulture), cancellationToken);

    public Task<CommandResult?> RemoveCpuLimitAsync(int pid, CancellationToken cancellationToken = default) =>
        SendCommandAsync(CommandTypes.RemoveCpuLimit, pid, null, cancellationToken);

    public Task<CommandResult?> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default) =>
        SendCommandAsync(CommandTypes.TerminateProcess, pid, null, cancellationToken);

    public Task<CommandResult?> SuspendProcessAsync(int pid, CancellationToken cancellationToken = default) =>
        SendCommandAsync(CommandTypes.SuspendProcess, pid, null, cancellationToken);

    public Task<CommandResult?> ResumeProcessAsync(int pid, CancellationToken cancellationToken = default) =>
        SendCommandAsync(CommandTypes.ResumeProcess, pid, null, cancellationToken);

    /// <summary>Trims one process, or with no pid every process above the SmartTrim threshold.</summary>
    public Task<CommandResult?> TrimWorkingSetAsync(int? pid = null, CancellationToken cancellationToken = default) =>
        SendCommandAsync(CommandTypes.TrimWorkingSet, pid, null, cancellationToken);

    /// <summary>
    /// SmartTrim's "Purge now" (administrators only). The report is null when the engine couldn't be reached or refused
    /// the request; the result's Error then says why.
    /// </summary>
    public async Task<(CommandResult? Result, MemoryPurgeReport? Report)> PurgeMemoryAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendCommandAsync(CommandTypes.PurgeMemory, null, null, cancellationToken).ConfigureAwait(false);
        MemoryPurgeReport? report = null;
        if (!string.IsNullOrEmpty(result?.Data))
        {
            try { report = JsonSerializer.Deserialize<MemoryPurgeReport>(result.Data); }
            catch (JsonException) { }
        }
        return (result, report);
    }

    // ---------- Settings commands (the engine persists these) ----------

    public Task<CommandResult?> SetProBalanceEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendCommandAsync(enabled ? CommandTypes.EnableProBalance : CommandTypes.DisableProBalance, null, null, cancellationToken);

    /// <summary>Sensitivity level 1-10.</summary>
    public Task<CommandResult?> SetSensitivityAsync(int level, CancellationToken cancellationToken = default) =>
        SendCommandAsync(CommandTypes.SetSensitivity, null, level.ToString(CultureInfo.InvariantCulture), cancellationToken);

    public Task<CommandResult?> ConfigureIdleSaverAsync(IdleSaverArgument argument, CancellationToken cancellationToken = default) =>
        SendCommandAsync(CommandTypes.ConfigureIdleSaver, null, JsonSerializer.Serialize(argument), cancellationToken);

    public Task<CommandResult?> ConfigureSmartTrimAsync(SmartTrimArgument argument, CancellationToken cancellationToken = default) =>
        SendCommandAsync(CommandTypes.ConfigureSmartTrim, null, JsonSerializer.Serialize(argument), cancellationToken);

    // ---------- Runtime toggles ----------

    public Task<CommandResult?> EnablePerformanceModeAsync(bool enable = true, CancellationToken cancellationToken = default) =>
        enable
            ? SendCommandAsync(CommandTypes.EnablePerformanceMode, null, "true", cancellationToken)
            : SendCommandAsync(CommandTypes.DisablePerformanceMode, null, null, cancellationToken);

    public async Task<CommandResult?> SendCommandAsync(string commandType, int? targetPid, string? argument, CancellationToken cancellationToken = default)
    {
        var command = new CommandMessage { CommandType = commandType, TargetPid = targetPid, Argument = argument };
        var reply = await _pipeClient.SendAsync(command, cancellationToken).ConfigureAwait(false);
        if (reply == null) return null;
        if (reply.Type != MessageType.CommandResult)
            return CommandResult.Fail($"Unexpected reply {reply.Type} to {commandType}");
        return Deserialize<CommandResult>(reply.Payload) ?? CommandResult.Fail($"Empty reply to {commandType}");
    }

    public void Dispose() => _pipeClient.Dispose();

    private static T? Deserialize<T>(string payload) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Malformed {typeof(T).Name} payload from engine: {ex.Message}");
            return null;
        }
    }
}
