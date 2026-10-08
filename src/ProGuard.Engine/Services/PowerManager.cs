using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// Manages Windows power plans, Performance Mode, and IdleSaver.
/// </summary>
public class PowerManager : IPowerManager
{
    private readonly ILogger<PowerManager> _logger;
    private Guid _originalPlan;
    private bool _performanceModeActive;
    private bool _idleSaverEnabled;
    private int _idleTimeoutMinutes = 15;
    private Guid _idlePlanId = Powrprof.PowerSaver;

    public PowerManager(ILogger<PowerManager> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Power Manager");
        _originalPlan = GetActivePlan();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Power Manager, restoring original plan");
        if (_performanceModeActive)
        {
            SetActivePlan(_originalPlan);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public IEnumerable<(Guid Id, string Name)> GetAvailablePlans()
    {
        var plans = new List<(Guid, string)>();
        // Add well-known plans
        plans.Add((Powrprof.HighPerformance, "High Performance"));
        plans.Add((new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), "Balanced"));
        plans.Add((Powrprof.PowerSaver, "Power Saver"));

        _logger.LogDebug("Returning {Count} power plans", plans.Count);
        return plans;
    }

    /// <inheritdoc/>
    public Guid GetActivePlan()
    {
        try
        {
            if (Powrprof.PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) == 0 && ptr != IntPtr.Zero)
            {
                var guid = (Guid)Marshal.PtrToStructure(ptr, typeof(Guid))!;
                Marshal.FreeHGlobal(ptr);
                return guid;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active power scheme");
        }
        return Guid.Empty;
    }

    /// <inheritdoc/>
    public void SetActivePlan(Guid planId)
    {
        try
        {
            var result = Powrprof.PowerSetActiveScheme(IntPtr.Zero, ref planId);
            if (result == 0)
            {
                _logger.LogInformation("Set active power plan to {Plan}", planId);
            }
            else
            {
                _logger.LogError("Failed to set power plan. Error: {Error}", result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set active power scheme");
        }
    }

    /// <inheritdoc/>
    public void EnablePerformanceMode(bool enable)
    {
        if (enable && !_performanceModeActive)
        {
            _originalPlan = GetActivePlan();
            SetActivePlan(Powrprof.HighPerformance);
            _performanceModeActive = true;
            _logger.LogInformation("Performance Mode ENABLED");
        }
        else if (!enable && _performanceModeActive)
        {
            SetActivePlan(_originalPlan);
            _performanceModeActive = false;
            _logger.LogInformation("Performance Mode DISABLED");
        }
    }

    /// <inheritdoc/>
    public void ConfigureIdleSaver(bool enable, int timeoutMinutes, Guid idlePlanId)
    {
        _idleSaverEnabled = enable;
        _idleTimeoutMinutes = timeoutMinutes;
        _idlePlanId = idlePlanId;
        _logger.LogInformation("IdleSaver configured: Enabled={Enabled}, Timeout={Timeout}min, Plan={Plan}",
            enable, timeoutMinutes, idlePlanId);
    }
}
