using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

public class ForegroundBoostService : IDisposable
{
    private readonly ILogger<ForegroundBoostService> _logger;
    private readonly IProBalanceEngine _proBalance;
    private readonly IForegroundTracker _foreground;
    private Timer? _timer;
    private int _lastBoostedPid;
    private ProcessPriorityClass _lastOriginalPriority = ProcessPriorityClass.Normal;
    // Off until the settings say otherwise (AppSettings.ForegroundBoostEnabled), as in Process Lasso.
    private bool _enabled;

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                RestorePrevious();
            }
        }
    }

    public ForegroundBoostService(ILogger<ForegroundBoostService> logger, IProBalanceEngine proBalance, IForegroundTracker foreground)
    {
        _logger = logger;
        _proBalance = proBalance;
        _foreground = foreground;
    }

    public void Start()
    {
        _timer = new Timer(CheckForegroundProcess, null, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500));
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, 0);
        RestorePrevious();
    }

    private void CheckForegroundProcess(object? state)
    {
        if (!_enabled) return;

        try
        {
            // Not GetForegroundWindow: as a service the engine is in session 0 and can't see the user's desktop.
            int currentPid = _foreground.GetForegroundPid();

            if (currentPid <= 4 || currentPid == _lastBoostedPid) return;

            // Restore previous
            RestorePrevious();

            // Check new foreground process
            try
            {
                using var p = Process.GetProcessById(currentPid);
                string name = p.ProcessName.ToLowerInvariant();

                // Skip shell & system processes
                if (name == "explorer" || name == "dwm" || name == "taskmgr" || name == "searchhost")
                {
                    return;
                }

                // ProBalance may be holding it at BelowNormal: end that first so we boost from its real priority
                // and ProBalance doesn't later mistake our boost for a user change.
                if (_proBalance.ReleaseRestraint(currentPid)) p.Refresh();

                var currentPriority = p.PriorityClass;
                if (currentPriority == ProcessPriorityClass.Normal)
                {
                    p.PriorityClass = ProcessPriorityClass.AboveNormal;
                    _lastBoostedPid = currentPid;
                    _lastOriginalPriority = currentPriority;
                    _logger.LogDebug("Foreground Boost: Elevated {ProcessName} ({Pid}) to AboveNormal", p.ProcessName, currentPid);
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error in ForegroundBoost check");
        }
    }

    private void RestorePrevious()
    {
        if (_lastBoostedPid > 0)
        {
            try
            {
                using var p = Process.GetProcessById(_lastBoostedPid);
                if (p.PriorityClass == ProcessPriorityClass.AboveNormal)
                {
                    p.PriorityClass = _lastOriginalPriority;
                    _logger.LogDebug("Foreground Boost: Restored {Pid} to {Priority}", _lastBoostedPid, _lastOriginalPriority);
                }
            }
            catch { }
            _lastBoostedPid = 0;
        }
    }

    public void Dispose()
    {
        Stop();
        _timer?.Dispose();
    }
}
