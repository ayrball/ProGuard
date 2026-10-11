using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Shared.Messages;

namespace ProGuard.Engine.IPC;

/// <summary>
/// Request/reply command pipe. Interactive users can connect, because the UI runs unelevated, so every request is
/// checked against the client's own token (see <see cref="IpcAuthorization"/>) before the engine acts on it.
/// </summary>
public class PipeServer
{
    public const string DefaultPipeName = PipeNames.Engine;

    /// <summary>Enough for every session's UI (and a few tools) without letting one user exhaust the engine.</summary>
    internal const int MaxConnections = 16;
    /// <summary>The largest request is a serialized AppSettings, well under this.</summary>
    internal const int MaxLineLength = 64 * 1024;

    private readonly ILogger<PipeServer> _logger;
    private readonly IpcDispatcher _dispatcher;
    private CancellationTokenSource? _cts;

    public PipeServer(ILogger<PipeServer> logger, IpcDispatcher dispatcher)
    {
        _logger = logger;
        _dispatcher = dispatcher;
    }

    /// <summary>Name of the pipe to listen on; tests use a unique one so they don't collide with a running engine.</summary>
    public string PipeName { get; init; } = DefaultPipeName;

    /// <summary>Who may connect. Tests narrow it to check what an unelevated client actually needs.</summary>
    public PipeSecurity Security { get; init; } = CreateSecurity();

    /// <summary>How a connected client is identified. Tests of request routing (rather than authorization) replace it.</summary>
    internal Func<NamedPipeServerStream, IIpcCaller?> IdentifyCaller { get; init; } = PipeCaller.Identify;

    /// <summary>
    /// Interactive users (the UI on any desktop, including Remote Desktop sessions) may read and write. That excludes
    /// creating instances, so they can't stand up a rival server on this name. SYSTEM, Administrators and the
    /// engine's own account get full control, which creating further instances needs. Network logons are denied
    /// outright: the pipe is for this machine only.
    /// </summary>
    public static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
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

    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task ServerLoop(CancellationToken token)
    {
        _logger.LogInformation("PipeServer started on {Pipe}", PipeName);
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, MaxConnections,
                    PipeTransmissionMode.Message, PipeOptions.Asynchronous, 0, 0, Security);
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
                _logger.LogDebug(ex, "Command pipe unavailable");
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in pipe server loop");
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
        try
        {
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            IIpcCaller? caller = null;

            while (pipe.IsConnected && !token.IsCancellationRequested)
            {
                var line = await PipeLineReader.ReadLineAsync(reader, MaxLineLength, token);
                if (line == null) break;

                // Windows only lets a server impersonate its client once it has read from the pipe, so the client is
                // identified when its first request arrives.
                caller ??= IdentifyCaller(pipe);
                if (caller == null)
                {
                    _logger.LogWarning("Dropping a command pipe client that could not be identified");
                    await writer.WriteLineAsync(JsonSerializer.Serialize(IpcDispatcher.Result(CommandResult.Fail("Access denied: could not identify the caller"))));
                    break;
                }

                // One reply line per request line; the dispatcher turns failures into an error reply.
                var response = _dispatcher.HandleLine(line, caller);
                await writer.WriteLineAsync(response);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            _logger.LogDebug(ex, "Command pipe client dropped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client connection error");
        }
        finally
        {
            pipe.Dispose();
        }
    }
}
