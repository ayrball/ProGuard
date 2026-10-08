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
}
