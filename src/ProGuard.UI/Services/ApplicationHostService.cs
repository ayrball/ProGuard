using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace ProGuard.UI.Services;

public class ApplicationHostService : IHostedService
{
    public ApplicationHostService()
    {
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
