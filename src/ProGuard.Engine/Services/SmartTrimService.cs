using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// Periodically trims the working set of high-memory processes to free RAM.
/// </summary>
public class SmartTrimService : ISmartTrimService, IDisposable
{
    private readonly ILogger<SmartTrimService> _logger;
    private readonly IProcessMonitor _processMonitor;
    private Timer? _timer;
    
    /// <inheritdoc/>
    public long ThresholdBytes { get; set; } = 1024L * 1024L * 500L; // 500 MB

    public SmartTrimService(ILogger<SmartTrimService> logger, IProcessMonitor processMonitor)
    {
        _logger = logger;
        _processMonitor = processMonitor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SmartTrim Service");
        _timer = new Timer(_ => TrimMemory(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping SmartTrim Service");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void TrimMemory()
    {
        var processes = _processMonitor.GetAllProcesses()
            .Where(p => p.WorkingSetBytes > ThresholdBytes && !p.IsForeground)
            .ToList();

        foreach (var pInfo in processes)
        {
            TrimProcess(pInfo.Pid);
        }
    }

    /// <inheritdoc/>
    public void TrimProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (Kernel32.EmptyWorkingSet(p.Handle))
            {
                _logger.LogInformation("Trimmed memory for {ProcessName} ({Pid})", p.ProcessName, pid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to trim memory for {Pid}", pid);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
