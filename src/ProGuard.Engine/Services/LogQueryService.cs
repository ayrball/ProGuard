using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Logging;
using ProGuard.Shared.Contracts;
using ActivityKind = ProGuard.Shared.Contracts.ActivityKind; // System.Diagnostics has one too

namespace ProGuard.Engine.Services;

/// <summary>
/// The log viewer's queries: the engine's text log (<see cref="EngineLogReader"/>) and the database action log, filtered
/// and merged newest first, a page at a time.
/// <para>
/// Paging is a merge of two sources that each have their own order: the engine log's file order and the action log's
/// row ids. A cursor holds a position in each (<see cref="LogCursor"/>), so paging never repeats or skips an entry even
/// though the two are interleaved by time. Neither position is a file offset, so cursors survive the engine log rolling
/// over.
/// </para>
/// </summary>
public sealed class LogQueryService
{
    /// <summary>An export holds at most this many entries, the newest.</summary>
    public const int MaxExportEntries = 100_000;

    private const int ActionBatch = 500;
    /// <summary>Action-log rows one page may look at before it returns what it has (with a cursor to carry on from).</summary>
    internal const int MaxActionScan = 50_000;

    private readonly EngineLogReader _engineLog;
    private readonly IRuleStore _store;
    private readonly TimeProvider _time;

