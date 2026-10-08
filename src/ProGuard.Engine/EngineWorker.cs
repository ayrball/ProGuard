using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;

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
    private readonly MemoryTrimService _memory trimService;
    private readonly PowerManager _powerManager;
    private readonly WatchdogService _watchdogService;
    private readonly ProcessOrchestrator _processOrchestrator;
    private readonly ForegroundBoostService _foregroundBoostService;
    private readonly PipeServer _pipeServer;
    private readonly RuleStore _ruleStore;

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
        RuleStore ruleStore)
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
        _ruleStore = ruleStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProGuard.Engine started at: {time}", System.DateTimeOffset.Now);

        await _processMonitor.StartAsync(stoppingToken);
        await _responsiveness guardEngine.StartAsync(stoppingToken);
        await _cpuLimiter.StartAsync(stoppingToken);
        await _memory trimService.StartAsync(stoppingToken);
        await _powerManager.StartAsync(stoppingToken);
        await _watchdogService.StartAsync(stoppingToken);
        await _processOrchestrator.StartAsync(stoppingToken);
        _foregroundBoostService.Start();

        _pipeServer.Start();

        // Keep alive until cancellation, picking up rule changes the UI writes to the shared database.
        using var reloadTimer = new PeriodicTimer(RuleReloadInterval);
        try
        {
            while (await reloadTimer.WaitForNextTickAsync(stoppingToken))
            {
                try { _ruleStore.ReloadIfChanged(); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to reload rules"); }
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("ProGuard.Engine stopping at: {time}", System.DateTimeOffset.Now);
        
        _pipeServer.Stop();
        _foregroundBoostService.Stop();

        await _processOrchestrator.StopAsync(stoppingToken);
        await _watchdogService.StopAsync(stoppingToken);
        await _powerManager.StopAsync(stoppingToken);
        await _memory trimService.StopAsync(stoppingToken);
        await _cpuLimiter.StopAsync(stoppingToken);
        await _responsiveness guardEngine.StopAsync(stoppingToken);
        await _processMonitor.StopAsync(stoppingToken);
    }
}
