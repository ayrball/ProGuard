namespace ProGuard.Shared.Messages;

/// <summary>
/// A <see cref="MessageType.Command"/> sent to the engine. The engine replies with a
/// <see cref="MessageType.CommandResult"/> whose payload is a serialized <see cref="CommandResult"/>.
/// </summary>
public class CommandMessage : IpcMessage
{
    public CommandMessage()
    {
        Type = MessageType.Command;
    }

    /// <summary>One of the <see cref="CommandTypes"/> constants.</summary>
    public string CommandType { get; set; } = string.Empty;
    public int? TargetPid { get; set; }
    public string? Argument { get; set; }
}

/// <summary>Command names understood by the engine, with the <see cref="CommandMessage"/> fields each one uses.</summary>
public static class CommandTypes
{
    /// <summary>TargetPid; Argument = ProcessPriorityClass name ("BelowNormal") or its numeric value.</summary>
    public const string SetPriority = "SetPriority";
    /// <summary>TargetPid; Argument = affinity mask, decimal or 0x-prefixed hex.</summary>
    public const string SetAffinity = "SetAffinity";
    /// <summary>TargetPid; Argument = maximum CPU percent.</summary>
    public const string SetCpuLimit = "SetCpuLimit";
    /// <summary>TargetPid.</summary>
    public const string RemoveCpuLimit = "RemoveCpuLimit";
    /// <summary>TargetPid.</summary>
    public const string TerminateProcess = "TerminateProcess";
    /// <summary>TargetPid.</summary>
    public const string SuspendProcess = "SuspendProcess";
    /// <summary>TargetPid.</summary>
    public const string ResumeProcess = "ResumeProcess";
    /// <summary>TargetPid, or no pid to trim every process above the SmartTrim threshold.</summary>
    public const string TrimWorkingSet = "TrimWorkingSet";

    // The commands below change persisted settings, so the engine saves them as well as applying them.
    public const string EnableProBalance = "EnableProBalance";
    public const string DisableProBalance = "DisableProBalance";
    /// <summary>Argument = sensitivity level 1-10.</summary>
    public const string SetSensitivity = "SetSensitivity";
    /// <summary>Argument = serialized <see cref="IdleSaverArgument"/>.</summary>
    public const string ConfigureIdleSaver = "ConfigureIdleSaver";
    /// <summary>Argument = serialized <see cref="SmartTrimArgument"/>.</summary>
    public const string ConfigureSmartTrim = "ConfigureSmartTrim";

    /// <summary>
    /// TargetPid = process owning the foreground window, 0 if none. Sent by the UI on
    /// <see cref="PipeNames.Foreground"/> only: the engine runs as a service in session 0 and can't see the desktop.
    /// </summary>
    public const string ReportForeground = "ReportForeground";

    /// <summary>Argument = "true"/"false" (defaults to true). Not persisted: it is a runtime toggle.</summary>
    public const string EnablePerformanceMode = "EnablePerformanceMode";
    public const string DisablePerformanceMode = "DisablePerformanceMode";
}

public class IdleSaverArgument
{
    public bool Enabled { get; set; }
    public int TimeoutMinutes { get; set; } = 15;
    /// <summary>Power plan to switch to when idle; null keeps the current one.</summary>
    public string? PlanGuid { get; set; }
}

public class SmartTrimArgument
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 300;
    public long ThresholdMb { get; set; } = 1024;
}

/// <summary>Payload of a <see cref="MessageType.CommandResult"/> reply.</summary>
public class CommandResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    public static CommandResult Ok() => new() { Success = true };
    public static CommandResult Fail(string error) => new() { Success = false, Error = error };
}
