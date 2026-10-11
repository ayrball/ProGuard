using System;
using System.Collections.Generic;

namespace ProGuard.Shared.Contracts;

/// <summary>
/// Something the Watchdog or Keep Running did. The UI asks for these with a <c>GetLogs</c> request (payload
/// <see cref="WatchdogEventQuery"/>, reply <c>LogList</c> with a <see cref="WatchdogEventList"/>) and shows a tray
/// notification for each one with <see cref="Notify"/> set.
/// </summary>
public class WatchdogEventDto
{
    /// <summary>Increases by one per event for the life of the engine process.</summary>
    public long Sequence { get; set; }
    public DateTime TimestampUtc { get; set; }
    /// <summary>One of <see cref="WatchdogEventKinds"/>.</summary>
    public string Kind { get; set; } = WatchdogEventKinds.Fired;
    public int? RuleId { get; set; }
    public string? ProcessName { get; set; }
    public int? Pid { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    /// <summary>The rule asked for a notification (the Notify action, or Keep Running giving up).</summary>
    public bool Notify { get; set; }
}

public static class WatchdogEventKinds
{
    public const string Fired = "Fired";
    public const string Restored = "Restored";
    public const string Relaunched = "Relaunched";
    public const string Failed = "Failed";
}

public class WatchdogEventQuery
{
    /// <summary>
    /// Return events after this sequence number. Null returns none, only <see cref="WatchdogEventList.Latest"/>, so a
    /// newly started UI doesn't replay old notifications.
    /// </summary>
    public long? After { get; set; }
}

public class WatchdogEventList
{
    /// <summary>Sequence number of the newest event, 0 if there are none yet. Pass it back as the next <see cref="WatchdogEventQuery.After"/>.</summary>
    public long Latest { get; set; }
    public List<WatchdogEventDto> Events { get; set; } = new();
}
