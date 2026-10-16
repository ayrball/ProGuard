using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Shared.Reporting;

namespace ProGuard.Engine.Services.SessionAgents;

/// <summary>A Windows session with a user signed in. <see cref="IsActive"/>: connected (console or remote), not just
/// kept in the background by fast user switching or a dropped remote connection.</summary>
public sealed record UserSession(int Id, bool IsActive);

/// <summary>A running (or recently running) session agent process.</summary>
public interface IAgentProcess : IDisposable
{
    int Id { get; }

    /// <summary>The exit code once the process has ended; null while it runs.</summary>
    int? ExitCode { get; }

    /// <summary>Ends the process if it still runs.</summary>
    void Kill();
}

/// <summary>What <see cref="SessionAgentSupervisor"/> needs from Windows; tests replace it.</summary>
public interface ISessionAgentPlatform
{
    /// <summary>False when the agent's executable isn't next to the engine (a partial build or install).</summary>
    bool IsAgentInstalled { get; }

    /// <summary>Sessions with a user signed in, other than session 0.</summary>
    IReadOnlyList<UserSession> GetUserSessions();

    /// <summary>An agent from this installation already running in the session (left by an engine that crashed), or null.</summary>
    IAgentProcess? FindRunningAgent(int sessionId);

    /// <summary>Starts the agent in the session as its user. Throws when it can't.</summary>
    IAgentProcess Launch(int sessionId);
}

