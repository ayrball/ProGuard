using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ProGuard.Shared.Client;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

/// <summary>
/// The engine is a service: Windows restarts it after a crash and the installer restarts it on every update, under a
/// UI that keeps running. Seen on an installed engine: after a reinstall the UI kept showing its last snapshot as
/// ONLINE and never asked the engine anything again.
/// </summary>
public class EngineClientReconnectTests
{
    /// <summary>Answers every request with a successful result, until disposed (which closes the connection).</summary>
    private sealed class OneEngine : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public OneEngine(string name)
        {
            _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _loop = Task.Run(async () =>
            {
                try
                {
                    await _server.WaitForConnectionAsync(_stop.Token);
                    var utf8 = new UTF8Encoding(false);
                    using var reader = new StreamReader(_server, utf8, false, 4096, leaveOpen: true);
                    using var writer = new StreamWriter(_server, utf8, 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
                    while (await reader.ReadLineAsync(_stop.Token) is not null)
                    {
                        var reply = new IpcMessage { Type = MessageType.CommandResult, Payload = JsonSerializer.Serialize(CommandResult.Ok("42")) };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(reply));
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _loop; } catch (OperationCanceledException) { }
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenTheEngineGoesAway_RequestsSayItIsUnavailable_AndWorkAgainOnceItIsBack()
    {
        var name = "ProGuard.Tests.Reconnect." + Guid.NewGuid().ToString("N");
        await using var client = new EngineClient(name, connectTimeout: TimeSpan.FromMilliseconds(300), requestTimeout: TimeSpan.FromSeconds(5));

        var first = new OneEngine(name);
        Assert.Equal(42, await client.QueryAsync<int>(CommandTypes.GetSettings));
        await first.DisposeAsync(); // the service stops: the connection the client holds is now dead

        // Not an IOException from inside the client: callers handle EngineUnavailableException, and anything else
        // stopped the UI's refresh loop for good.
        await Assert.ThrowsAsync<EngineUnavailableException>(() => client.QueryAsync<int>(CommandTypes.GetSettings));
        await Assert.ThrowsAsync<EngineUnavailableException>(() => client.QueryAsync<int>(CommandTypes.GetSettings));

        await using var second = new OneEngine(name);
        Assert.Equal(42, await client.QueryAsync<int>(CommandTypes.GetSettings));
    }
}
