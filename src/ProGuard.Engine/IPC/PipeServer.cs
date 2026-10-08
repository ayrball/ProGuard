using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Shared.Messages;

namespace ProGuard.Engine.IPC;

public class PipeServer
{
    private readonly ILogger<PipeServer> _logger;
    private CancellationTokenSource? _cts;

    public PipeServer(ILogger<PipeServer> logger)
    {
        _logger = logger;
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
        _logger.LogInformation("PipeServer started");
        while (!token.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream("ProGuardEngine", PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);

                _ = Task.Run(() => HandleClientAsync(server, token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in pipe server loop");
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };

            while (pipe.IsConnected && !token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                try
                {
                    var msg = JsonSerializer.Deserialize<IpcMessage>(line);
                    if (msg != null)
                    {
                        // Handle message based on CommandType
                        // Send response
                        var response = new IpcMessage { Type = MessageType.Pong, Payload = "{}" };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process message from client");
                }
            }
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
