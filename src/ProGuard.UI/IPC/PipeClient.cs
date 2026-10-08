using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace ProGuard.UI.IPC;

public class PipeClient
{
    private NamedPipeClientStream _pipeClient;

    public PipeClient()
    {
        _pipeClient = new NamedPipeClientStream(".", "ProGuardEngine", PipeDirection.InOut, PipeOptions.Asynchronous);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _pipeClient.ConnectAsync(1000, cancellationToken);
        }
        catch (Exception ex)
        {
            // Log connection error
            Console.WriteLine($"Connection failed: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        if (_pipeClient.IsConnected)
        {
            _pipeClient.Close();
        }
    }

    public bool IsConnected => _pipeClient.IsConnected;
}
