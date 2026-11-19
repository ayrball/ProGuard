namespace ProGuard.Core.Services;

public interface IProcessOrchestrator
{
    IProcessMonitor Monitor { get; }
    IResponsivenessGuardEngine ResponsivenessGuard { get; }
    ICpuLimiter CpuLimiter { get; }
    IMemoryTrimService MemoryTrim { get; }
    IPowerManager PowerManager { get; }
    IWatchdogService Watchdog { get; }
    IRuleStore RuleStore { get; }

    bool IsRunning { get; }
    void Start();
    void Stop();
}