    public LogQueryService(EngineLogReader engineLog, IRuleStore store, TimeProvider? time = null)
    {
        _engineLog = engineLog;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// One page. <paramref name="includeEngineLog"/> is false for callers who may not read the engine log: the page is
    /// then the action log alone, whatever the query asked for.
    /// </summary>
    public LogPage Query(LogQuery query, bool includeEngineLog)
    {
        if (query.Before != null && query.After != null) throw new ArgumentException("A log query takes Before or After, not both");
        var limit = Math.Clamp(query.Limit, 1, LogQuery.MaxLimit);
        var (useEngine, useActions) = Sources(query, includeEngineLog);

        var lines = useEngine ? _engineLog.Read() : Array.Empty<EngineLogLine>();
        var newestAction = useActions ? _store.GetActions(null, null, 1) : Array.Empty<ProBalanceAction>();
        var head = new LogCursor(
            lines.Length > 0 ? SourcePosition.At(lines[^1].Key) : SourcePosition.Unbounded,
            newestAction.Count > 0 ? SourcePosition.At(newestAction[0].Id) : SourcePosition.Unbounded);

        if (query.After != null)
        {
            var after = LogCursor.Parse(query.After);
            // Ids restart once the action log is emptied: anything still below the cursor means it was cleared.
            bool cleared = useActions && after.Actions.Kind == PositionKind.At && newestAction.Count > 0 && newestAction[0].Id < after.Actions.ActionId;
            if (!cleared)
                return Tail(query, after, lines, useEngine, useActions, limit, head, includeEngineLog);

            var fresh = Older(query, new LogCursor(SourcePosition.Unbounded, SourcePosition.Unbounded), lines, useEngine, useActions, limit, MaxActionScan);
            fresh.HeadCursor = head.ToString();
            fresh.Reset = true;
            fresh.EngineLogIncluded = includeEngineLog;
            return fresh;
        }

        var start = query.Before == null ? new LogCursor(SourcePosition.Unbounded, SourcePosition.Unbounded) : LogCursor.Parse(query.Before);
        var page = Older(query, start, lines, useEngine, useActions, limit, MaxActionScan);
        page.HeadCursor = head.ToString();
        page.EngineLogIncluded = includeEngineLog;
        return page;
    }

    /// <summary>Every entry that matches <paramref name="query"/> (up to <see cref="MaxExportEntries"/>, the newest) as CSV.</summary>
    public LogExportDto Export(LogQuery query, bool includeEngineLog)
    {
        var (useEngine, useActions) = Sources(query, includeEngineLog);
        var lines = useEngine ? _engineLog.Read() : Array.Empty<EngineLogLine>();
        var page = Older(query, new LogCursor(SourcePosition.Unbounded, SourcePosition.Unbounded), lines, useEngine, useActions,
            MaxExportEntries, maxActionScan: int.MaxValue);

        var now = _time.GetLocalNow();
        return new LogExportDto
        {
            FileName = $"proguard-log-{now:yyyyMMdd-HHmmss}.csv",
            Csv = ToCsv(page.Entries),
            Count = page.Entries.Count,
            Truncated = page.OlderCursor != null,
            EngineLogIncluded = includeEngineLog,
        };
    }

    private static (bool Engine, bool Actions) Sources(LogQuery query, bool includeEngineLog)
    {
        bool all = query.Sources == null || query.Sources.Count == 0;
        return (includeEngineLog && (all || query.Sources!.Contains(LogSource.Engine)), all || query.Sources!.Contains(LogSource.Actions));
    }

    // ------------------------------------------------------------------ paging older

    private LogPage Older(LogQuery query, LogCursor start, EngineLogLine[] lines, bool useEngine, bool useActions, int limit, int maxActionScan)
    {
        // Each source contributes up to `limit` matches older than its position, newest first, and says how far it looked.
        var engine = useEngine ? OlderEngine(query, start.Engine, lines, limit) : SourceResult.Empty;
        var actions = useActions ? OlderActions(query, start.Actions, limit, maxActionScan) : SourceResult.Empty;

        var taken = Merge(engine, actions, limit);
        var next = new LogCursor(
            useEngine ? NextPosition(engine, start.Engine, taken, isEngine: true) : SourcePosition.End,
            useActions ? NextPosition(actions, start.Actions, taken, isEngine: false) : SourcePosition.End);

        return new LogPage
        {
            Entries = taken.Select(t => t.Entry).ToList(),
            OlderCursor = next.Engine.Kind == PositionKind.End && next.Actions.Kind == PositionKind.End ? null : next.ToString(),
        };
    }

    private static SourceResult OlderEngine(LogQuery query, SourcePosition from, EngineLogLine[] lines, int limit)
    {
        if (from.Kind == PositionKind.End) return SourceResult.Empty;
        int index = from.Kind == PositionKind.Unbounded ? lines.Length : IndexOf(lines, from.EngineKey, after: false);

        var matches = new List<(LogEntryDto, SourcePosition)>();
        for (int i = index - 1; i >= 0; i--)
        {
            var entry = ToDto(lines[i]);
            if (!Matches(entry, query)) continue;
            matches.Add((entry, SourcePosition.At(lines[i].Key)));
            if (matches.Count == limit) return new SourceResult(matches, matches[^1].Item2);
        }
        return new SourceResult(matches, SourcePosition.End);
    }

    private SourceResult OlderActions(LogQuery query, SourcePosition from, int limit, int maxScan)
    {
        if (from.Kind == PositionKind.End) return SourceResult.Empty;
        int? before = from.Kind == PositionKind.At ? from.ActionId : null;

        var matches = new List<(LogEntryDto, SourcePosition)>();
        int scanned = 0;
        while (true)
        {
            var batch = _store.GetActions(before, null, ActionBatch) ?? Array.Empty<ProBalanceAction>();
            foreach (var action in batch)
            {
                before = action.Id;
                scanned++;
                var entry = ToDto(action);
                if (!Matches(entry, query)) continue;
                matches.Add((entry, SourcePosition.At(action.Id)));
                if (matches.Count == limit) return new SourceResult(matches, matches[^1].Item2);
            }
            if (batch.Count < ActionBatch) return new SourceResult(matches, SourcePosition.End);
            // Looked far enough for one page: carry on from here next time.
            if (scanned >= maxScan) return new SourceResult(matches, SourcePosition.At(before!.Value));
        }
    }

    /// <summary>Both sources' matches, newest first; the engine log's first where the times are equal.</summary>
    private static List<(LogEntryDto Entry, SourcePosition Position, bool IsEngine)> Merge(SourceResult engine, SourceResult actions, int limit)
    {
        var merged = new List<(LogEntryDto, SourcePosition, bool)>(Math.Min(limit, engine.Matches.Count + actions.Matches.Count));
        int e = 0, a = 0;
        while (merged.Count < limit && (e < engine.Matches.Count || a < actions.Matches.Count))
        {
            bool takeEngine = a >= actions.Matches.Count
                || (e < engine.Matches.Count && engine.Matches[e].Entry.TimestampUtc >= actions.Matches[a].Entry.TimestampUtc);
            if (takeEngine) { merged.Add((engine.Matches[e].Entry, engine.Matches[e].Position, true)); e++; }
            else { merged.Add((actions.Matches[a].Entry, actions.Matches[a].Position, false)); a++; }
        }
        return merged;
    }

    /// <summary>
    /// Where a source carries on from: just past its last entry on the page if some of its matches didn't fit, otherwise as
    /// far as it looked (which is the end if it ran out).
    /// </summary>
    private static SourcePosition NextPosition(SourceResult result, SourcePosition start, List<(LogEntryDto Entry, SourcePosition Position, bool IsEngine)> taken, bool isEngine)
    {
        // The merge takes each source's matches in order, so what it used is a prefix of them.
        int used = taken.Count(t => t.IsEngine == isEngine);
        if (used < result.Matches.Count) return used == 0 ? start : result.Matches[used - 1].Position;
        return result.ScannedTo;
    }

    // ------------------------------------------------------------------ live tail

    private LogPage Tail(LogQuery query, LogCursor after, EngineLogLine[] lines, bool useEngine, bool useActions, int limit, LogCursor head, bool includeEngineLog)
    {
        var entries = new List<LogEntryDto>();
        bool truncated = false;

        if (useEngine)
        {
            int from = after.Engine.Kind == PositionKind.At ? IndexOf(lines, after.Engine.EngineKey, after: true) : 0;
            for (int i = lines.Length - 1; i >= from; i--)
            {
                var entry = ToDto(lines[i]);
                if (!Matches(entry, query)) continue;
                if (entries.Count == limit) { truncated = true; break; }
                entries.Add(entry);
            }
        }

        if (useActions)
        {
            int? afterId = after.Actions.Kind == PositionKind.At ? after.Actions.ActionId : null;
            int? before = null;
            int found = 0;
            while (true)
            {
                var batch = _store.GetActions(before, afterId, ActionBatch) ?? Array.Empty<ProBalanceAction>();
                foreach (var action in batch)
                {
                    before = action.Id;
                    var entry = ToDto(action);
                    if (!Matches(entry, query)) continue;
                    if (found == limit) { truncated = true; break; }
                    entries.Add(entry);
                    found++;
                }
                if (truncated || batch.Count < ActionBatch) break;
            }
        }

        var ordered = entries.OrderByDescending(e => e.TimestampUtc).ToList();
        if (ordered.Count > limit)
        {
            ordered.RemoveRange(limit, ordered.Count - limit);
            truncated = true;
        }

        return new LogPage
        {
            Entries = ordered,
            HeadCursor = head.ToString(),
            Truncated = truncated,
            EngineLogIncluded = includeEngineLog,
        };
    }

    /// <summary>
    /// The index of the entry at <paramref name="key"/> (plus one with <paramref name="after"/>). An entry that is gone (the
    /// log rolled over twice since) is placed by its time instead.
    /// </summary>
    private static int IndexOf(EngineLogLine[] lines, EngineLogKey key, bool after)
    {
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Key == key) return after ? i + 1 : i;
            if (lines[i].Key.Ticks < key.Ticks) return i + 1;
        }
        return 0;
    }

    // ------------------------------------------------------------------ entries

    internal static bool Matches(LogEntryDto entry, LogQuery query)
    {
        if (query.Levels is { Count: > 0 } levels && !levels.Contains(entry.Level)) return false;
        if (query.FromUtc is DateTime from && entry.TimestampUtc < AsUtc(from)) return false;
        if (query.ToUtc is DateTime to && entry.TimestampUtc > AsUtc(to)) return false;
        if (!string.IsNullOrWhiteSpace(query.Category) && !Contains(entry.Category, query.Category)) return false;

        if (!string.IsNullOrWhiteSpace(query.Process))
        {
            var process = query.Process.Trim();
            bool isPid = int.TryParse(process, NumberStyles.None, CultureInfo.InvariantCulture, out var pid);
            if (isPid ? entry.Pid != pid : !Contains(entry.ProcessName, process)) return false;
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var text = query.Text.Trim();
            if (!Contains(entry.Message, text) && !Contains(entry.Category, text) && !Contains(entry.ProcessName, text) && !Contains(entry.Detail, text))
                return false;
        }
        return true;
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack != null && haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DateTime AsUtc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Utc => time,
        DateTimeKind.Local => time.ToUniversalTime(),
        _ => DateTime.SpecifyKind(time, DateTimeKind.Utc),
    };

    internal static LogEntryDto ToDto(EngineLogLine line) => new()
    {
        Id = "f:" + line.Key,
        TimestampUtc = line.TimestampUtc,
        Source = LogSource.Engine,
        Level = line.Level,
        Category = line.Category,
        ProcessName = line.ProcessName,
        Pid = line.Pid,
        Message = line.Message,
        Detail = line.Detail,
    };

    /// <summary>An action-log row as the activity feed words it, under the feature that logged it.</summary>
    internal static LogEntryDto ToDto(ProBalanceAction action)
    {
        var activity = ActivityFeed.FromAction(action);
        var category = activity.Kind switch
        {
            ActivityKind.Power => "Power",
            ActivityKind.Rule => "CpuLimiter",
            _ => "ProBalance",
        };
        string? detail = null;
        if (activity.Kind is ActivityKind.Restrained or ActivityKind.Restored && action.OriginalPriority != action.NewPriority)
            detail = $"Priority: {PriorityName(action.OriginalPriority)} -> {PriorityName(action.NewPriority)}";

        return new LogEntryDto
        {
            Id = "a:" + action.Id.ToString(CultureInfo.InvariantCulture),
            TimestampUtc = activity.TimestampUtc,
            Source = LogSource.Actions,
            Level = LogLevelDto.Information,
            Category = category,
            ProcessName = string.IsNullOrEmpty(activity.ProcessName) ? null : activity.ProcessName,
            Pid = activity.Pid > 0 ? activity.Pid : null,
            Message = activity.Message,
            Detail = detail,
        };
    }

    private static string PriorityName(int value) =>
        Enum.IsDefined(typeof(ProcessPriorityClass), value) ? ((ProcessPriorityClass)value).ToString() : value.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ CSV

    public static readonly string[] CsvColumns = { "TimestampUtc", "LocalTime", "Source", "Level", "Category", "Process", "PID", "Message", "Detail" };

    internal static string ToCsv(IEnumerable<LogEntryDto> entries)
    {
        var csv = new StringBuilder();
        csv.AppendJoin(',', CsvColumns).Append("\r\n");
        foreach (var e in entries)
        {
            Field(csv, e.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append(',');
            Field(csv, e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',');
            Field(csv, e.Source.ToString()).Append(',');
            Field(csv, e.Level.ToString()).Append(',');
            Field(csv, e.Category).Append(',');
            Field(csv, e.ProcessName).Append(',');
            Field(csv, e.Pid?.ToString(CultureInfo.InvariantCulture)).Append(',');
            Field(csv, e.Message).Append(',');
            Field(csv, e.Detail).Append("\r\n");
        }
        return csv.ToString();
    }

    /// <summary>
    /// One RFC 4180 field. Process names and messages come from other users' programs, so a field a spreadsheet would read
    /// as a formula ("=", "+", "-", "@") gets a leading apostrophe.
    /// </summary>
    private static StringBuilder Field(StringBuilder csv, string? value)
    {
        if (string.IsNullOrEmpty(value)) return csv;
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        if (value.AsSpan().IndexOfAny(",\"\r\n") < 0) return csv.Append(value);
        return csv.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
    }

    // ------------------------------------------------------------------ cursors

    private sealed record SourceResult(List<(LogEntryDto Entry, SourcePosition Position)> Matches, SourcePosition ScannedTo)
    {
        /// <summary>A source not read, or one with nothing left before the position.</summary>
        public static SourceResult Empty { get; } = new(new List<(LogEntryDto, SourcePosition)>(), SourcePosition.End);
    }
}

