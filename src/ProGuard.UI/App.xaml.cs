using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProGuard.Shared.Client;
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
    // Foreground Boost and IdleSaver all depend on this UI reporting the foreground process and idle time.
    private readonly ForegroundReporter _foregroundReporter = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Started at sign-in (see RunKeyStartup): go straight to the tray rather than opening a window.
        bool trayStart = e.Args.Contains(RunKeyStartup.TrayArgument, StringComparer.OrdinalIgnoreCase);

        // A copy already runs in this session: it shows its window (unless this is the sign-in start) and this one leaves.
        if (!SingleInstance.TryClaim(() => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront()), out _instance, askToShow: !trayStart))
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        SessionEnding += (_, _) => { if (MainWindow is MainWindow w) w.IsQuitting = true; };

        var toaster = new Toaster(Dispatcher);
        var client = new EngineClient();
        _hub = new EngineHub(client, Dispatcher, toaster);
        var shell = new ShellViewModel(_hub, toaster, new IconCache(Dispatcher));

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
