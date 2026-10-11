using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ProGuard.Shared.Client;
using ProGuard.Shared.Messages;

namespace ProGuard.Preview;

/// <summary>
/// A stand-in for the engine's command pipe: the same line-delimited JSON protocol (one <see cref="CommandMessage"/> in,
/// one <see cref="IpcMessage"/> of type <see cref="MessageType.CommandResult"/> out), answered by a canned
/// <see cref="StandInMachine"/>. Name the pipe anything and point the UI at it with the PROGUARD_PIPE variable.
/// </summary>
public sealed class StandInEngine : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _connections = new();
    private Task? _accepting;

    public string PipeName { get; }
    public StandInMachine Machine { get; }

    /// <param name="callerIsAdministrator">False answers as the engine would to a standard user.</param>
    /// <param name="readOnly">Actions succeed without changing anything, so a render run is repeatable.</param>
    public StandInEngine(string pipeName, bool callerIsAdministrator = true, bool readOnly = false)
    {
        PipeName = pipeName;
        Machine = new StandInMachine(callerIsAdministrator, readOnly);
    }

    /// <summary>Opens the pipe and starts answering. The pipe exists by the time this returns, so a client can connect at once.</summary>
    public StandInEngine Start()
    {
        if (_accepting != null) throw new InvalidOperationException("Already started.");
        var first = NewServer();
        _accepting = Task.Run(() => AcceptAsync(first));
        return this;
    }

    private NamedPipeServerStream NewServer() =>
        new(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private async Task AcceptAsync(NamedPipeServerStream server)
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                server.Dispose();
                return;
            }

            _connections[server] = 0;
            var connected = server;
            _ = Task.Run(() => ServeAsync(connected, token), token);
            server = NewServer();
        }
        server.Dispose();
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, Utf8, bufferSize: 64 * 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            while (pipe.IsConnected && !token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line == null) return;
                await writer.WriteLineAsync(Reply(line).AsMemory(), token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client went away, or the stand-in is stopping.
        }
        finally
        {
            _connections.TryRemove(pipe, out _);
            pipe.Dispose();
        }
    }

    private string Reply(string line)
    {
        CommandResult result;
        try
        {
            var request = JsonSerializer.Deserialize<CommandMessage>(line, EngineClient.Json);
            result = request == null ? CommandResult.Fail("Empty request")
                : request.Type != MessageType.Command ? CommandResult.Fail($"Unsupported message type {request.Type}")
                : Machine.Handle(request);
        }
        catch (JsonException ex)
        {
            result = CommandResult.Fail("Malformed request: " + ex.Message);
        }

        var reply = new IpcMessage { Type = MessageType.CommandResult, Payload = JsonSerializer.Serialize(result) };
        return JsonSerializer.Serialize(reply);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var connection in _connections.Keys)
        {
            try { connection.Dispose(); } catch (ObjectDisposedException) { }
        }
        if (_accepting != null)
        {
            try { await _accepting.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
