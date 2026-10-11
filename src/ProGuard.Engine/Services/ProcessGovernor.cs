using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Models;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// Master orchestrator - coordinates all services and applies persistent rules.
/// </summary>
public class ProcessGovernor : IProcessGovernor
{
    private readonly ILogger<ProcessGovernor> _logger;
    private bool _isRunning;

    public IProcessMonitor Monitor { get; }
    public IProBalanceEngine ProBalance { get; }
    public ICpuLimiter CpuLimiter { get; }
    public ISmartTrimService SmartTrim { get; }
    public IPowerManager PowerManager { get; }
    public IWatchdogService Watchdog { get; }
    public IRuleStore RuleStore { get; }
    public bool IsRunning => _isRunning;

    public ProcessGovernor(
        ILogger<ProcessGovernor> logger,
        IProcessMonitor processMonitor,
        IProBalanceEngine proBalance,
        ICpuLimiter cpuLimiter,
        ISmartTrimService smartTrim,
        IPowerManager powerManager,
        IWatchdogService watchdog,
        IRuleStore ruleStore)
    {
        _logger = logger;
        Monitor = processMonitor;
        ProBalance = proBalance;
        CpuLimiter = cpuLimiter;
        SmartTrim = smartTrim;
        PowerManager = powerManager;
        Watchdog = watchdog;
        RuleStore = ruleStore;

        Monitor.ProcessStarted += OnProcessStarted;
        Monitor.ProcessExited += OnProcessExited;
    }

    public void Start()
    {
        _isRunning = true;
        _logger.LogInformation("Process Governor started");
    }

    public void Stop()
    {
        _isRunning = false;
        Monitor.ProcessStarted -= OnProcessStarted;
        Monitor.ProcessExited -= OnProcessExited;
        _logger.LogInformation("Process Governor stopped");
    }

    // Release the per-process job object (and its handle) once the limited process is gone.
    private void OnProcessExited(object? sender, int pid) => CpuLimiter.RemoveLimit(pid);

    public Task StartAsync(CancellationToken cancellationToken) { Start(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) { Stop(); return Task.CompletedTask; }

    private void OnProcessStarted(object? sender, ProcessInfo e)
    {
        var rule = RuleStore.GetProcessRule(e.Name);
        if (rule == null) return;

        try
        {
            using var p = Process.GetProcessById(e.Pid);

            if (rule.DisallowExecution)
            {
                _logger.LogInformation("Terminating disallowed process {Name} ({Pid})", e.Name, e.Pid);
                p.Kill();
                return;
            }

            if (rule.PriorityClass.HasValue)
            {
                p.PriorityClass = (ProcessPriorityClass)rule.PriorityClass.Value;
                _logger.LogInformation("Applied priority {Priority} to {Name} ({Pid})", rule.PriorityClass.Value, e.Name, e.Pid);
            }

            if (rule.AffinityMask.HasValue)
            {
                p.ProcessorAffinity = new IntPtr(rule.AffinityMask.Value);
                _logger.LogInformation("Applied affinity mask to {Name} ({Pid})", e.Name, e.Pid);
            }

            if (rule.CpuLimitPercent.HasValue)
            {
                CpuLimiter.AddLimit(e.Pid, rule.CpuLimitPercent.Value);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to apply rule to {Name} ({Pid})", e.Name, e.Pid); }
    }
}
