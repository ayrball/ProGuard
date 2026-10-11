using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Native;

namespace ProGuard.Engine.Services;

/// <summary>Which process owns the user's foreground window.</summary>
public interface IForegroundTracker
{
    /// <summary>PID of the foreground process, or 0 if unknown.</summary>
    int GetForegroundPid();
}

/// <summary>How long the user has left the keyboard and mouse alone.</summary>
public interface IUserIdleSource
{
    /// <summary>Time since the last input, or null when nobody can tell (no UI reporting to a session-0 engine).</summary>
    TimeSpan? GetIdleTime();
}

/// <summary>
/// Knows the foreground process even when the engine runs as a service. A service lives in session 0, where
/// <c>GetForegroundWindow</c> only sees session 0's own (empty) desktop, so each user's UI reports its desktop's
/// foreground process over <see cref="IPC.ForegroundPipeServer"/>.
/// <para>
/// A report lasts as long as the reporting connection. With several reporters (fast user switching, RDP), the one
/// in the session attached to the physical console wins, then the most recent. With none, the engine falls back
/// to asking Windows directly, which only works when it isn't running in session 0.
/// </para>
/// <para>
/// The same reports carry the reporter's idle time (<c>GetLastInputInfo</c> is per session too, so a service can't
/// read it itself). The machine counts as idle only while every desktop we know of is idle: the power plan is
/// system-wide, so one active user is enough to keep it. A report is the idle time when it was sent; it is aged by
/// the time since, so the engine needs no report while the user stays away, and the reporter sends one right away
/// when they come back (see <c>ForegroundReporter</c>).
/// </para>
/// </summary>
public sealed class ForegroundTracker : IForegroundTracker, IUserIdleSource
{
    private const int UnknownSession = -1;

    private readonly ILogger<ForegroundTracker> _logger;
    private readonly Func<int> _localForegroundPid;
    private readonly Func<int, int?> _sessionOfPid;
    private readonly Func<int> _activeConsoleSession;
    private readonly bool _canSeeDesktop;
    private readonly Func<TimeSpan?> _localIdleTime;
    private readonly Func<TimeSpan> _clock;

    private readonly object _sync = new();
    private readonly Dictionary<long, Reported> _reports = new();
    private long _sequence;

    /// <param name="Idle">Idle time the reporter saw, or null if it didn't say.</param>
    /// <param name="ReceivedAt">Engine clock when the report arrived, to age <paramref name="Idle"/>.</param>
    private sealed record Reported(int SessionId, int Pid, long Sequence, TimeSpan? Idle, TimeSpan ReceivedAt);

    public ForegroundTracker(ILogger<ForegroundTracker> logger)
        : this(logger, ReadLocalForegroundPid, SessionOf, ActiveConsoleSession, Process.GetCurrentProcess().SessionId != 0)
    {
    }

    internal ForegroundTracker(ILogger<ForegroundTracker>? logger, Func<int> localForegroundPid, Func<int, int?> sessionOfPid,
        Func<int> activeConsoleSession, bool canSeeDesktop, Func<TimeSpan?>? localIdleTime = null, Func<TimeSpan>? clock = null)
    {
        _logger = logger ?? NullLogger<ForegroundTracker>.Instance;
        _localForegroundPid = localForegroundPid;
        _sessionOfPid = sessionOfPid;
        _activeConsoleSession = activeConsoleSession;
        _canSeeDesktop = canSeeDesktop;
        _localIdleTime = localIdleTime ?? User32.GetIdleTime;
        _clock = clock ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>True while at least one UI is reporting.</summary>
    public bool HasReporters
    {
        get { lock (_sync) return _reports.Count > 0; }
    }

    /// <summary>
    /// Records what a reporter in <paramref name="clientSessionId"/> sees. A reporter may only name a process in its
    /// own session (or 0 for "nothing"), so one user can't exempt or boost processes on someone else's desktop.
    /// </summary>
    public bool Report(long connectionId, int clientSessionId, int pid, TimeSpan? idle = null)
    {
        if (pid < 0) return false;
        if (pid != 0 && _sessionOfPid(pid) != clientSessionId)
        {
            _logger.LogDebug("Ignoring foreground report of PID {Pid} from session {Session}: not in that session", pid, clientSessionId);
            return false;
        }

        if (idle < TimeSpan.Zero) idle = null;
        lock (_sync) _reports[connectionId] = new Reported(clientSessionId, pid, ++_sequence, idle, _clock());
        return true;
    }

    /// <summary>The reporter went away; its last report no longer says anything about the desktop. False if it never reported.</summary>
    public bool Disconnect(long connectionId)
    {
        lock (_sync) return _reports.Remove(connectionId);
    }

    public int GetForegroundPid()
    {
        lock (_sync)
        {
            if (_reports.Count > 0)
            {
                var console = _activeConsoleSession();
                Reported? best = null;
                foreach (var r in _reports.Values)
                {
                    if (best == null || Prefer(r, best, console)) best = r;
                }
                return best!.Pid;
            }
        }
        return _canSeeDesktop ? _localForegroundPid() : 0;
    }

    public TimeSpan? GetIdleTime()
    {
        TimeSpan? least = null;
        lock (_sync)
        {
            var now = _clock();
            foreach (var r in _reports.Values)
            {
                if (r.Idle is not { } idle) continue;
                var aged = idle + (now - r.ReceivedAt);
                if (least == null || aged < least) least = aged;
            }
        }
        // An engine started on a user's desktop (not as a service) can read its own session's input as well.
        if (_canSeeDesktop && _localIdleTime() is { } local && (least == null || local < least)) least = local;
        return least;
    }

    private static bool Prefer(Reported candidate, Reported current, int consoleSession)
    {
        var candidateOnConsole = candidate.SessionId == consoleSession;
        var currentOnConsole = current.SessionId == consoleSession;
        if (candidateOnConsole != currentOnConsole) return candidateOnConsole;
        return candidate.Sequence > current.Sequence;
    }

    private static int ReadLocalForegroundPid()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    private static int? SessionOf(int pid) =>
        Kernel32.ProcessIdToSessionId((uint)pid, out var session) ? (int)session : null;

    private static int ActiveConsoleSession()
    {
        var session = Kernel32.WTSGetActiveConsoleSessionId();
        return session == uint.MaxValue ? UnknownSession : (int)session;
    }
}
