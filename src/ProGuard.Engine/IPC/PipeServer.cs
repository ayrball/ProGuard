using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ProGuard.Engine.IPC;

public class PipeServer
{
    public const string DefaultPipeName = "ProGuardEngine";

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
                var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
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

                // One reply line per request line; the dispatcher turns failures into an error reply.
                var response = _dispatcher.HandleLine(line);
                await writer.WriteLineAsync(response);
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
