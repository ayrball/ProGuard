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
    /// <summary>
    /// TargetPid, or no pid to trim every process above the SmartTrim threshold. Data = bytes reclaimed (a number),
    /// when known.
    /// </summary>
    public const string TrimWorkingSet = "TrimWorkingSet";
    /// <summary>
    /// SmartTrim's "Purge now": trims eligible working sets, purges the standby list and, if enabled, the file cache.
    /// Administrators only. Data = <see cref="Contracts.MemoryPurgeResultDto"/>.
    /// </summary>
    public const string PurgeMemory = "PurgeMemory";

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
    /// Argument = milliseconds since the last keyboard/mouse input on that desktop (for IdleSaver), or null if unknown.
    /// </summary>
    public const string ReportForeground = "ReportForeground";

    /// <summary>Argument = "true"/"false" (defaults to true). Not persisted: it is a runtime toggle.</summary>
    public const string EnablePerformanceMode = "EnablePerformanceMode";
    public const string DisablePerformanceMode = "DisablePerformanceMode";

    // --- Thin-client commands --------------------------------------------------------------------------------------
    // The UI reads and changes everything through these; it never opens the database or calls Windows itself.
    // "Argument" is JSON unless stated; the reply's CommandResult.Data is JSON of the stated type.

    /// <summary>No args. Data = <see cref="Contracts.EngineSnapshot"/>, with file paths limited to what the caller may see.</summary>
    public const string GetSnapshot = "GetSnapshot";

    /// <summary>TargetPid; Argument = "true" (EcoQoS on) or "false" (explicitly off).</summary>
    public const string SetEfficiencyMode = "SetEfficiencyMode";
    /// <summary>TargetPid; Argument = IO_PRIORITY_HINT 0-3 (Very Low, Low, Normal, High).</summary>
    public const string SetIoPriority = "SetIoPriority";
    /// <summary>TargetPid; Argument = MEMORY_PRIORITY 1-5 (Very Low .. Normal).</summary>
    public const string SetMemoryPriority = "SetMemoryPriority";
    /// <summary>TargetPid; ends the process and every descendant the caller may end. Data = number of processes ended.</summary>
    public const string TerminateProcessTree = "TerminateProcessTree";

    /// <summary>No args. Data = List&lt;<see cref="Contracts.ProcessRuleDto"/>&gt;.</summary>
    public const string GetProcessRules = "GetProcessRules";
    /// <summary>Argument = <see cref="Contracts.ProcessRuleDto"/> (Id 0 = new). Data = the saved rule. Applied to running instances.</summary>
    public const string SaveProcessRule = "SaveProcessRule";
    /// <summary>Argument = rule Id.</summary>
    public const string DeleteProcessRule = "DeleteProcessRule";

    /// <summary>No args. Data = List&lt;<see cref="Contracts.WatchdogRuleDto"/>&gt;.</summary>
    public const string GetWatchdogRules = "GetWatchdogRules";
    /// <summary>Argument = <see cref="Contracts.WatchdogRuleDto"/> (Id 0 = new). Data = the saved rule.</summary>
    public const string SaveWatchdogRule = "SaveWatchdogRule";
    /// <summary>Argument = rule Id.</summary>
    public const string DeleteWatchdogRule = "DeleteWatchdogRule";

    /// <summary>No args. Data = <see cref="Contracts.EngineSettingsDto"/>.</summary>
    public const string GetSettings = "GetSettings";
    /// <summary>Argument = <see cref="Contracts.EngineSettingsDto"/>. Saved and applied; Data = the settings as stored.</summary>
    public const string SaveSettings = "SaveSettings";

    /// <summary>No args. Data = <see cref="Contracts.PowerStateDto"/>.</summary>
    public const string GetPower = "GetPower";
    /// <summary>Argument = power plan GUID (plain text).</summary>
    public const string SetPowerPlan = "SetPowerPlan";

    /// <summary>Argument = maximum number of entries (plain integer, default 200). Data = List&lt;<see cref="Contracts.ActivityEntry"/>&gt;, newest first.</summary>
    public const string GetActivity = "GetActivity";

    /// <summary>No args. Data = List&lt;<see cref="Contracts.CpuSetDto"/>&gt;.</summary>
    public const string GetCpuSets = "GetCpuSets";
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
    /// <summary>Command-specific result data (JSON), for the commands that return any.</summary>
    public string? Data { get; set; }

    public static CommandResult Ok() => new() { Success = true };
    public static CommandResult Ok(string? data) => new() { Success = true, Data = data };
    public static CommandResult Fail(string error) => new() { Success = false, Error = error };
}
