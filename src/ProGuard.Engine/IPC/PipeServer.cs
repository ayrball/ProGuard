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
    /// <summary>Connections one account may hold at once: a UI needs one, the preview tool or a script a few more.</summary>
    internal const int MaxConnectionsPerAccount = 4;
    /// <summary>
    /// Instances kept for administrators and LocalSystem: standard clients together hold at most
    /// <see cref="MaxConnections"/> minus these, so four standard accounts at their own cap can't fill the pipe.
    /// </summary>
    internal const int ReservedForPrivileged = 4;
    /// <summary>A client that connects must send its first request within this long, or it is dropped.</summary>
    internal static readonly TimeSpan DefaultFirstRequestTimeout = TimeSpan.FromSeconds(15);
    /// <summary>A connection with no request for this long is dropped; the UI reconnects when it next asks.</summary>
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);
    /// <summary>
    /// The largest request is an imported config file (ConfigDocuments.MaxDocumentLength characters, a little more once
    /// JSON-escaped into the request). Bounded, so an unprivileged client can't make the engine buffer unbounded input.
    /// </summary>
    internal const int MaxLineLength = 1024 * 1024;

    private readonly ILogger<PipeServer> _logger;
    private readonly IpcDispatcher _dispatcher;
    private CancellationTokenSource? _cts;
    private PipeConnectionLimiter _connections = new(MaxConnectionsPerAccount, MaxConnections - ReservedForPrivileged);

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

    /// <summary>Which account a connection counts against (<see cref="MaxConnectionsPerAccount"/>). Tests replace it.</summary>
    internal Func<NamedPipeServerStream, string> AccountOf { get; init; } = PipeConnectionLimiter.AccountOf;

    /// <summary>Whether a connection may use the instances kept for administrators and LocalSystem. Tests replace it.</summary>
    internal Func<NamedPipeServerStream, bool> IsPrivileged { get; init; } = PipeConnectionLimiter.IsPrivileged;

    internal int MaxPerAccount { init => _connections = new PipeConnectionLimiter(value, _connections.MaxStandardTotal); }
    internal int MaxStandardTotal { init => _connections = new PipeConnectionLimiter(_connections.MaxPerAccount, value); }
    internal TimeSpan FirstRequestTimeout { get; init; } = DefaultFirstRequestTimeout;
    internal TimeSpan IdleTimeout { get; init; } = DefaultIdleTimeout;

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
            // With none of our instances left, the name is free: the next one must be the first, or a program that
            // created the pipe in the meantime (to capture a connecting UI's token) would serve our clients.
            var first = _connections.Open == 0;
            try
            {
                server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, MaxConnections,
                    PipeTransmissionMode.Message, PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, Security);
                await server.WaitForConnectionAsync(token);

                var connected = server;
                server = null;
                _connections.Connected();
                _ = Task.Run(() => HandleClientAsync(connected, token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (UnauthorizedAccessException ex) when (first)
            {
                _logger.LogError(ex, "Could not create the command pipe {Pipe}: another process already owns that name. Clients refuse a server that isn't the engine, so nothing is sent to it, but the UI can't reach the engine until that process exits. Retrying.", PipeName);
                try { await Task.Delay(5000, token); } catch (OperationCanceledException) { break; }
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
        string? account = null;
        var privileged = false;
        try
        {
            var candidate = AccountOf(pipe);
            privileged = IsPrivileged(pipe);
            if (!_connections.TryAdmit(candidate, privileged))
            {
                _logger.LogWarning("Refused a command pipe connection from {Account}: it already holds {Max} connections, or standard users already hold the {Total} they may share",
                    candidate, _connections.MaxPerAccount, _connections.MaxStandardTotal);
                return;
            }
            account = candidate;

            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            IIpcCaller? caller = null;

            while (pipe.IsConnected && !token.IsCancellationRequested)
            {
                // A client that never writes, or stops, would otherwise hold its instance for good.
                string? line;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    idle.CancelAfter(caller == null ? FirstRequestTimeout : IdleTimeout);
                    try
                    {
                        line = await PipeLineReader.ReadLineAsync(reader, MaxLineLength, idle.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        _logger.LogDebug("Dropping an idle command pipe client ({Account})", account);
                        break;
                    }
                }
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
                // A client that asks and never reads would otherwise hold its instance on a blocked write.
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    idle.CancelAfter(IdleTimeout);
                    try
                    {
                        await writer.WriteLineAsync(response.AsMemory(), idle.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        _logger.LogDebug("Dropping a command pipe client that doesn't read its replies ({Account})", account);
                        break;
                    }
                }
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
            if (account != null) _connections.Release(account, privileged);
            _connections.Closed();
        }
    }
}
