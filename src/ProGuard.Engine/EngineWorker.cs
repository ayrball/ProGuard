using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Engine.Services.SessionAgents;

namespace ProGuard.Engine;

/// <summary>
/// Main background service that initializes and manages all ProGuard engine components.
/// </summary>
public class EngineWorker : BackgroundService
{
    private readonly ILogger<EngineWorker> _logger;
    private readonly ProcessMonitor _processMonitor;
    private readonly ProBalanceEngine _proBalanceEngine;
    private readonly CpuLimiter _cpuLimiter;
    private readonly CpuAffinityLimiter _cpuAffinityLimiter;
    private readonly SmartTrimService _smartTrimService;
    private readonly PowerManager _powerManager;
    private readonly WatchdogService _watchdogService;
    private readonly ProcessGovernor _processGovernor;
    private readonly ForegroundBoostService _foregroundBoostService;
    private readonly PipeServer _pipeServer;
    private readonly ForegroundPipeServer _foregroundPipeServer;
    private readonly RuleStore _ruleStore;
    private readonly SettingsApplier _settingsApplier;
    private readonly AutoPerformanceModeService _autoPerformanceMode;
    private readonly LaunchPriorityService? _launchPriorities;
    private readonly TimerResolutionService? _timerResolution;
    private readonly CoreParkingService? _coreParking;
    private readonly SessionAgentSupervisor? _sessionAgents;

    private static readonly TimeSpan RuleReloadInterval = TimeSpan.FromSeconds(2);

    public EngineWorker(
        ILogger<EngineWorker> logger,
        ProcessMonitor processMonitor,
        ProBalanceEngine proBalanceEngine,
        CpuLimiter cpuLimiter,
        SmartTrimService smartTrimService,
        PowerManager powerManager,
        WatchdogService watchdogService,
        ProcessGovernor processGovernor,
        ForegroundBoostService foregroundBoostService,
        PipeServer pipeServer,
        ForegroundPipeServer foregroundPipeServer,
        RuleStore ruleStore,
        SettingsApplier settingsApplier,
        CpuAffinityLimiter cpuAffinityLimiter,
        AutoPerformanceModeService autoPerformanceMode,
        LaunchPriorityService? launchPriorities = null,
        TimerResolutionService? timerResolution = null,
        CoreParkingService? coreParking = null,
        SessionAgentSupervisor? sessionAgents = null)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _proBalanceEngine = proBalanceEngine;
        _cpuLimiter = cpuLimiter;
        _smartTrimService = smartTrimService;
        _powerManager = powerManager;
        _watchdogService = watchdogService;
        _processGovernor = processGovernor;
        _foregroundBoostService = foregroundBoostService;
        _pipeServer = pipeServer;
        _foregroundPipeServer = foregroundPipeServer;
        _ruleStore = ruleStore;
        _settingsApplier = settingsApplier;
        _cpuAffinityLimiter = cpuAffinityLimiter;
        _autoPerformanceMode = autoPerformanceMode;
        _launchPriorities = launchPriorities;
        _timerResolution = timerResolution;
        _coreParking = coreParking;
        _sessionAgents = sessionAgents;
    }

    internal const string IncreaseBasePriorityPrivilege = "SeIncreaseBasePriorityPrivilege";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProGuard.Engine started at: {time}", System.DateTimeOffset.Now);

        // High I/O priority and Realtime priority class need SeIncreaseBasePriorityPrivilege enabled, not just held:
        // an elevated token has it disabled, and without it NtSetInformationProcess refuses High and SetPriorityClass
        // quietly gives High instead of Realtime. Enabled once on the process token, before any rule or request runs.
        if (new Win32MemoryOperations().EnablePrivilege(IncreaseBasePriorityPrivilege) is { } privilegeProblem)
            _logger.LogWarning("High I/O priority and Realtime priority won't work: {Problem}", privilegeProblem);

        // Before ProBalance can restrain anything: core parking values a crashed run left changed go back first.
        if (_coreParking != null)
        {
            _coreParking.RecoverFromLastRun();
            _proBalanceEngine.RestraintsUpdated += _coreParking.Update;
        }

        // Before the services start, so they come up with the user's settings rather than their defaults.
        _settingsApplier.Apply(_ruleStore.GetSettings());
        // Catches up on rules changed while the engine was stopped (forced: the registry may have changed too).
        _launchPriorities?.Sync(_ruleStore.GetProcessRules(), force: true);

        // Before anything asks for the foreground process, so a UI that's already running can reconnect.
        _foregroundPipeServer.Start();
        // Then the agents that report to it, one per user session, so the reports don't depend on the UI being open.
        _sessionAgents?.Start();
        await _processMonitor.StartAsync(stoppingToken);
        await _proBalanceEngine.StartAsync(stoppingToken);
        await _cpuLimiter.StartAsync(stoppingToken);
        await _cpuAffinityLimiter.StartAsync(stoppingToken);
        await _smartTrimService.StartAsync(stoppingToken);
        await _powerManager.StartAsync(stoppingToken);
        _autoPerformanceMode.Start();
        await _watchdogService.StartAsync(stoppingToken);
        await _processGovernor.StartAsync(stoppingToken);
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
        
        // Each step on its own: one that fails (at Windows shutdown, services this depends on may already be gone) must
        // not skip the ones after it, such as putting the user's power plan back or releasing the timer resolution.
        StopStep("pipe server", _pipeServer.Stop);
        StopStep("session agents", () => _sessionAgents?.Stop());
        StopStep("foreground pipe server", _foregroundPipeServer.Stop);
        StopStep("Foreground Boost", _foregroundBoostService.Stop);

        await StopStepAsync("Process Governor", () => _processGovernor.StopAsync(stoppingToken));
        await StopStepAsync("Watchdog", () => _watchdogService.StopAsync(stoppingToken));
        StopStep("automatic Performance Mode", _autoPerformanceMode.Stop);
        await StopStepAsync("Power Manager", () => _powerManager.StopAsync(stoppingToken));
        await StopStepAsync("SmartTrim", () => _smartTrimService.StopAsync(stoppingToken));
        await StopStepAsync("CPU Limiter (affinity)", () => _cpuAffinityLimiter.StopAsync(stoppingToken));
        await StopStepAsync("CPU Limiter", () => _cpuLimiter.StopAsync(stoppingToken));
        await StopStepAsync("ProBalance", () => _proBalanceEngine.StopAsync(stoppingToken));
        StopStep("core parking", () => _coreParking?.Stop());
        await StopStepAsync("Process Monitor", () => _processMonitor.StopAsync(stoppingToken));
        StopStep("timer resolution", () => _timerResolution?.Release());
    }

    private void StopStep(string what, Action step)
    {
        try { step(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Stopping {What} failed; carrying on with the rest of shutdown", what); }
    }

    private async Task StopStepAsync(string what, Func<Task> step)
    {
        try { await step(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Stopping {What} failed; carrying on with the rest of shutdown", what); }
    }
}
