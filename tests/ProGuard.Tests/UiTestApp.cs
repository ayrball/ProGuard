using System.Windows;
using System.Windows.Threading;

namespace ProGuard.Tests;

/// <summary>
/// One UI thread for every test that creates WPF views. A process may have only one <see cref="Application"/>, and
/// its resources belong to the thread that created it: views built on another thread look resources up through that
/// thread's dispatcher, which hangs once the thread has finished. So the application lives on a single STA thread
/// whose dispatcher keeps running, and view tests run their work there with <see cref="Run"/>.
/// </summary>
internal static class UiTestApp
{
    private static readonly Lazy<Dispatcher> UiThread = new(StartUiThread, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Dispatcher StartUiThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            // App.xaml's resources (theme, converters) are what the views look up.
            var app = new ProGuard.UI.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true, // never keeps the test host alive
            Name = "UI test thread"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    /// <summary>The application, for adding resources from inside <see cref="Run"/>.</summary>
    public static Application Ensure() => UiThread.Value.Invoke(() => Application.Current);

    /// <summary>Runs <paramref name="body"/> on the UI thread and waits for it. Exceptions surface to the caller.</summary>
    public static void Run(Action body) => UiThread.Value.Invoke(body);
}
