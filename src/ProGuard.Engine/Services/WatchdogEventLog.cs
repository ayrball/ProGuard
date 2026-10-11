using System;
using System.Collections.Generic;
using System.Linq;
using ProGuard.Shared.Contracts;

namespace ProGuard.Engine.Services;

/// <summary>
/// The most recent Watchdog and Keep Running events, kept in memory for the UI to collect over IPC
/// (<see cref="IPC.IpcDispatcher"/> answers GetLogs from it). The engine can't show a notification itself: it runs in
/// session 0, so the UI on each desktop polls this and shows the ones marked <see cref="WatchdogEventDto.Notify"/>.
/// </summary>
public class WatchdogEventLog
{
    public const int Capacity = 200;

    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly Queue<WatchdogEventDto> _events = new();
    private long _sequence;

    public WatchdogEventLog(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public WatchdogEventDto Add(string kind, string title, string message, string? processName = null, int? pid = null, int? ruleId = null, bool notify = false)
    {
        lock (_sync)
        {
            var entry = new WatchdogEventDto
            {
                Sequence = ++_sequence,
                TimestampUtc = _time.GetUtcNow().UtcDateTime,
                Kind = kind,
                Title = title,
                Message = message,
                ProcessName = processName,
                Pid = pid,
                RuleId = ruleId,
                Notify = notify
            };
            _events.Enqueue(entry);
            while (_events.Count > Capacity) _events.Dequeue();
            return entry;
        }
    }

    /// <summary>
    /// Events newer than <paramref name="after"/>. A cursor ahead of the newest event comes from before an engine
    /// restart, so everything kept is new to that client.
    /// </summary>
    public WatchdogEventList GetSince(long? after)
    {
        lock (_sync)
        {
            var list = new WatchdogEventList { Latest = _sequence };
            if (after is not long cursor) return list;
            list.Events = cursor > _sequence
                ? _events.ToList()
                : _events.Where(e => e.Sequence > cursor).ToList();
            return list;
        }
    }
}
