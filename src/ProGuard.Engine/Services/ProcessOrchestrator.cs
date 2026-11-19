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
public class ProcessOrchestrator : IProcessOrchestrator
{
    private readonly ILogger<ProcessOrchestrator> _logger;
    private readonly InstanceLimiter? _instanceLimiter;
    private readonly InstanceSpread? _instanceSpread;
    private readonly StartupBoostService? _startupBoost;
    /// <summary>Asks Windows whether a process is critical. Null (tests of fake processes) checks names only.</summary>
    private readonly IRuleProcessAccess? _access;
    private bool _isRunning;

    public IProcessMonitor Monitor { get; }
    public IResponsivenessGuardEngine ResponsivenessGuard { get; }
    public ICpuLimiter CpuLimiter { get; }
    public IMemoryTrimService MemoryTrim { get; }
    public IPowerManager PowerManager { get; }
    public IWatchdogService Watchdog { get; }
    public IRuleStore RuleStore { get; }
    /// <summary>I/O, memory, EcoQoS, boost, CPU Sets and GPU settings from the rule; null when not wired up.</summary>
    public PersistentRuleService? PersistentRules { get; }
    public bool IsRunning => _isRunning;

    /// <summary>Which processes a rule may act on (see <see cref="RuleScope"/>). Tests supply fake owners.</summary>
    internal RuleScope Scope { get; set; } = RuleScope.Default;

    public ProcessOrchestrator(
        ILogger<ProcessOrchestrator> logger,
        IProcessMonitor processMonitor,
        IResponsivenessGuardEngine responsivenessGuard,
        ICpuLimiter cpuLimiter,
        IMemoryTrimService memoryTrim,
        IPowerManager powerManager,
        IWatchdogService watchdog,
        IRuleStore ruleStore,
        InstanceLimiter? instanceLimiter = null,
        InstanceSpread? instanceSpread = null,
        StartupBoostService? startupBoost = null,
        PersistentRuleService? persistentRules = null,
        IRuleProcessAccess? access = null)
    {
        _access = access;
        _logger = logger;
        Monitor = processMonitor;
        ResponsivenessGuard = responsivenessGuard;
        CpuLimiter = cpuLimiter;
        MemoryTrim = memoryTrim;
        PowerManager = powerManager;
        Watchdog = watchdog;
        RuleStore = ruleStore;
        _instanceLimiter = instanceLimiter;
        _instanceSpread = instanceSpread;
        _startupBoost = startupBoost;

        PersistentRules = persistentRules;

        Monitor.ProcessStarted += OnProcessStarted;
        Monitor.ProcessExited += OnProcessExited;
    }

    public void Start()
    {
        _isRunning = true;
        _startupBoost?.Start();
        _instanceSpread?.Start();

        PersistentRules?.Start();
        _logger.LogInformation("Process Orchestrator started");
    }

    public void Stop()
    {
        _isRunning = false;
        PersistentRules?.Stop();
        Monitor.ProcessStarted -= OnProcessStarted;
        Monitor.ProcessExited -= OnProcessExited;
        _startupBoost?.Stop();
        _instanceSpread?.Stop();
        _logger.LogInformation("Process Orchestrator stopped");
    }

    // Release the per-process job object (and its handle) once the limited process is gone.
    private void OnProcessExited(object? sender, int pid)
    {
        CpuLimiter.RemoveLimit(pid);
        _instanceLimiter?.OnProcessExited(pid);
        _startupBoost?.OnProcessExited(pid);
        _instanceSpread?.OnProcessExited(pid); // re-splits the cores among the remaining instances

        PersistentRules?.Forget(pid);
    }

    public Task StartAsync(CancellationToken cancellationToken) { Start(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) { Stop(); return Task.CompletedTask; }

    /// <summary>
    /// Whether every rule must leave <paramref name="process"/> alone, whatever it says: Idle and System, the engine
    /// itself, the session, security and ProGuard processes (<see cref="CriticalProcesses.IsCriticalName"/>), and any
    /// process Windows marks critical (its end would bug-check the machine). That last check needs the
    /// <see cref="IRuleProcessAccess"/>, which counts a process it can't query as critical.
    /// </summary>
    internal bool IsProtected(ProcessInfo process) =>
        process.Pid <= 4 || process.Pid == Environment.ProcessId || CriticalProcesses.IsCriticalName(process.Name)
        || _access?.IsCritical(process.Pid) == true;

    private void OnProcessStarted(object? sender, ProcessInfo e)
    {
        // A standard user's rule acts only on that user's own processes; everyone else's start untouched.
        var rule = Scope.RuleFor(RuleStore, e);
        if (rule == null) return;

        // Before anything at all: no rule ends, caps, re-prioritises or pins a process Windows can't do without.
        if (IsProtected(e))
        {
            _logger.LogWarning("The rule for {Rule} matched {Name} ({Pid}), a protected system process; not applying it", rule.ProcessName, e.Name, e.Pid);
            return;
        }

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

        // After the rule's priority and affinity, so those are what the boost and Instance Spread restore.
        _startupBoost?.TryBoost(e, rule);
        _instanceSpread?.OnProcessStarted(e, rule);
    }
}
