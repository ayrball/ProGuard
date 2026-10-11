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

/// <summary>
/// Knows the foreground process even when the engine runs as a service. A service lives in session 0, where
/// <c>GetForegroundWindow</c> only sees session 0's own (empty) desktop, so each user's UI reports its desktop's
/// foreground process over <see cref="IPC.ForegroundPipeServer"/>.
/// <para>
/// A report lasts as long as the reporting connection. With several reporters (fast user switching, RDP), the one
/// in the session attached to the physical console wins, then the most recent. With none, the engine falls back
/// to asking Windows directly, which only works when it isn't running in session 0.
/// </para>
/// </summary>
public sealed class ForegroundTracker : IForegroundTracker
{
    private const int UnknownSession = -1;

    private readonly ILogger<ForegroundTracker> _logger;
    private readonly Func<int> _localForegroundPid;
    private readonly Func<int, int?> _sessionOfPid;
    private readonly Func<int> _activeConsoleSession;
    private readonly bool _canSeeDesktop;

    private readonly object _sync = new();
    private readonly Dictionary<long, Reported> _reports = new();
    private long _sequence;

    private sealed record Reported(int SessionId, int Pid, long Sequence);

    public ForegroundTracker(ILogger<ForegroundTracker> logger)
        : this(logger, ReadLocalForegroundPid, SessionOf, ActiveConsoleSession, Process.GetCurrentProcess().SessionId != 0)
    {
    }

    internal ForegroundTracker(ILogger<ForegroundTracker>? logger, Func<int> localForegroundPid, Func<int, int?> sessionOfPid,
        Func<int> activeConsoleSession, bool canSeeDesktop)
    {
        _logger = logger ?? NullLogger<ForegroundTracker>.Instance;
        _localForegroundPid = localForegroundPid;
        _sessionOfPid = sessionOfPid;
        _activeConsoleSession = activeConsoleSession;
        _canSeeDesktop = canSeeDesktop;
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
    public bool Report(long connectionId, int clientSessionId, int pid)
    {
        if (pid < 0) return false;
        if (pid != 0 && _sessionOfPid(pid) != clientSessionId)
        {
            _logger.LogDebug("Ignoring foreground report of PID {Pid} from session {Session}: not in that session", pid, clientSessionId);
            return false;
        }

        lock (_sync) _reports[connectionId] = new Reported(clientSessionId, pid, ++_sequence);
        return true;
    }

    /// <summary>The reporter went away; its last report no longer says anything about the desktop.</summary>
    public void Disconnect(long connectionId)
    {
        lock (_sync) _reports.Remove(connectionId);
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
