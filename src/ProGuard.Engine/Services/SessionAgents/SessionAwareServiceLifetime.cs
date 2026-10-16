using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProGuard.Engine.Services.SessionAgents;

/// <summary>
/// The Windows service lifetime, also asking the SCM for session changes (SERVICE_ACCEPT_SESSIONCHANGE), so the
/// <see cref="SessionAgentSupervisor"/> starts an agent as soon as a user signs in or reconnects, and stops it at
/// sign-out, rather than at its next check.
/// </summary>
public sealed class SessionAwareServiceLifetime : WindowsServiceLifetime
{
    private readonly SessionAgentSupervisor _agents;
    private readonly ILogger<SessionAwareServiceLifetime> _logger;

    public SessionAwareServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory, IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor, SessionAgentSupervisor agents)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _agents = agents;
        _logger = loggerFactory.CreateLogger<SessionAwareServiceLifetime>();
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        _logger.LogDebug("Session {Session}: {Reason}", changeDescription.SessionId, changeDescription.Reason);
        try { _agents.OnSessionChange(changeDescription.SessionId, changeDescription.Reason); }
        catch (Exception ex) { _logger.LogError(ex, "Session change handling failed"); }
        base.OnSessionChange(changeDescription);
    }
}
