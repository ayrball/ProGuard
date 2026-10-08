namespace ProGuard.Core.Services;

public interface IProcessGovernor
{
    IProcessMonitor Monitor { get; }
    IProBalanceEngine ProBalance { get; }
    ICpuLimiter CpuLimiter { get; }
    ISmartTrimService SmartTrim { get; }
    IPowerManager PowerManager { get; }
    IWatchdogService Watchdog { get; }
    IRuleStore RuleStore { get; }

    bool IsRunning { get; }
    void Start();
    void Stop();
}
