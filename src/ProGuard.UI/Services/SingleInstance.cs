using System.Threading;
using ProGuard.Shared.Messages;

namespace ProGuard.UI.Services;

/// <summary>
/// One ProGuard window per Windows session. A second launch (the Start menu while the sign-in copy sits in the tray,
/// say) asks the first copy to show its window and exits, instead of adding a second tray icon, a second foreground
/// reporter and a second balloon for every Watchdog event.
/// </summary>
/// <remarks>
/// The names live in the session's Local\ namespace, so other users' sessions have their own copy. If a copy's objects
/// can't be opened at all, this launch can't ask it anything and starts normally.
/// <para>
/// A third object, the quit event (<see cref="UiInstanceNames.QuitEvent"/>), lets the installer end the window
/// gracefully before it replaces or removes the files: the engine's <c>--stop-clients</c> mode, running as LocalSystem,
/// sets it in every session, and the window quits as its tray menu's Exit would. Anyone in the session who can set it
/// could also just end the process, so it gives nobody anything new.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    public const string DefaultName = UiInstanceNames.Standard;

    /// <summary>
    /// Standard and elevated copies count separately: "Run as administrator" while the standard copy sits in the tray
    /// has to start an elevated window, not just show the standard one (the UI tells standard users to do exactly that).
    /// </summary>
    public static string NameFor(bool elevated) => elevated ? UiInstanceNames.Elevated : UiInstanceNames.Standard;

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly RegisteredWaitHandle _registration;
    private readonly EventWaitHandle? _quit;
    private readonly RegisteredWaitHandle? _quitRegistration;

    private SingleInstance(Mutex mutex, EventWaitHandle show, Action onShowRequested, EventWaitHandle? quit, Action? onQuitRequested)
    {
        _mutex = mutex;
        _show = show;
        _registration = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
        if (quit != null && onQuitRequested != null)
        {
            _quit = quit;
            _quitRegistration = ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => onQuitRequested(), null, Timeout.Infinite, executeOnlyOnce: true);
        }
    }

    /// <summary>
    /// False when another copy is already running in this session and has been asked to show its window (or, with
    /// <paramref name="askToShow"/> false, as for a second sign-in start, left alone): the caller should exit.
    /// Otherwise true, with <paramref name="instance"/> (null if Windows wouldn't create the objects) to keep until
    /// exit. <paramref name="onShowRequested"/> runs on a thread-pool thread whenever a later launch asks, and
    /// <paramref name="onQuitRequested"/>, if given, once when the quit event is set.
    /// </summary>
    public static bool TryClaim(Action onShowRequested, out SingleInstance? instance, bool askToShow = true, string name = DefaultName,
        Action? onQuitRequested = null)
    {
        instance = null;
        try
        {
            var mutex = new Mutex(initiallyOwned: false, $@"Local\{name}", out bool createdNew);
            if (createdNew)
            {
                var quit = onQuitRequested == null ? null : new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\{UiInstanceNames.QuitEvent(name)}");
                // A quit request left over from before this copy started isn't for it.
                quit?.Reset();
                instance = new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{UiInstanceNames.ShowEvent(name)}"), onShowRequested,
                    quit, onQuitRequested);
                return true;
            }
            mutex.Dispose();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Another copy's objects exist but aren't ours to open (it runs elevated): fall through and try to signal.
        }

        return !TrySignal(name, askToShow);
    }

    // True when a copy is listening on the name (and, if asked, has been told to show itself).
    private static bool TrySignal(string name, bool askToShow)
    {
        try
        {
            using var show = EventWaitHandle.OpenExisting($@"Local\{UiInstanceNames.ShowEvent(name)}");
            return !askToShow || show.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _quitRegistration?.Unregister(null);
        _quit?.Dispose();
        _show.Dispose();
        _mutex.Dispose();
    }
}
