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

    // The engine runs as a service in session 0 and can't see this desktop: ProBalance's foreground exemption,
    // Foreground Boost and IdleSaver all depend on this UI reporting the foreground process and idle time.
    private readonly ForegroundReporter _foregroundReporter = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var toaster = new Toaster(Dispatcher);
        _hub = new EngineHub(new EngineClient(), Dispatcher, toaster);
        var shell = new ShellViewModel(_hub, toaster, new IconCache(Dispatcher));

        MainWindow = new MainWindow { DataContext = shell };
        MainWindow.Show();
        _hub.Start();
        _foregroundReporter.StartAsync(CancellationToken.None);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _foregroundReporter.Dispose();
        _hub?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive and tell the user, but leave a trace for diagnosis.
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProGuard");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "ui-errors.log"), $"{DateTime.Now:O} {e.Exception}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        (MainWindow?.DataContext as ShellViewModel)?.Toaster.Show($"Something went wrong: {e.Exception.Message}", isError: true);
        e.Handled = true;
    }
}
