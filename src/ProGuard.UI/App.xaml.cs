using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProGuard.Shared.Client;
using ProGuard.Shared.Reporting;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.UI;

public partial class App : Application
{
    private EngineHub? _hub;
    private TrayIconService? _tray;
    private WatchdogNotificationFeed? _watchdogFeed;
    private SingleInstance? _instance;

    // The engine runs as a service in session 0 and can't see this desktop: ProBalance's foreground exemption,
    // Foreground Boost and IdleSaver depend on a report of the foreground process and idle time. The session agent the
    // engine starts normally sends it; this one covers a session without one and stays quiet while the agent runs.
    private readonly ForegroundReporter _foregroundReporter = new(yieldToSessionAgent: true);

    /// <summary>
    /// Set by a host that builds the UI itself (tools/ProGuard.Preview) before constructing the App. WPF queues
    /// Startup from the Application constructor, so it fires inside any message pump, Run() or not; without this
    /// the single-instance check would hand over to a ProGuard window already open in the session and shut down.
    /// </summary>
    public static bool SkipStartup { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SkipStartup) return;

        // Started at sign-in (see RunKeyStartup): go straight to the tray rather than opening a window.
        bool trayStart = e.Args.Contains(RunKeyStartup.TrayArgument, StringComparer.OrdinalIgnoreCase);

        var elevation = new ElevationLauncher();

        // "Always start as administrator": a copy with standard rights hands over to an elevated one straight away, with
        // the same arguments (the sign-in start keeps --tray). Declined or failed, this copy carries on with standard rights.
        string? elevationProblem = null;
        if (!elevation.IsElevated && elevation.AlwaysElevate)
        {
            switch (elevation.Restart(e.Args, out var error))
            {
                case ElevationOutcome.Started:
                    Shutdown();
                    return;
                case ElevationOutcome.Failed:
                    elevationProblem = error;
                    break;
            }
        }

        // A copy already runs in this session: it shows its window (unless this is the sign-in start) and this one leaves.
        if (!SingleInstance.TryClaim(() => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront()), out _instance, askToShow: !trayStart,
            name: SingleInstance.NameFor(elevation.IsElevated)))
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        SessionEnding += (_, _) => { if (MainWindow is MainWindow w) w.IsQuitting = true; };

        var toaster = new Toaster(Dispatcher);
        var client = new EngineClient();
        _hub = new EngineHub(client, Dispatcher, toaster);
        var shell = new ShellViewModel(_hub, toaster, new IconCache(Dispatcher), elevation: elevation, preferences: new RegistryUiPreferences()) { Quit = Quit };
        // Restarted as administrator from another copy: back on the page it was showing.
        if (ElevationLauncher.PageFrom(e.Args) is { } page) shell.GoTo(page);
        if (elevationProblem != null) toaster.Show(elevationProblem, isError: true);

        var window = new MainWindow { DataContext = shell };
        MainWindow = window;

        try
        {
            _tray = new TrayIconService(shell, Dispatcher, window.ToggleVisibility, window.BringToFront, Quit);
            window.HasTray = true;
            // Watchdog rules that ask to be notified: the engine can't reach this desktop, so poll for them.
            _watchdogFeed = new WatchdogNotificationFeed(client, _tray.ShowWatchdogEvent);
            _watchdogFeed.Start();
        }
        catch (Exception ex)
        {
            // No tray (a locked-down shell, say): the app still works, the window just behaves as an ordinary one.
            LogError(ex);
        }

        bool toTray = window.HasTray && trayStart;
        if (!toTray) window.Show();

        _hub.Start();
        _foregroundReporter.StartAsync(CancellationToken.None);
    }

    private void Quit()
    {
        if (MainWindow is MainWindow w) w.IsQuitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        _watchdogFeed?.Dispose();
        _tray?.Dispose();
        _foregroundReporter.Dispose();
        _hub?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive and tell the user, but leave a trace for diagnosis.
        LogError(e.Exception);

        (MainWindow?.DataContext as ShellViewModel)?.Toaster.Show($"Something went wrong: {e.Exception.Message}", isError: true);
        e.Handled = true;
    }

    private static void LogError(Exception exception)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProGuard");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "ui-errors.log"), $"{DateTime.Now:O} {exception}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
