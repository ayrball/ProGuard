using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Native;
using ProGuard.Engine.Services;
using ProGuard.Shared.Messages;

namespace ProGuard.Engine.IPC;

/// <summary>
/// Receives foreground-process reports from the UIs running on users' desktops (see <see cref="ForegroundTracker"/>).
/// Unlike <see cref="PipeServer"/>, any authenticated user may write here, because the UI runs unelevated; so the
/// pipe accepts <see cref="CommandTypes.ReportForeground"/> and nothing else, and never replies.
/// </summary>
public sealed class ForegroundPipeServer
{
    /// <summary>Enough for every session's UI (and a spare) without letting one user exhaust the engine.</summary>
    internal const int MaxConnections = 16;
    /// <summary>A report is one short JSON line; anything longer is not a ProGuard UI.</summary>
    internal const int MaxLineLength = 512;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<ForegroundPipeServer> _logger;
    private readonly ForegroundTracker _tracker;
    private CancellationTokenSource? _cts;
    private long _nextConnectionId;

    public ForegroundPipeServer(ILogger<ForegroundPipeServer> logger, ForegroundTracker tracker)
    {
        _logger = logger;
        _tracker = tracker;
    }

    /// <summary>Tests use a unique name so they don't collide with a running engine.</summary>
    public string PipeName { get; init; } = PipeNames.Foreground;

    /// <summary>Who may connect and write. Tests narrow it to check what an unelevated client actually needs.</summary>
    public PipeSecurity Security { get; init; } = CreateSecurity();

    /// <summary>
    /// SYSTEM, Administrators and the engine's own account get full control (creating further pipe instances needs
    /// it); other signed-in users may only read and write. Write here maps to what a client opening the pipe for
    /// writing asks for, and excludes creating instances, so users can't stand up a rival server on this name.
    /// </summary>
    public static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        using var self = WindowsIdentity.GetCurrent();
        if (self.User != null)
            security.AddAccessRule(new PipeAccessRule(self.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => ServerLoop(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    private async Task ServerLoop(CancellationToken token)
    {
        _logger.LogInformation("Foreground pipe server started on {Pipe}", PipeName);
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, MaxConnections,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security);
                await server.WaitForConnectionAsync(token);

                var connected = server;
                server = null;
                _ = Task.Run(() => HandleClientAsync(connected, token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                // All instances busy: wait for one to free up rather than spinning.
                _logger.LogDebug(ex, "Foreground pipe unavailable");
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in foreground pipe server loop");
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var connectionId = Interlocked.Increment(ref _nextConnectionId);
        try
        {
            if (!Kernel32.GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session))
            {
                _logger.LogDebug("Rejecting foreground reporter: client session unknown");
                return;
            }

            using var reader = new StreamReader(pipe, Encoding.UTF8);
            while (pipe.IsConnected && !token.IsCancellationRequested)
            {
                var line = await PipeLineReader.ReadLineAsync(reader, MaxLineLength, token);
                if (line == null) break;
                if (line.Length == 0) continue;

                var message = JsonSerializer.Deserialize<CommandMessage>(line, JsonOptions);
                if (message?.Type != MessageType.Command || message.CommandType != CommandTypes.ReportForeground)
                {
                    _logger.LogDebug("Foreground pipe ignored a {Type}/{Command} message", message?.Type, message?.CommandType);
                    continue;
                }
                _tracker.Report(connectionId, (int)session, message.TargetPid ?? 0, ParseIdle(message.Argument));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            _logger.LogDebug(ex, "Foreground reporter dropped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Foreground reporter connection error");
        }
        finally
        {
            _tracker.Disconnect(connectionId);
            pipe.Dispose();
        }
    }

    /// <summary>Argument of <see cref="CommandTypes.ReportForeground"/>: idle milliseconds, absent from older UIs.</summary>
    internal static TimeSpan? ParseIdle(string? argument) =>
        long.TryParse(argument, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            ? TimeSpan.FromMilliseconds(Math.Min(ms, uint.MaxValue))  // GetLastInputInfo can't say more; avoids overflow
            : null;
}
