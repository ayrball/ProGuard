using System;
using System.Collections.Generic;
using ProGuard.Shared.Contracts;

namespace ProGuard.Engine.Services;

/// <summary>
/// What users did through the command pipe (priority changes, terminations, rule edits, ...), for the UI's activity
/// feed: a fixed-size in-memory ring that hands entries out newest first. Written from any thread. The engine's own
/// actions live in the action log and the Watchdog event log; <see cref="ActivityFeed"/> merges all three.
/// </summary>
public sealed class ActivityLog
{
    public const int DefaultCapacity = 500;

    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly ActivityEntry[] _ring;
    private int _next;   // slot the next entry goes into
    private int _count;

    public ActivityLog(int capacity = DefaultCapacity, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _ring = new ActivityEntry[capacity];
        _time = time ?? TimeProvider.System;
    }

    public int Capacity => _ring.Length;

    public int Count
    {
        get { lock (_sync) return _count; }
    }

    public void Add(ActivityKind kind, string processName, int pid, string message)
    {
        var entry = new ActivityEntry
        {
            TimestampUtc = _time.GetUtcNow().UtcDateTime,
            Kind = kind,
            ProcessName = processName,
            Pid = pid,
            Message = message
        };

        lock (_sync)
        {
            _ring[_next] = entry;
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
    }

    /// <summary>The newest <paramref name="count"/> entries (fewer if the log holds fewer), newest first.</summary>
    public List<ActivityEntry> GetRecent(int count)
    {
        lock (_sync)
        {
            var take = Math.Clamp(count, 0, _count);
            var result = new List<ActivityEntry>(take);
            for (var i = 1; i <= take; i++)
                result.Add(_ring[(_next - i + _ring.Length) % _ring.Length]);
            return result;
        }
    }
}
