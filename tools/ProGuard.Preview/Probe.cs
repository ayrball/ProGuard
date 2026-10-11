using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Preview;

/// <summary>
/// Against a running engine: opens the Processes page and measures UI-thread stalls while live data streams in, plus
/// the latency of selecting a row and opening its context menu (what a right-click does).
/// </summary>
internal static class Probe
{
    public static int Run(string pipe)
    {
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));

        var toaster = new Toaster(app.Dispatcher);
        var client = new EngineClient(pipe);
        var hub = new EngineHub(client, app.Dispatcher, toaster);
        var shell = new ShellViewModel(hub, toaster, new IconCache(app.Dispatcher));
        var window = new MainWindow
        {
            DataContext = shell,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, Width = 1440, Height = 900, ShowActivated = false, ShowInTaskbar = false
        };
        window.Show();
        hub.Start();
        shell.CurrentPage = shell.Pages.First(p => p.Page == shell.Processes);
        Pump(TimeSpan.FromSeconds(4));
        if (!hub.IsOnline)
        {
            Console.WriteLine("engine not reachable on pipe " + pipe);
            return 2;
        }

        // Measure only: keep this engine from changing any real process's priority meanwhile, then put the settings back.
        var original = Task.Run(() => client.QueryAsync<EngineSettingsDto>(CommandTypes.GetSettings)).GetAwaiter().GetResult();
        var quiet = DtoClone(original);
        quiet.ProBalanceEnabled = false;
        quiet.ForegroundBoostEnabled = false;
        quiet.SmartTrimEnabled = false;
        Task.Run(() => client.CommandAsync(CommandTypes.SaveSettings, argument: quiet)).GetAwaiter().GetResult();

        try
        {
            Measure(app, window, shell);
        }
        finally
        {
            Task.Run(() => client.CommandAsync(CommandTypes.SaveSettings, argument: original)).GetAwaiter().GetResult();
            window.Close();
            app.Shutdown();
        }
        return 0;
    }

    private static void Measure(App app, Window window, ShellViewModel shell)
    {
        // Stall probe: how long does an Input-priority no-op wait for the UI thread?
        var stalls = new List<double>();
        var dispatcher = app.Dispatcher;
        bool probing = true;
        var probeThread = new Thread(() =>
        {
            var sw = new Stopwatch();
            while (Volatile.Read(ref probing))
            {
                sw.Restart();
                dispatcher.Invoke(() => { }, DispatcherPriority.Input);
                lock (stalls) stalls.Add(sw.Elapsed.TotalMilliseconds);
                Thread.Sleep(20);
            }
        }) { IsBackground = true };
        probeThread.Start();

        // Right-click simulation, spread over the live list, while snapshots keep arriving.
        var table = FindChild<ListBox>(window, "Table")!;
        var menu = table.ContextMenu!;
        var clicks = new List<double>();
        var rng = new Random(3);
        for (int i = 0; i < 12; i++)
        {
            var rows = shell.Processes.Rows;
            var row = rows[rng.Next(rows.Count)];
            var sw = Stopwatch.StartNew();
            shell.Processes.Selected = row;
            menu.PlacementTarget = table;
            menu.IsOpen = true;
            dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); // menu laid out and rendered
            clicks.Add(sw.Elapsed.TotalMilliseconds);
            Pump(TimeSpan.FromMilliseconds(250));
            menu.IsOpen = false;
            Pump(TimeSpan.FromMilliseconds(600));
        }

        Pump(TimeSpan.FromSeconds(3));
        Volatile.Write(ref probing, false);
        probeThread.Join();

        static string Stats(List<double> v)
        {
            var s = v.OrderBy(x => x).ToList();
            double P(double q) => s[Math.Min(s.Count - 1, (int)(q * s.Count))];
            return $"n={s.Count} median={P(0.5):0.0}ms p95={P(0.95):0.0}ms max={s[^1]:0.0}ms >50ms={s.Count(x => x > 50)} >100ms={s.Count(x => x > 100)}";
        }

        Console.WriteLine($"rows on screen: {shell.Processes.Rows.Count} of {shell.Processes.TotalCount}");
        lock (stalls) Console.WriteLine($"UI-thread stalls while live: {Stats(stalls)}");
        Console.WriteLine($"right-click to menu shown:   {Stats(clicks)} (first {clicks[0]:0.0}ms)");
    }

    private static EngineSettingsDto DtoClone(EngineSettingsDto source) =>
        System.Text.Json.JsonSerializer.Deserialize<EngineSettingsDto>(System.Text.Json.JsonSerializer.Serialize(source))!;

    private static T? FindChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            if (FindChild<T>(child, name) is { } found) return found;
        }
        return null;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
