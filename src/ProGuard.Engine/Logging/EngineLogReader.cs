using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ProGuard.Shared.Contracts;

namespace ProGuard.Engine.Logging;

/// <summary>
/// One entry of the engine's text log: a "yyyy-MM-dd HH:mm:ss.fff LVL Category: message" line plus any lines after it
/// that don't start with a timestamp (an exception's stack trace).
/// </summary>
/// <param name="Key">Identifies the entry for as long as it is in either file; see <see cref="EngineLogKey"/>.</param>
public sealed record EngineLogLine(
    EngineLogKey Key,
    DateTime TimestampUtc,
    LogLevelDto Level,
    string Category,
    string Message,
    string? Detail,
    string? ProcessName,
    int? Pid);

/// <summary>
/// Where an entry sits in the engine log, independent of which file it is in or at what offset, so a cursor survives the
/// log rolling over (<c>engine.log</c> renamed to <c>engine.1.log</c>). <see cref="Ticks"/> is the local time as written,
/// <see cref="Hash"/> a hash of the line, and <see cref="Ordinal"/> counts earlier lines in the same file with the same
/// time and hash, which only identical lines written in the same millisecond share.
/// </summary>
public readonly record struct EngineLogKey(long Ticks, uint Hash, int Ordinal)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Ticks}.{Hash:x8}.{Ordinal}");

    public static bool TryParse(string text, out EngineLogKey key)
    {
        key = default;
        var parts = text.Split('.');
        if (parts.Length != 3
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
            return false;
        key = new EngineLogKey(ticks, hash, ordinal);
        return true;
    }
}

/// <summary>
/// Reads the log <see cref="FileLoggerProvider"/> writes: the previous file (<c>engine.1.log</c>) then the current one,
/// oldest entry first. Parsed entries are cached: the current file is append-only, so a later read parses only what was
/// added since, and either file is parsed afresh once it is replaced. The cache is dropped after
/// <see cref="IdleRelease"/> without a read, since it holds up to two full log files in memory and is only wanted while
/// someone is looking at the log.
/// </summary>
public sealed class EngineLogReader : IDisposable
{
    public static readonly TimeSpan IdleRelease = TimeSpan.FromMinutes(5);

