using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services.ProcessRules;

namespace ProGuard.Engine.Services;

/// <summary>
/// Master orchestrator - coordinates all services and applies persistent rules.
/// </summary>
public class ProcessGovernor : IProcessGovernor
{
    private readonly ILogger<ProcessGovernor> _logger;
    private readonly InstanceLimiter? _instanceLimiter;
    private readonly InstanceBalancer? _instanceBalancer;
    private readonly StartupBoostService? _startupBoost;
    private bool _isRunning;

    public IProcessMonitor Monitor { get; }
    public IProBalanceEngine ProBalance { get; }
    public ICpuLimiter CpuLimiter { get; }
    public ISmartTrimService SmartTrim { get; }
    public IPowerManager PowerManager { get; }
    public IWatchdogService Watchdog { get; }
    public IRuleStore RuleStore { get; }
    /// <summary>I/O, memory, EcoQoS, boost, CPU Sets and GPU settings from the rule; null when not wired up.</summary>
    public PersistentRuleService? PersistentRules { get; }
    public bool IsRunning => _isRunning;

    public ProcessGovernor(
        ILogger<ProcessGovernor> logger,
        IProcessMonitor processMonitor,
        IProBalanceEngine proBalance,
        ICpuLimiter cpuLimiter,
        ISmartTrimService smartTrim,
        IPowerManager powerManager,
        IWatchdogService watchdog,
        IRuleStore ruleStore,
        InstanceLimiter? instanceLimiter = null,
        InstanceBalancer? instanceBalancer = null,
        StartupBoostService? startupBoost = null,
        PersistentRuleService? persistentRules = null)
    {
        _logger = logger;
        Monitor = processMonitor;
        ProBalance = proBalance;
        CpuLimiter = cpuLimiter;
        SmartTrim = smartTrim;
        PowerManager = powerManager;
        Watchdog = watchdog;
        RuleStore = ruleStore;
        _instanceLimiter = instanceLimiter;
        _instanceBalancer = instanceBalancer;
        _startupBoost = startupBoost;

        PersistentRules = persistentRules;

        Monitor.ProcessStarted += OnProcessStarted;
        Monitor.ProcessExited += OnProcessExited;
    }

    public void Start()
    {
        _isRunning = true;
        _startupBoost?.Start();
        _instanceBalancer?.Start();

        PersistentRules?.Start();
        _logger.LogInformation("Process Governor started");
    }

    public void Stop()
    {
        _isRunning = false;
        PersistentRules?.Stop();
        Monitor.ProcessStarted -= OnProcessStarted;
        Monitor.ProcessExited -= OnProcessExited;
        _startupBoost?.Stop();
        _instanceBalancer?.Stop();
        _logger.LogInformation("Process Governor stopped");
    }

    // Release the per-process job object (and its handle) once the limited process is gone.
    private void OnProcessExited(object? sender, int pid)
    {
        CpuLimiter.RemoveLimit(pid);
        _instanceLimiter?.OnProcessExited(pid);
        _startupBoost?.OnProcessExited(pid);
        _instanceBalancer?.OnProcessExited(pid); // re-splits the cores among the remaining instances

        PersistentRules?.Forget(pid);
    }

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

            // Before anything else is applied: this may be an instance over the limit.
            if (_instanceLimiter?.Enforce(e, rule) == true) return;

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

        if (rule.DisallowExecution) return;

        // Separate from the block above so a priority/affinity failure doesn't skip these. Logs and never throws.
        PersistentRules?.Apply(rule, new ProcessTarget(e.Pid, e.Name, e.FilePath));

        // After the rule's priority and affinity, so those are what the boost and the balancer restore.
        _startupBoost?.TryBoost(e, rule);
        _instanceBalancer?.OnProcessStarted(e, rule);
    }
}
