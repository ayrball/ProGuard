namespace ProGuard.Shared.Messages;

public static class PipeNames
{
    /// <summary>Request/reply command pipe. Default pipe security: only administrators and SYSTEM can write.</summary>
    public const string Engine = "ProGuardEngine";

    /// <summary>
    /// One-way pipe on which each signed-in user's UI reports its desktop's foreground process
    /// (<see cref="CommandTypes.ReportForeground"/>). Writable by any authenticated user, so it accepts nothing else.
    /// </summary>
    public const string Foreground = "ProGuardEngine.Foreground";
}