    // "2026-10-16 14:03:22.123 INF ProBalanceEngine: message"
    private static readonly Regex LinePattern = new(
        @"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (?<level>TRC|DBG|INF|WRN|ERR|CRT|\?\?\?) (?<category>\S*?): (?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Best guesses at the process a line is about, from the shapes the engine's messages use: "Discord (2144)",
    // "pid 2144" / "PID 2144" / "process 2144", and a bare "name.exe".
    private static readonly Regex NameAndPid = new(@"(?<name>[A-Za-z0-9_.\-]+) \((?<pid>\d{1,10})\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PidOnly = new(@"\b(?:pid|PID|process) (?<pid>\d{1,10})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExeName = new(@"(?<name>[A-Za-z0-9_.\-]+\.exe)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private const int HeadBytes = 64;

    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly FileState _previous;
    private readonly FileState _current;
    private ITimer? _idleTimer;
    private long _lastReadTicks;

    public EngineLogReader(string path, TimeProvider? time = null)
    {
        CurrentPath = Path.GetFullPath(path);
        PreviousPath = FileLoggerProvider.RolledOverPath(CurrentPath);
        _time = time ?? TimeProvider.System;
        _previous = new FileState(PreviousPath);
        _current = new FileState(CurrentPath);
    }

    public string CurrentPath { get; }
    public string PreviousPath { get; }

    /// <summary>True while parsed entries are held in memory.</summary>
    public bool IsCached
    {
        get { lock (_sync) return _previous.Lines.Count > 0 || _current.Lines.Count > 0; }
    }

    /// <summary>Every entry in both files, oldest first.</summary>
    public EngineLogLine[] Read()
    {
        lock (_sync)
        {
            _lastReadTicks = _time.GetTimestamp();
            _idleTimer ??= _time.CreateTimer(_ => ReleaseIfIdle(), null, IdleRelease, IdleRelease);

            Refresh(_previous);
            Refresh(_current);
            var all = new EngineLogLine[_previous.Lines.Count + _current.Lines.Count];
            _previous.Lines.CopyTo(all, 0);
            _current.Lines.CopyTo(all, _previous.Lines.Count);
            return all;
        }
    }

    /// <summary>Drops the cache if nothing has read the log for <see cref="IdleRelease"/>.</summary>
    public void ReleaseIfIdle()
    {
        lock (_sync)
        {
            if (_time.GetElapsedTime(_lastReadTicks) < IdleRelease) return;
            _previous.Reset();
            _current.Reset();
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    private static void Refresh(FileState state)
    {
        FileStream stream;
        try
        {
            // The engine's own logger appends while we read, and rolls the file over by renaming it.
            stream = new FileStream(state.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            state.Reset();
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // keep what we had; the next read tries again
        }

        using (stream)
        {
            var length = stream.Length;
            var head = new byte[(int)Math.Min(HeadBytes, length)];
            stream.ReadExactly(head);

            // Still the same file, only longer: parse what was added. Anything else (rolled over, replaced, truncated)
            // starts again from the top.
            bool appended = state.Length >= 0 && length >= state.Length && head.AsSpan().StartsWith(state.Head);
            if (!appended) state.Reset();
            if (length == state.Consumed) return;

            stream.Position = state.Consumed;
            var bytes = new byte[length - state.Consumed];
            stream.ReadExactly(bytes);

            // Only whole lines: a line still being written is read next time.
            var end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end < 0)
            {
                state.Length = length;
                state.Head = head;
                return;
            }

            Parse(state, Encoding.UTF8.GetString(bytes, 0, end + 1));
            state.Consumed += end + 1;
            state.Length = length;
            state.Head = head;
        }
    }

    private static void Parse(FileState state, string text)
    {
        // A line that isn't an entry continues the one before it (an exception's text).
        StringBuilder? detail = null;
        EngineLogLine? pending = null;

        void Flush()
        {
            if (pending == null) return;
            if (detail != null) pending = pending with { Detail = detail.ToString().TrimEnd() };
            state.Lines.Add(pending);
            pending = null;
            detail = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var match = LinePattern.Match(line);
            if (!match.Success)
            {
                if (line.Length == 0 && pending == null) continue;
                if (pending == null && state.Lines.Count > 0)
                {
                    // More of the last entry from an earlier read.
                    pending = state.Lines[^1];
                    state.Lines.RemoveAt(state.Lines.Count - 1);
                    if (pending.Detail != null) detail = new StringBuilder(pending.Detail).Append('\n');
                }
                if (pending != null)
                {
                    (detail ??= new StringBuilder()).Append(line).Append('\n');
                }
                continue;
            }

            Flush();
            var local = DateTime.ParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None);
            var hash = Fnv1a(line);
            pending = new EngineLogLine(
                new EngineLogKey(local.Ticks, hash, state.NextOrdinal(local.Ticks, hash)),
                DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime(),
                ParseLevel(match.Groups["level"].Value),
                string.Intern(match.Groups["category"].Value),
                match.Groups["message"].Value,
                null,
                null,
                null);
            var (name, pid) = GuessProcess(pending.Message);
            if (name != null || pid != null) pending = pending with { ProcessName = name, Pid = pid };
        }
        Flush();
    }

    /// <summary>The process a message names, if any: "name (pid)", else a pid and/or an "x.exe" found separately.</summary>
    internal static (string? Name, int? Pid) GuessProcess(string message)
    {
        var both = NameAndPid.Match(message);
        if (both.Success && int.TryParse(both.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p))
            return (both.Groups["name"].Value, p);

        int? pid = null;
        var pidMatch = PidOnly.Match(message);
        if (pidMatch.Success && int.TryParse(pidMatch.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var q)) pid = q;
        var exe = ExeName.Match(message);
        return (exe.Success ? exe.Groups["name"].Value : null, pid);
    }

    private static LogLevelDto ParseLevel(string level) => level switch
    {
        "TRC" => LogLevelDto.Trace,
        "DBG" => LogLevelDto.Debug,
        "WRN" => LogLevelDto.Warning,
        "ERR" => LogLevelDto.Error,
        "CRT" => LogLevelDto.Critical,
        _ => LogLevelDto.Information,
    };

    // Stable across processes, unlike string.GetHashCode, so a cursor still works after the engine restarts.
    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }

    private sealed class FileState(string path)
    {
        public string Path { get; } = path;
        public List<EngineLogLine> Lines { get; } = new();
        public long Length { get; set; } = -1;
        public byte[] Head { get; set; } = Array.Empty<byte>();
        public long Consumed { get; set; }

        // Ordinals only need counting within one timestamp, and the log is written in time order.
        private long _ordinalTicks = long.MinValue;
        private readonly Dictionary<uint, int> _ordinals = new();

        public int NextOrdinal(long ticks, uint hash)
        {
            if (ticks != _ordinalTicks)
            {
                _ordinalTicks = ticks;
                _ordinals.Clear();
            }
            _ordinals.TryGetValue(hash, out var ordinal);
            _ordinals[hash] = ordinal + 1;
            return ordinal;
        }

        public void Reset()
        {
            Lines.Clear();
            Lines.TrimExcess();
            Length = -1;
            Head = Array.Empty<byte>();
            Consumed = 0;
            _ordinalTicks = long.MinValue;
            _ordinals.Clear();
        }
    }
}
