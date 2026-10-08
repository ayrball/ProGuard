namespace ProGuard.Shared.Messages;

public class CommandMessage : IpcMessage
{
    public string CommandType { get; set; } = string.Empty;
    public int? TargetPid { get; set; }
    public string? Argument { get; set; }
    
    // Command Types: SetPriority, SetAffinity, TerminateProcess, SuspendProcess, 
    // ResumeProcess, EnableProBalance, DisableProBalance, EnablePerformanceMode, TrimWorkingSet
}
