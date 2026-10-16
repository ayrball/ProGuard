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
/// <c>GetForegroundWindow</c> only sees session 0's own (empty) desktop, so each user session's agent (or, without
/// one, its UI) reports its desktop's foreground process over <see cref="IPC.ForegroundPipeServer"/>.
/// <para>
/// A report lasts as long as the reporting connection. With several sessions reporting (fast user switching, RDP),
/// one session's report is used, chosen in this order:
/// <list type="number">
/// <item>the session attached to the physical console;</item>
/// <item>else the session whose user touched the keyboard or mouse most recently, by more than
/// <see cref="IdleTolerance"/> (a remote desktop user while the console sits at the sign-in screen);</item>
/// <item>else the one whose foreground changed last. Reporters repeat their report every few seconds; a repeat isn't a
/// change, so two sessions don't take turns just because each keeps repeating itself.</item>
/// </list>
/// With no reporter, the engine falls back to asking Windows directly, which only works when it isn't running in
/// session 0; otherwise <see cref="IsForegroundKnown"/> is false.
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

    /// <summary>Three missed repeats (reporters repeat every 5 s): the reporter is hung, not just quiet.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    /// <summary>Idle times this close count as equal. A user at the keyboard reports at least every 5 s, and the engine
    /// ages each report until the next, so two active users can look up to that far apart.</summary>
    internal static readonly TimeSpan IdleTolerance = TimeSpan.FromSeconds(10);

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

    /// <param name="ChangedSequence">Order of the last report that changed <paramref name="Pid"/> (repeats keep it).</param>
    /// <param name="Idle">Idle time the reporter saw, or null if it didn't say.</param>
    /// <param name="ReceivedAt">Engine clock when the report arrived, to age <paramref name="Idle"/>.</param>
    private sealed record Reported(int SessionId, int Pid, long ChangedSequence, TimeSpan? Idle, TimeSpan ReceivedAt);

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
        lock (_sync)
        {
            var changed = _reports.TryGetValue(connectionId, out var previous) && previous.Pid == pid
                ? previous.ChangedSequence
                : ++_sequence;
            _reports[connectionId] = new Reported(clientSessionId, pid, changed, idle, _clock());
        }
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
                var now = _clock();
                Reported? best = null;
                foreach (var r in _reports.Values)
                {
                    if (best == null || Prefer(r, best, console, now)) best = r;
                }
                return best!.Pid;
            }
        }
        return _canSeeDesktop ? _localForegroundPid() : 0;
    }

    /// <summary>
    /// False when nothing has told the engine what the foreground process is for a while: no reporter is connected
    /// (or none has spoken for <see cref="StaleAfter"/>) and the engine can't see a desktop itself. A 0 from
    /// <see cref="GetForegroundPid"/> then means "don't know", not "nothing has focus".
    /// </summary>
    public bool IsForegroundKnown
    {
        get
        {
            if (_canSeeDesktop) return true;
            lock (_sync)
            {
                var now = _clock();
                foreach (var r in _reports.Values)
                {
                    if (now - r.ReceivedAt < StaleAfter) return true;
                }
                return false;
            }
        }
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

    private static bool Prefer(Reported candidate, Reported current, int consoleSession, TimeSpan now)
    {
        var candidateOnConsole = candidate.SessionId == consoleSession;
        var currentOnConsole = current.SessionId == consoleSession;
        if (candidateOnConsole != currentOnConsole) return candidateOnConsole;

        if (candidate.SessionId != current.SessionId && candidate.Idle is { } a && current.Idle is { } b)
        {
            var candidateIdle = a + (now - candidate.ReceivedAt);
            var currentIdle = b + (now - current.ReceivedAt);
            if ((candidateIdle - currentIdle).Duration() > IdleTolerance) return candidateIdle < currentIdle;
        }
        return candidate.ChangedSequence > current.ChangedSequence;
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
