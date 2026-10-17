using System.Globalization;
using System.Text;
using ProGuard.Shared.Contracts;
using ActivityKind = ProGuard.Shared.Contracts.ActivityKind;

namespace ProGuard.Preview;

/// <summary>
/// The stand-in engine's answers to GetLogEntries and ExportLogs: a canned engine log plus the stand-in's own action
/// entries (the activity feed's ProBalance, rule and power entries), filtered the way the engine filters. Cursors here are
/// just an entry's time in ticks (the canned entries are seconds apart); the UI treats them as opaque either way. Unless read-only, every few tail requests add a line,
/// so the live tail can be watched against <c>--serve</c>.
/// </summary>
internal sealed class StandInLogs
{
    private readonly List<LogEntryDto> _engine;
    private readonly bool _readOnly;
    private int _tailRequests;
    private int _liveLines;

    public StandInLogs(DateTime nowUtc, bool readOnly)
    {
        _readOnly = readOnly;
        _engine = StandInData.EngineLog(nowUtc).OrderBy(e => e.TimestampUtc).ToList();
    }

    public LogPage Query(LogQuery query, IReadOnlyList<ActivityEntry> activity, bool admin)
    {
        if (query.Before != null && query.After != null) throw new ArgumentException("A log query takes Before or After, not both");
        int limit = Math.Clamp(query.Limit, 1, LogQuery.MaxLimit);

        if (query.After != null)
        {
            long after = ParseCursor(query.After);
            if (!_readOnly && admin && ++_tailRequests % 3 == 0) AddLiveLine();
            var fresh = All(query, activity, admin).Where(e => e.TimestampUtc.Ticks > after).ToList();
            return new LogPage
            {
                Entries = fresh.Take(limit).ToList(),
                Truncated = fresh.Count > limit,
                HeadCursor = Head(activity),
                EngineLogIncluded = admin,
            };
        }

        long before = query.Before == null ? long.MaxValue : ParseCursor(query.Before);
        var older = All(query, activity, admin).Where(e => e.TimestampUtc.Ticks < before).ToList();
        var page = older.Take(limit).ToList();
        return new LogPage
        {
            Entries = page,
            OlderCursor = older.Count > limit ? page[^1].TimestampUtc.Ticks.ToString(CultureInfo.InvariantCulture) : null,
            HeadCursor = Head(activity),
            EngineLogIncluded = admin,
        };
    }

    public LogExportDto Export(LogQuery query, IReadOnlyList<ActivityEntry> activity, bool admin)
    {
        var entries = All(query, activity, admin).ToList();
        var csv = new StringBuilder("TimestampUtc,LocalTime,Source,Level,Category,Process,PID,Message,Detail\r\n");
        foreach (var e in entries)
        {
            csv.AppendJoin(',', new[]
            {
                e.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                e.Source.ToString(), e.Level.ToString(), e.Category, e.ProcessName ?? "", e.Pid?.ToString(CultureInfo.InvariantCulture) ?? "",
                e.Message, e.Detail ?? "",
            }.Select(Quote)).Append("\r\n");
        }
        return new LogExportDto
        {
            FileName = $"proguard-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Csv = csv.ToString(),
            Count = entries.Count,
            EngineLogIncluded = admin,
        };
    }

    private static string Quote(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    private string Head(IReadOnlyList<ActivityEntry> activity) =>
        Math.Max(_engine.Count > 0 ? _engine[^1].TimestampUtc.Ticks : 0, activity.Count > 0 ? activity.Max(a => a.TimestampUtc.Ticks) : 0).ToString(CultureInfo.InvariantCulture);

    private static long ParseCursor(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) ? seq : throw new ArgumentException("The log cursor is malformed");

    /// <summary>Every entry the caller may read that matches, newest first.</summary>
    private IEnumerable<LogEntryDto> All(LogQuery query, IReadOnlyList<ActivityEntry> activity, bool admin)
    {
        bool all = query.Sources is not { Count: > 0 };
        var entries = new List<LogEntryDto>();
        if (admin && (all || query.Sources!.Contains(LogSource.Engine))) entries.AddRange(_engine);
        if (all || query.Sources!.Contains(LogSource.Actions))
            entries.AddRange(activity.Where(a => a.Kind is ActivityKind.Restrained or ActivityKind.Restored or ActivityKind.Rule or ActivityKind.Power).Select(FromActivity));
        return entries.Where(e => Matches(e, query)).OrderByDescending(e => e.TimestampUtc);
    }

    private static LogEntryDto FromActivity(ActivityEntry a) => new()
    {
        Id = "a:" + a.TimestampUtc.Ticks.ToString(CultureInfo.InvariantCulture),
        TimestampUtc = a.TimestampUtc,
        Source = LogSource.Actions,
        Level = LogLevelDto.Information,
        Category = a.Kind switch { ActivityKind.Power => "Power", ActivityKind.Rule => "CpuLimiter", _ => "ProBalance" },
        ProcessName = string.IsNullOrEmpty(a.ProcessName) ? null : a.ProcessName + ".exe",
        Pid = a.Pid > 0 ? a.Pid : null,
        Message = a.Message,
        Detail = a.Kind == ActivityKind.Restrained ? "Priority: Normal -> BelowNormal" : null,
    };

    private static bool Matches(LogEntryDto e, LogQuery q)
    {
        static bool Has(string? text, string? part) => text != null && part != null && text.Contains(part.Trim(), StringComparison.OrdinalIgnoreCase);

        if (q.Levels is { Count: > 0 } && !q.Levels.Contains(e.Level)) return false;
        if (q.FromUtc is DateTime from && e.TimestampUtc < from.ToUniversalTime()) return false;
        if (q.ToUtc is DateTime to && e.TimestampUtc > to.ToUniversalTime()) return false;
        if (!string.IsNullOrWhiteSpace(q.Category) && !Has(e.Category, q.Category)) return false;
        if (!string.IsNullOrWhiteSpace(q.Process))
        {
            if (int.TryParse(q.Process.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? e.Pid != pid : !Has(e.ProcessName, q.Process)) return false;
        }
        if (!string.IsNullOrWhiteSpace(q.Text) && !Has(e.Message, q.Text) && !Has(e.Category, q.Text) && !Has(e.ProcessName, q.Text) && !Has(e.Detail, q.Text))
            return false;
        return true;
    }

    private static readonly string[] LiveLines =
    {
        "IPC command SetPriority (pid 7220, arg BelowNormal) from " + StandInData.Me,
        "ProBalance restrained chrome (7220) from Normal to BelowNormal at 6.1% CPU",
        "SmartTrim trimmed 4 processes, 212 MB reclaimed",
        "ProBalance restored chrome (7220) to Normal: CPU usage dropped to 0.4%",
    };

    private void AddLiveLine()
    {
        var message = LiveLines[_liveLines++ % LiveLines.Length];
        var category = message.StartsWith("IPC", StringComparison.Ordinal) ? "IpcDispatcher" : message.StartsWith("Smart", StringComparison.Ordinal) ? "SmartTrimService" : "ProBalanceEngine";
        var now = DateTime.UtcNow;
        _engine.Add(new LogEntryDto
        {
            Id = "f:" + now.Ticks.ToString(CultureInfo.InvariantCulture), TimestampUtc = now, Source = LogSource.Engine, Level = LogLevelDto.Information,
            Category = category, ProcessName = message.Contains("chrome") ? "chrome" : null, Pid = message.Contains("7220") ? 7220 : null, Message = message,
        });
    }
}