internal enum PositionKind { Unbounded, At, End }

/// <summary>
/// A position in one source. In a "before" cursor, <see cref="PositionKind.Unbounded"/> starts at the newest entry and
/// <see cref="PositionKind.End"/> means nothing is older; in a head cursor, Unbounded means the source was empty.
/// </summary>
internal readonly record struct SourcePosition(PositionKind Kind, EngineLogKey EngineKey, int ActionId)
{
    public static SourcePosition Unbounded => new(PositionKind.Unbounded, default, 0);
    public static SourcePosition End => new(PositionKind.End, default, 0);
    public static SourcePosition At(EngineLogKey key) => new(PositionKind.At, key, 0);
    public static SourcePosition At(int actionId) => new(PositionKind.At, default, actionId);
}

/// <summary>A position in both sources, sent to the client as an opaque string: "1;f=&lt;engine&gt;;a=&lt;action&gt;".</summary>
internal readonly record struct LogCursor(SourcePosition Engine, SourcePosition Actions)
{
    public override string ToString() =>
        $"1;f={Format(Engine, k => k.EngineKey.ToString())};a={Format(Actions, k => k.ActionId.ToString(CultureInfo.InvariantCulture))}";

    private static string Format(SourcePosition position, Func<SourcePosition, string> at) => position.Kind switch
    {
        PositionKind.Unbounded => "*",
        PositionKind.End => "end",
        _ => at(position),
    };

    public static LogCursor Parse(string text)
    {
        var parts = text.Split(';');
        if (parts.Length == 3 && parts[0] == "1" && parts[1].StartsWith("f=", StringComparison.Ordinal) && parts[2].StartsWith("a=", StringComparison.Ordinal))
        {
            var engine = ParsePosition(parts[1][2..], s => EngineLogKey.TryParse(s, out var key) ? SourcePosition.At(key) : null);
            var actions = ParsePosition(parts[2][2..], s =>
                int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? SourcePosition.At(id) : null);
            if (engine is { } e && actions is { } a) return new LogCursor(e, a);
        }
        throw new ArgumentException("The log cursor is malformed");
    }

    private static SourcePosition? ParsePosition(string text, Func<string, SourcePosition?> at) => text switch
    {
        "*" => SourcePosition.Unbounded,
        "end" => SourcePosition.End,
        _ => at(text),
    };
}
