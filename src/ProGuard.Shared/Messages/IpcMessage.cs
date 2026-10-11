using System;

namespace ProGuard.Shared.Messages;

public enum MessageType
{
    Ping, Pong, GetProcesses, ProcessList, GetStatus, Status, 
    SetRule, RemoveRule, GetRules, RuleList, SetSetting, 
    GetLogs, LogList, SubscribeUpdates, UnsubscribeUpdates, 
    ProcessUpdate, ProBalanceEvent, Command,
    // Appended so the numeric values of the members above stay the same on the wire.
    CommandResult
}

public class IpcMessage
{
    public MessageType Type { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
