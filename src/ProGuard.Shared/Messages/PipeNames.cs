namespace ProGuard.Shared.Messages;

public static class PipeNames
{
    /// <summary>
    /// Request/reply command pipe. Any interactive user may connect; the engine checks each request against the
    /// caller's own rights, so a standard user can only act on processes they could open themselves.
    /// </summary>
    public const string Engine = "ProGuardEngine";

    /// <summary>
    /// One-way pipe on which each signed-in user's UI reports its desktop's foreground process
    /// (<see cref="CommandTypes.ReportForeground"/>). Writable by any authenticated user, so it accepts nothing else.
    /// </summary>
    public const string Foreground = "ProGuardEngine.Foreground";
}
