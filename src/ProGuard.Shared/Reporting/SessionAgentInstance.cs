using System.Threading;

namespace ProGuard.Shared.Reporting;

/// <summary>
/// The session agent (ProGuard.SessionAgent) reports the foreground process and idle time for its session so that
/// doesn't depend on the UI being open. One runs per session: it holds a mutex in the session's Local\ namespace,
/// which other users' sessions don't share. The UI's own reporter checks the same mutex and stands aside while it
/// exists.
/// </summary>
public static class SessionAgentInstance
{
    public const string MutexName = @"Local\ProGuard.SessionAgent";

    /// <summary>Overrides the mutex name, so agents that tests start don't silence the UI on the developer's desktop.</summary>
    public const string MutexNameVariable = "PROGUARD_AGENT_MUTEX";

    public static string DefaultMutexName =>
        Environment.GetEnvironmentVariable(MutexNameVariable) is { Length: > 0 } name ? name : MutexName;

    /// <summary>File name of the agent, published next to the engine.</summary>
    public const string ExecutableName = "ProGuard.SessionAgent.exe";

    /// <summary>The agent's exit code when another agent already runs in its session.</summary>
    public const int AlreadyRunningExitCode = 2;

    /// <summary>
    /// Claims this session for the caller: the mutex to keep for as long as it reports, or null when another agent
    /// already has it (including one we may not open, such as an elevated copy's).
    /// </summary>
    public static Mutex? TryClaim(string? name = null)
    {
        name ??= DefaultMutexName;
        try
        {
            var mutex = new Mutex(initiallyOwned: false, name, out bool createdNew);
            if (createdNew) return mutex;
            mutex.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True while an agent holds this session's mutex.</summary>
    public static bool IsRunning(string? name = null)
    {
        name ??= DefaultMutexName;
        try
        {
            if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // It exists; it just isn't ours to open (an agent started elevated, by an engine run from a console).
            return true;
        }
    }
}
