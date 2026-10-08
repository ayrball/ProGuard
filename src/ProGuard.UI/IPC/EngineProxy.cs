using System;
using System.Threading.Tasks;

namespace ProGuard.UI.IPC;

public class EngineProxy
{
    private readonly PipeClient _pipeClient;

    public event EventHandler<bool>? ConnectionStatusChanged;

    public EngineProxy()
    {
        _pipeClient = new PipeClient();
    }

    public async Task InitializeAsync()
    {
        await _pipeClient.ConnectAsync();
        ConnectionStatusChanged?.Invoke(this, _pipeClient.IsConnected);
    }

    public bool IsConnected => _pipeClient.IsConnected;

    // TODO: Map commands to pipe client send/receive
}
