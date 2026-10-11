using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;

namespace ProGuard.Engine;

/// <summary>
/// Main background service that initializes and manages all ProGuard engine components.
/// </summary>
public class EngineWorker : BackgroundService
{
    private readonly ILogger<EngineWorker> _logger;
    private readonly ProcessMonitor _processMonitor;
    private readonly ResponsivenessGuardEngine _responsiveness guardEngine;
    private readonly CpuLimiter _cpuLimiter;
    private readonly CpuAffinityLimiter _cpuAffinityLimiter;
    private readonly MemoryTrimService _memory trimService;
    private readonly PowerManager _powerManager;
    private readonly WatchdogService _watchdogService;
    private readonly ProcessOrchestrator _processOrchestrator;
    private readonly ForegroundBoostService _foregroundBoostService;
    private readonly PipeServer _pipeServer;
    private readonly ForegroundPipeServer _foregroundPipeServer;
    private readonly RuleStore _ruleStore;
    private readonly SettingsApplier _settingsApplier;
    private readonly AutoPerformanceModeService _autoPerformanceMode;
    private readonly LaunchPriorityService? _launchPriorities;

    private static readonly TimeSpan RuleReloadInterval = TimeSpan.FromSeconds(2);

    public EngineWorker(
        ILogger<EngineWorker> logger,
        ProcessMonitor processMonitor,
        ResponsivenessGuardEngine responsiveness guardEngine,
        CpuLimiter cpuLimiter,
        MemoryTrimService memory trimService,
        PowerManager powerManager,
        WatchdogService watchdogService,
        ProcessOrchestrator processOrchestrator,
        ForegroundBoostService foregroundBoostService,
        PipeServer pipeServer,
        ForegroundPipeServer foregroundPipeServer,
        RuleStore ruleStore,
        SettingsApplier settingsApplier,
        CpuAffinityLimiter cpuAffinityLimiter,
        AutoPerformanceModeService autoPerformanceMode,
        LaunchPriorityService? launchPriorities = null)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _responsiveness guardEngine = responsiveness guardEngine;
        _cpuLimiter = cpuLimiter;
        _memory trimService = memory trimService;
        _powerManager = powerManager;
        _watchdogService = watchdogService;
        _processOrchestrator = processOrchestrator;
        _foregroundBoostService = foregroundBoostService;
        _pipeServer = pipeServer;
        _foregroundPipeServer = foregroundPipeServer;
        _ruleStore = ruleStore;
        _settingsApplier = settingsApplier;
        _cpuAffinityLimiter = cpuAffinityLimiter;
        _autoPerformanceMode = autoPerformanceMode;
        _launchPriorities = launchPriorities;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProGuard.Engine started at: {time}", System.DateTimeOffset.Now);

        // Before the services start, so they come up with the user's settings rather than their defaults.
        _settingsApplier.Apply(_ruleStore.GetSettings());
        // Catches up on rules changed while the engine was stopped (forced: the registry may have changed too).
        _launchPriorities?.Sync(_ruleStore.GetProcessRules(), force: true);

        // Before anything asks for the foreground process, so a UI that's already running can reconnect.
        _foregroundPipeServer.Start();
        await _processMonitor.StartAsync(stoppingToken);
        await _responsiveness guardEngine.StartAsync(stoppingToken);
        await _cpuLimiter.StartAsync(stoppingToken);
        await _cpuAffinityLimiter.StartAsync(stoppingToken);
        await _memory trimService.StartAsync(stoppingToken);
        await _powerManager.StartAsync(stoppingToken);
        _autoPerformanceMode.Start();
        await _watchdogService.StartAsync(stoppingToken);
        await _processOrchestrator.StartAsync(stoppingToken);
        _foregroundBoostService.Start();

        _pipeServer.Start();

        // Keep alive until cancellation, picking up rule and settings changes the UI writes to the shared database.
        using var reloadTimer = new PeriodicTimer(RuleReloadInterval);
        try
        {
            while (await reloadTimer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (_ruleStore.ReloadIfChanged())
                    {
                        _settingsApplier.Apply(_ruleStore.GetSettings());
                        _launchPriorities?.Sync(_ruleStore.GetProcessRules());
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to reload rules"); }
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("ProGuard.Engine stopping at: {time}", System.DateTimeOffset.Now);
        
        _pipeServer.Stop();
        _foregroundPipeServer.Stop();
        _foregroundBoostService.Stop();

        await _processOrchestrator.StopAsync(stoppingToken);
        await _watchdogService.StopAsync(stoppingToken);
        _autoPerformanceMode.Stop();
        await _powerManager.StopAsync(stoppingToken);
        await _memory trimService.StopAsync(stoppingToken);
        await _cpuAffinityLimiter.StopAsync(stoppingToken);
        await _cpuLimiter.StopAsync(stoppingToken);
        await _responsiveness guardEngine.StopAsync(stoppingToken);
        await _processMonitor.StopAsync(stoppingToken);
    }
}
