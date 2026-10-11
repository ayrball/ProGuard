namespace ProGuard.Shared.Contracts;

public class EngineStatus
{
    public bool IsRunning { get; set; }
    public bool ProBalanceEngaged { get; set; }
    public int ActiveInterventions { get; set; }
    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }
    public string Uptime { get; set; } = string.Empty;
    public int ActiveRuleCount { get; set; }
    public int ProcessCount { get; set; }

    /// <summary>The foreground process the engine is acting on (reported by the UI on the user's desktop), 0 if unknown.</summary>
    public int ForegroundPid { get; set; }
    /// <summary>Seconds since the last keyboard/mouse input on the desktops reporting to the engine, null if none report it.</summary>
    public double? UserIdleSeconds { get; set; }
    public bool PerformanceModeActive { get; set; }
}
