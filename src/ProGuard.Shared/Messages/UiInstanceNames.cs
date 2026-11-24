namespace ProGuard.Shared.Messages;

/// <summary>
/// The names of the kernel objects each ProGuard window keeps in its session's <c>Local\</c> namespace
/// (<c>SingleInstance</c> in the UI). The engine's <c>--stop-clients</c> mode opens the quit event of every session's
/// window by these names, so both sides take them from here.
/// </summary>
public static class UiInstanceNames
{
    /// <summary>The window started with standard rights.</summary>
    public const string Standard = "ProGuard.UI";

    /// <summary>The window started with administrator rights; it counts separately (see <c>SingleInstance.NameFor</c>).</summary>
    public const string Elevated = "ProGuard.UI.Elevated";

    /// <summary>A later launch sets this to have the running window show itself.</summary>
    public static string ShowEvent(string instance) => instance + ".Show";

    /// <summary>Setting this asks the running window to quit, as its tray menu's Exit does.</summary>
    public static string QuitEvent(string instance) => instance + ".Quit";
}
