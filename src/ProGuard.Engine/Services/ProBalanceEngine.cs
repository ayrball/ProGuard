using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Models;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// ProBalance algorithm - dynamically lowers priority of CPU-hogging background processes.
/// </summary>
public class ProBalanceEngine : IProBalanceEngine, IDisposable
{
    private readonly ILogger<ProBalanceEngine> _logger;
    private readonly IProcessMonitor _processMonitor;
    private readonly IRuleStore _ruleStore;
    private Timer? _timer;
    private readonly ConcurrentDictionary<int, ProcessPriorityClass> _restrainedProcesses = new();
    private readonly HashSet<string> _exclusions = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled = true;
    private int _sensitivity = 5;

    public double CpuLoadThreshold { get; set; } = 85.0;
    public double ProcessCpuThreshold { get; set; } = 15.0;
    public double CpuRecoveryThreshold { get; set; } = 50.0;

    public bool IsEngaged => _restrainedProcesses.Count > 0;
    public int ActiveInterventions => _restrainedProcesses.Count;

    public ProBalanceEngine(ILogger<ProBalanceEngine> logger, IProcessMonitor processMonitor, IRuleStore ruleStore)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _ruleStore = ruleStore;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting ProBalance Engine");
        _timer = new Timer(Evaluate, null, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping ProBalance Engine");
        _timer?.Change(Timeout.Infinite, 0);
        RestoreAll();
        return Task.CompletedTask;
    }

    public void SetEnabled(bool enabled) => _enabled = enabled;

    public void SetSensitivity(int level)
    {
        _sensitivity = Math.Clamp(level, 1, 10);
        CpuLoadThreshold = 100.0 - (_sensitivity * 7.0);
        ProcessCpuThreshold = 25.0 - (_sensitivity * 2.0);
        CpuRecoveryThreshold = CpuLoadThreshold - 20.0;
    }

    public void ExcludeProcess(string name) => _exclusions.Add(name);
    public void RemoveExclusion(string name) => _exclusions.Remove(name);

    private void Evaluate(object? state)
    {
        if (!_enabled) return;
        try
        {
            var systemCpu = Core.Helpers.SystemInfo.GetOverallCpuPercent();
            if (systemCpu > CpuLoadThreshold)
                RestrainHogs();
            else if (systemCpu < CpuRecoveryThreshold && _restrainedProcesses.Count > 0)
                RestoreSome();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ProBalance evaluation loop");
        }
    }

    private void RestrainHogs()
    {
        var processes = _processMonitor.GetAllProcesses()
            .Where(p => p.CpuPercent >= ProcessCpuThreshold && !p.IsForeground)
            .OrderByDescending(p => p.CpuPercent)
            .ToList();

        foreach (var pInfo in processes)
        {
            if (_restrainedProcesses.ContainsKey(pInfo.Pid)) continue;
            if (_exclusions.Contains(pInfo.Name)) continue;

            var rule = _ruleStore.GetProcessRule(pInfo.Name);
            if (rule != null && rule.ExcludeFromProBalance) continue;

            try
            {
                using var p = Process.GetProcessById(pInfo.Pid);
                var origPriority = p.PriorityClass;
                if (origPriority == ProcessPriorityClass.Idle || origPriority == ProcessPriorityClass.BelowNormal)
                    continue;

                p.PriorityClass = ProcessPriorityClass.BelowNormal;
                _restrainedProcesses[pInfo.Pid] = origPriority;

                var action = new ProBalanceAction
                {
                    Timestamp = DateTime.UtcNow,
                    ProcessName = pInfo.Name,
                    Pid = pInfo.Pid,
                    OriginalPriority = (int)origPriority,
                    NewPriority = (int)ProcessPriorityClass.BelowNormal,
                    Reason = $"High CPU usage ({pInfo.CpuPercent:F1}%)",
                    IsRestored = false
                };
                _ = _ruleStore.LogProBalanceActionAsync(action);

                _logger.LogInformation("ProBalance restrained {ProcessName} ({Pid}) from {Orig} to BelowNormal",
                    pInfo.Name, pInfo.Pid, origPriority);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to restrain process {Pid}", pInfo.Pid); }
        }
    }

    private void RestoreSome()
    {
        foreach (var pid in _restrainedProcesses.Keys.ToList()) RestoreProcess(pid);
    }

    private void RestoreAll()
    {
        foreach (var pid in _restrainedProcesses.Keys.ToList()) RestoreProcess(pid);
    }

    private void RestoreProcess(int pid)
    {
        if (_restrainedProcesses.TryRemove(pid, out var origPriority))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.PriorityClass = origPriority;
                _logger.LogInformation("ProBalance restored {Pid} to {Orig}", pid, origPriority);
            }
            catch { /* Process likely exited */ }
        }
    }

    public void Dispose() => _timer?.Dispose();
}
