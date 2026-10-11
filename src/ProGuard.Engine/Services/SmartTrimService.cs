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
    private readonly object _sync = new();
    private Timer? _timer;
    private bool _started;
    private bool _enabled = true;
    private TimeSpan _interval = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    
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
        lock (_sync)
        {
            _started = true;
            Reschedule();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping SmartTrim Service");
        lock (_sync)
        {
            _started = false;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public bool IsEnabled
    {
        get { lock (_sync) return _enabled; }
    }

    /// <inheritdoc/>
    public TimeSpan Interval
    {
        get { lock (_sync) return _interval; }
    }

    /// <inheritdoc/>
    public void Configure(bool enabled, TimeSpan interval, long thresholdBytes)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _interval = interval < MinInterval ? MinInterval : interval;
            ThresholdBytes = Math.Max(0, thresholdBytes);
            Reschedule();
        }
        _logger.LogInformation("SmartTrim configured: Enabled={Enabled}, Interval={Interval}s, Threshold={Threshold}MB",
            enabled, _interval.TotalSeconds, ThresholdBytes / (1024 * 1024));
    }

    // Caller holds _sync. The timer only exists while the service is started and enabled.
    private void Reschedule()
    {
        if (!_started || !_enabled)
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        _timer ??= new Timer(_ => SafeTrimMemory(), null, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(_interval, _interval);
    }

    // Timer-thread entry point: an exception escaping a timer callback would terminate the engine process.
    private void SafeTrimMemory()
    {
        try { TrimMemory(); }
        catch (Exception ex) { _logger.LogError(ex, "SmartTrim pass failed"); }
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
