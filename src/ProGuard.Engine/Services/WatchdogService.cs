using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// IFTTT Watchdog service - monitors processes and executes actions when conditions are met.
/// </summary>
public class WatchdogService : IWatchdogService, IDisposable
{
    private readonly ILogger<WatchdogService> _logger;
    private readonly IProcessMonitor _processMonitor;
    private readonly IRuleStore _ruleStore;
    private Timer? _timer;

    public WatchdogService(ILogger<WatchdogService> logger, IProcessMonitor processMonitor, IRuleStore ruleStore)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _ruleStore = ruleStore;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Watchdog Service");
        _timer = new Timer(_ => EvaluateRules(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Watchdog Service");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void AddRule(WatchdogRule rule)
    {
        _ruleStore.AddWatchdogRule(rule);
        _logger.LogInformation("Added watchdog rule for {ProcessName}", rule.TargetProcessName);
    }

    public void RemoveRule(int ruleId)
    {
        _ruleStore.DeleteWatchdogRule(ruleId);
        _logger.LogInformation("Removed watchdog rule {Id}", ruleId);
    }

    public void UpdateRule(WatchdogRule rule)
    {
        _ruleStore.UpdateWatchdogRule(rule);
        _logger.LogInformation("Updated watchdog rule {Id}", rule.Id);
    }

    internal void EvaluateRules()
    {
        // Runs on a timer thread: an exception escaping here would terminate the whole engine process.
        try
        {
            var rules = _ruleStore.GetWatchdogRules().Where(r => r.IsEnabled).ToList();
            if (!rules.Any()) return;

            var processes = _processMonitor.GetAllProcesses().ToList();

            foreach (var rule in rules)
            {
                // Rules are stored the way the UI shows them ("chrome.exe"); the monitor reports "chrome".
                var wanted = ProcessExtensions.NormalizeName(rule.TargetProcessName);
                var targets = processes.Where(p => ProcessExtensions.NormalizeName(p.Name) == wanted).ToList();
                if (!targets.Any()) continue;

                foreach (var target in targets)
                {
                    bool triggered = false;
                    switch (rule.ConditionType)
                    {
                        case WatchdogCondition.CpuAbove:
                            triggered = target.CpuPercent > rule.ThresholdValue;
                            break;
                        case WatchdogCondition.RamAbove:
                            triggered = (target.WorkingSetBytes / (1024.0 * 1024.0)) > rule.ThresholdValue;
                            break;
                        case WatchdogCondition.InstanceCountAbove:
                            triggered = targets.Count > rule.ThresholdValue;
                            break;
                    }

                    if (triggered) ExecuteAction(rule, target);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating watchdog rules");
        }
    }

    private void ExecuteAction(WatchdogRule rule, ProcessInfo target)
    {
        _logger.LogInformation("Watchdog triggered for {Name} ({Pid}) - Action: {Action}", target.Name, target.Pid, rule.ActionType);
        try
        {
            using var p = Process.GetProcessById(target.Pid);
            switch (rule.ActionType)
            {
                case WatchdogAction.Terminate:
                    p.Kill();
                    break;
                case WatchdogAction.SetPriority:
                    if (p.PriorityClass != ProcessPriorityClass.Idle) p.PriorityClass = ProcessPriorityClass.Idle;
                    break;
                case WatchdogAction.LogAlert:
                    break;
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed watchdog action on {Pid}", target.Pid); }
    }

    public void Dispose() => _timer?.Dispose();
}