/// <summary>
/// Keeps one session agent (ProGuard.SessionAgent) running in every signed-in user's session, so the engine hears
/// about each desktop's foreground process and idle time whether or not the ProGuard window is open. This is what
/// Process Lasso's service does with bitsumsessionagent.exe.
/// <list type="bullet">
/// <item>As a service: an agent in every active session (console or remote), started as that session's user through
/// <see cref="UserSessionLauncher"/>. Sessions are listed every <see cref="CheckInterval"/>, and at once when the
/// service hears of a sign-in, connection or unlock (<see cref="OnSessionChange"/>).</item>
/// <item>As an ordinary process (run from a console): only in the engine's own session, as the engine's user.</item>
/// <item>An agent that exits is started again after <see cref="FirstRetryDelay"/>, doubling with every failure up to
/// <see cref="MaxRetryDelay"/>; an agent that ran for <see cref="StableRunTime"/> first counts as having worked. A
/// session whose agent can't be started at all backs off the same way.</item>
/// <item>A session whose user has disconnected keeps its agent; a new one starts there only when they come back.</item>
/// <item>At sign-out the agent is ended and the session left alone (it can still be listed for a moment while it
/// closes). When the engine stops, it ends every agent, so none is left holding the installed files.</item>
/// <item>An agent left running by an engine that died is adopted rather than duplicated.</item>
/// </list>
/// </summary>
public sealed class SessionAgentSupervisor : IDisposable
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);
    /// <summary>An agent that ran this long before it exited was working: its earlier failures are forgotten.</summary>
    internal static readonly TimeSpan StableRunTime = TimeSpan.FromMinutes(1);
    /// <summary>How long after a sign-out its session is left alone if the service hears nothing more about it.</summary>
    internal static readonly TimeSpan SignOutGrace = TimeSpan.FromSeconds(30);

    private readonly ILogger<SessionAgentSupervisor> _logger;
    private readonly ISessionAgentPlatform _platform;
    private readonly int? _onlySession;
    private readonly Func<TimeSpan> _clock;

    private readonly object _sync = new();
    private readonly Dictionary<int, SessionState> _sessions = new();
    /// <summary>Sessions signing out, and when we heard: no agent is started into them.</summary>
    private readonly Dictionary<int, TimeSpan> _signingOut = new();
    private Timer? _timer;
    private bool _running;

    private sealed class SessionState
    {
        public IAgentProcess? Agent;
        public TimeSpan StartedAt;
        public int Failures;
        public TimeSpan NextAttemptAt;
    }

    /// <param name="onlySession">Supervise this session only (the engine's own, when it isn't a service); null for every session.</param>
    public SessionAgentSupervisor(ILogger<SessionAgentSupervisor>? logger, ISessionAgentPlatform platform, int? onlySession,
        Func<TimeSpan>? clock = null)
    {
        _logger = logger ?? NullLogger<SessionAgentSupervisor>.Instance;
        _platform = platform;
        _onlySession = onlySession;
        _clock = clock ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>Sessions with an agent we're watching (running or not yet noticed to have exited).</summary>
    public IReadOnlyCollection<int> SupervisedSessions
    {
        get { lock (_sync) return _sessions.Where(s => s.Value.Agent != null).Select(s => s.Key).ToList(); }
    }

    /// <summary>The agent's process id in that session, or null.</summary>
    public int? AgentProcessId(int sessionId)
    {
        lock (_sync) return _sessions.TryGetValue(sessionId, out var s) ? s.Agent?.Id : null;
    }

    /// <summary>Starts supervising. False (and nothing happens) when the agent isn't installed.</summary>
    public bool Start()
    {
        if (!StartSupervising()) return false;
        _timer = new Timer(_ =>
        {
            // An exception escaping a timer callback would take the whole engine down.
            try { Reconcile(); }
            catch (Exception ex) { _logger.LogError(ex, "Session agent check failed"); }
        }, null, TimeSpan.Zero, CheckInterval);
        return true;
    }

    /// <summary><see cref="Start"/> without the timer: tests call <see cref="Reconcile"/> themselves.</summary>
    internal bool StartSupervising()
    {
        if (!_platform.IsAgentInstalled)
        {
            _logger.LogWarning("Session agent not found next to the engine: foreground and idle reports will come from the UI only");
            return false;
        }

        lock (_sync) _running = true;
        _logger.LogInformation(_onlySession is int own
            ? "Keeping a session agent running in this session ({Session})"
            : "Keeping a session agent running in every user session", _onlySession);
        return true;
    }

    /// <summary>Stops supervising and ends every agent.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (!_running) return;
            _running = false;
            _timer?.Dispose();
            _timer = null;
            foreach (var id in _sessions.Keys.ToList()) EndAgent(id, "the engine is stopping");
        }
    }

    /// <summary>The service heard of a session change (SERVICE_CONTROL_SESSIONCHANGE).</summary>
    public void OnSessionChange(int sessionId, SessionChangeReason reason)
    {
        lock (_sync)
        {
            if (!_running) return;
            switch (reason)
            {
                case SessionChangeReason.SessionLogoff:
                    _signingOut[sessionId] = _clock();
                    EndAgent(sessionId, "the user signed out");
                    return;
                case SessionChangeReason.SessionLogon:
                    // A session id can be reused by the next sign-in.
                    _signingOut.Remove(sessionId);
                    break;
            }
        }
        // Sign-in, connect, unlock: start an agent now rather than at the next check.
        Reconcile();
    }

    /// <summary>One check: start agents where they're missing and due, forget sessions that have gone.</summary>
    internal void Reconcile()
    {
        lock (_sync)
        {
            if (!_running) return;
            var now = _clock();

            IReadOnlyList<UserSession> listed;
            try { listed = _platform.GetUserSessions(); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not list user sessions");
                return;
            }

            var sessions = listed.Where(s => s.Id > 0 && (_onlySession == null || s.Id == _onlySession)).ToDictionary(s => s.Id);

            foreach (var (id, since) in _signingOut.ToList())
            {
                if (!sessions.ContainsKey(id) || now - since >= SignOutGrace) _signingOut.Remove(id);
            }

            foreach (var id in _sessions.Keys.ToList())
            {
                if (!sessions.ContainsKey(id) || _signingOut.ContainsKey(id)) EndAgent(id, "the session has ended");
            }

            foreach (var session in sessions.Values)
            {
                if (_signingOut.ContainsKey(session.Id)) continue;
                if (!_sessions.TryGetValue(session.Id, out var state))
                {
                    // Nothing to do for a disconnected session we never had an agent in.
                    if (!session.IsActive) continue;
                    _sessions[session.Id] = state = new SessionState();
                }

                if (state.Agent != null)
                {
                    if (state.Agent.ExitCode is not int exitCode) continue;
                    OnAgentExited(session.Id, state, exitCode, now);
                }

                if (session.IsActive && now >= state.NextAttemptAt) StartAgent(session.Id, state, now);
            }
        }
    }

    internal static TimeSpan RetryDelay(int failures)
    {
        if (failures <= 1) return FirstRetryDelay;
        var ticks = FirstRetryDelay.Ticks * Math.Pow(2, Math.Min(failures - 1, 30));
        return ticks >= MaxRetryDelay.Ticks ? MaxRetryDelay : TimeSpan.FromTicks((long)ticks);
    }

    private void OnAgentExited(int sessionId, SessionState state, int exitCode, TimeSpan now)
    {
        var ran = now - state.StartedAt;
        var pid = state.Agent!.Id;
        state.Agent.Dispose();
        state.Agent = null;

        if (ran >= StableRunTime) state.Failures = 0;
        state.Failures++;
        var delay = RetryDelay(state.Failures);
        state.NextAttemptAt = now + delay;

        if (exitCode == SessionAgentInstance.AlreadyRunningExitCode)
            _logger.LogInformation("Session agent {Pid} in session {Session} found another agent running there; checking again in {Delay:g}",
                pid, sessionId, delay);
        else
            _logger.LogWarning("Session agent {Pid} in session {Session} exited with code {Code} after {Ran:g}; restarting in {Delay:g}",
                pid, sessionId, exitCode, ran, delay);
    }

    private void StartAgent(int sessionId, SessionState state, TimeSpan now)
    {
        try
        {
            var agent = _platform.FindRunningAgent(sessionId);
            if (agent != null)
            {
                _logger.LogInformation("Session agent {Pid} already runs in session {Session}: watching it", agent.Id, sessionId);
            }
            else
            {
                agent = _platform.Launch(sessionId);
                _logger.LogInformation("Started session agent {Pid} in session {Session}", agent.Id, sessionId);
            }
            state.Agent = agent;
            state.StartedAt = now;
        }
        catch (Exception ex)
        {
            state.Failures++;
            var delay = RetryDelay(state.Failures);
            state.NextAttemptAt = now + delay;
            // Once at Warning; a session that keeps refusing (no user token yet, say) shouldn't fill the log.
            _logger.Log(state.Failures == 1 ? LogLevel.Warning : LogLevel.Debug, ex,
                "Could not start the session agent in session {Session}; trying again in {Delay:g}", sessionId, delay);
        }
    }

    private void EndAgent(int sessionId, string why)
    {
        if (!_sessions.Remove(sessionId, out var state) || state.Agent == null) return;
        try
        {
            if (state.Agent.ExitCode == null)
            {
                state.Agent.Kill();
                _logger.LogInformation("Ended session agent {Pid} in session {Session}: {Why}", state.Agent.Id, sessionId, why);
            }
        }
        catch (Exception ex)
        {
            // Windows ends it at sign-out anyway.
            _logger.LogDebug(ex, "Could not end session agent {Pid} in session {Session}", state.Agent.Id, sessionId);
        }
        finally
        {
            state.Agent.Dispose();
        }
    }

    public void Dispose() => Stop();
}
