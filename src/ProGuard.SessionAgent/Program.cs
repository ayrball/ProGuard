using ProGuard.Shared.Reporting;

namespace ProGuard.SessionAgent;

/// <summary>
/// ProGuard's counterpart to Process Lasso's bitsumsessionagent.exe. The engine runs as a service in session 0 and can't
/// see any user's desktop, so it starts this in every signed-in session, as that session's user, and keeps it running.
/// It tells the engine which process owns the session's foreground window and how long the user has been idle,
/// whether or not the ProGuard window is open (<see cref="ForegroundReporter"/>).
/// <para>
/// One per session: a second copy exits at once with <see cref="SessionAgentInstance.AlreadyRunningExitCode"/>. It has
/// no window and never exits on its own; Windows ends it at sign-out and the engine when it stops.
/// </para>
/// </summary>
public static class Program
{
    public static int Main()
    {
        using var claim = SessionAgentInstance.TryClaim();
        if (claim == null) return SessionAgentInstance.AlreadyRunningExitCode;

        using var reporter = new ForegroundReporter(yieldToSessionAgent: false);
        reporter.StartAsync(CancellationToken.None);
        // The reporter's loop runs on the thread pool; this thread only keeps the process alive.
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
