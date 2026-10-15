using System.Threading;

namespace ProGuard.UI.Services;

/// <summary>
/// One ProGuard window per Windows session. A second launch (the Start menu while the sign-in copy sits in the tray,
/// say) asks the first copy to show its window and exits, instead of adding a second tray icon, a second foreground
/// reporter and a second balloon for every Watchdog event.
/// </summary>
/// <remarks>
/// The names live in the session's Local\ namespace, so other users' sessions have their own copy. If a copy's objects
/// can't be opened at all, this launch can't ask it anything and starts normally.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    public const string DefaultName = "ProGuard.UI";

    /// <summary>
    /// Standard and elevated copies count separately: "Run as administrator" while the standard copy sits in the tray
    /// has to start an elevated window, not just show the standard one (the UI tells standard users to do exactly that).
    /// </summary>
    public static string NameFor(bool elevated) => elevated ? DefaultName + ".Elevated" : DefaultName;

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle show, Action onShowRequested)
    {
        _mutex = mutex;
        _show = show;
        _registration = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// False when another copy is already running in this session and has been asked to show its window (or, with
    /// <paramref name="askToShow"/> false, as for a second sign-in start, left alone): the caller should exit.
    /// Otherwise true, with <paramref name="instance"/> (null if Windows wouldn't create the objects) to keep until
    /// exit. <paramref name="onShowRequested"/> runs on a thread-pool thread whenever a later launch asks.
    /// </summary>
    public static bool TryClaim(Action onShowRequested, out SingleInstance? instance, bool askToShow = true, string name = DefaultName)
    {
        instance = null;
        try
        {
            var mutex = new Mutex(initiallyOwned: false, $@"Local\{name}", out bool createdNew);
            if (createdNew)
            {
                instance = new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show"), onShowRequested);
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
            using var show = EventWaitHandle.OpenExisting($@"Local\{name}.Show");
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
        _show.Dispose();
        _mutex.Dispose();
    }
}
