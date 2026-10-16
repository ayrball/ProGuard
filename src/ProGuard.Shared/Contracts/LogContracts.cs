using System;
using System.Collections.Generic;

namespace ProGuard.Shared.Contracts;

/// <summary>Where a log entry comes from.</summary>
public enum LogSource
{
    /// <summary>The engine's text log (%ProgramData%\ProGuard\logs\engine.log and the file it last rolled over into). Administrators only.</summary>
    Engine,
    /// <summary>The action log in the database: ProBalance restraints and restores, CPU Limiter actions and power plan changes.</summary>
    Actions,
}

/// <summary>Severity, in the engine log's order. Action-log entries are all <see cref="Information"/>.</summary>
public enum LogLevelDto
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>
/// Argument of <c>GetLogEntries</c> and <c>ExportLogs</c>. Every filter is optional; an empty query is the newest page of
/// everything the caller may read.
/// </summary>
public class LogQuery
{
    /// <summary>The sources to read; null or empty reads every source the caller may.</summary>
    public List<LogSource>? Sources { get; set; }
    /// <summary>The levels to include; null or empty includes every level.</summary>
    public List<LogLevelDto>? Levels { get; set; }
    /// <summary>Inclusive bounds, UTC.</summary>
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    /// <summary>Category, case-insensitive substring: the engine log's logger name ("ProBalanceEngine") or the action log's kind ("ProBalance", "Power", "CpuLimiter").</summary>
    public string? Category { get; set; }
    /// <summary>A process id (all digits) or part of a process name, case-insensitive.</summary>
    public string? Process { get; set; }
    /// <summary>Case-insensitive substring of the message, category, process or detail.</summary>
    public string? Text { get; set; }

    /// <summary>A <see cref="LogPage.OlderCursor"/>: returns the page after it, older entries. Not with <see cref="After"/>.</summary>
    public string? Before { get; set; }
    /// <summary>A <see cref="LogPage.HeadCursor"/>: returns only entries written since, for a live tail.</summary>
    public string? After { get; set; }
    /// <summary>Page size, 1 to <see cref="MaxLimit"/>. Ignored by ExportLogs.</summary>
    public int Limit { get; set; } = DefaultLimit;

    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;
}

public class LogEntryDto
{
    /// <summary>Stable for as long as the entry exists: "f:..." for the engine log, "a:&lt;row id&gt;" for the action log.</summary>
    public string Id { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public LogSource Source { get; set; }
    public LogLevelDto Level { get; set; }
    public string Category { get; set; } = string.Empty;
    /// <summary>The process the entry is about, when it names one. For the engine log this is read out of the message, so it is a best guess.</summary>
    public string? ProcessName { get; set; }
    public int? Pid { get; set; }
    public string Message { get; set; } = string.Empty;
    /// <summary>Anything after the first line: an exception and its stack trace, or the action's priorities.</summary>
    public string? Detail { get; set; }
}

/// <summary>Data of <c>GetLogEntries</c>: one page, newest first.</summary>
public class LogPage
{
    public List<LogEntryDto> Entries { get; set; } = new();
    /// <summary>Pass as <see cref="LogQuery.Before"/> for the next, older page; null when there is nothing older.</summary>
    public string? OlderCursor { get; set; }
    /// <summary>
    /// Where every source ended when this page was read, whatever the filters matched. Pass as <see cref="LogQuery.After"/>
    /// to get what is written next.
    /// </summary>
    public string HeadCursor { get; set; } = string.Empty;
    /// <summary>For an <see cref="LogQuery.After"/> query: more new entries matched than fit, so the oldest of them were left out.</summary>
    public bool Truncated { get; set; }
    /// <summary>
    /// For an <see cref="LogQuery.After"/> query: the cursor no longer fits the log (the action log was cleared, or the engine
    /// log rolled over twice), so this page starts again from the newest entries. The client should start afresh.
    /// </summary>
    public bool Reset { get; set; }
    /// <summary>False when the caller may not read the engine log, so only the action log was searched.</summary>
    public bool EngineLogIncluded { get; set; }
}

/// <summary>Data of <c>ExportLogs</c>: the filtered entries, newest first, as CSV for the client to save.</summary>
public class LogExportDto
{
    /// <summary>A suggested file name, e.g. "proguard-log-20261016-142233.csv".</summary>
    public string FileName { get; set; } = string.Empty;
    /// <summary>RFC 4180 CSV with a header row; lines end in CRLF.</summary>
    public string Csv { get; set; } = string.Empty;
    public int Count { get; set; }
    /// <summary>More entries matched than an export holds (<c>MaxExportEntries</c>); the newest were kept.</summary>
    public bool Truncated { get; set; }
    public bool EngineLogIncluded { get; set; }
}
