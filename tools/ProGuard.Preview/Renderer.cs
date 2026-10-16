using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Client;
using ProGuard.UI;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Preview;

internal enum RenderMode { Admin, Standard, Offline }

internal sealed record RenderOptions(string OutDir, IReadOnlyList<RenderMode> Modes, int Width, int Height, double Scale);

/// <summary>
/// Drives the real main window, off-screen, against a <see cref="StandInEngine"/> and writes PNGs. Pages come from the
/// shell's own navigation (<see cref="ShellViewModel.Pages"/>). Dialogs and editors are found by running, on each page's
/// view model, the commands whose names say they open something (<see cref="Openers"/>) and noting whether the shell
/// then has a dialog or any page has an editor open. Nothing here knows what a page looks like, so pages and dialogs
/// that other people add are picked up as long as they follow the same shape.
/// </summary>
internal sealed class Renderer
{
    /// <summary>
    /// Commands run to find dialogs and editors, by name (without the "Command" suffix). A deliberate allow-list:
    /// anything else may do real work (OpenLocation starts Explorer), and a missed dialog is better than a surprise.
    /// Extend it when a page gets a differently named opener.
    /// </summary>
    private static readonly Regex Openers = new(
        @"^(Edit|New|Add|Make|Configure|Delete|Remove|End|TrimAll|Show|Choose|Pick|Rename|Clone|Duplicate)", RegexOptions.Compiled);

    private const int MaxParametersPerCommand = 3;

    private readonly RenderOptions _options;
    private readonly App _app;
    private readonly IconCache _icons;
    private readonly BindingWarnings _bindingWarnings = new();
    private readonly List<string> _problems = new();
    private readonly List<string> _written = new();

    // Per mode
    private RenderMode _mode;
    private string _modeDir = string.Empty;
    private MainWindow _window = null!;
    private ShellViewModel _shell = null!;
    private EngineHub _hub = null!;
    private string _step = string.Empty;

    public Renderer(RenderOptions options)
    {
        _options = options;
        _app = new App();
        _app.InitializeComponent();
        _app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Outside a running frame there is no context, and async commands would then finish on a pool thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(_app.Dispatcher));
        _app.DispatcherUnhandledException += (_, e) =>
        {
            Problem($"unhandled exception: {e.Exception}");
            e.Handled = true;
        };
        _icons = new IconCache(_app.Dispatcher);
        _bindingWarnings.Attach();
    }

    public int Run()
    {
        foreach (var mode in _options.Modes)
        {
            try
            {
                RenderOne(mode);
            }
            catch (Exception ex)
            {
                Problem($"[{mode}] {_step}: {ex}");
            }
        }

        _app.Shutdown();

        Console.WriteLine();
        Console.WriteLine($"{_written.Count} image(s) written under {_options.OutDir}");
        foreach (var line in _bindingWarnings.Summary()) Console.WriteLine("  binding: " + line);
        foreach (var problem in _problems) Console.Error.WriteLine("  PROBLEM: " + problem);
        return _problems.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ one mode

    private void RenderOne(RenderMode mode)
    {
        _mode = mode;
        _modeDir = Path.Combine(_options.OutDir, mode.ToString().ToLowerInvariant());
        Directory.CreateDirectory(_modeDir);
        foreach (var old in Directory.EnumerateFiles(_modeDir, "*.png")) File.Delete(old);
        Console.WriteLine($"== {mode}");

        var pipe = $"ProGuard.Preview.{Environment.ProcessId}.{mode}";
        StandInEngine? engine = mode == RenderMode.Offline ? null : new StandInEngine(pipe, callerIsAdministrator: mode == RenderMode.Admin, readOnly: true).Start();

        var toaster = new Toaster(_app.Dispatcher);
        _hub = new EngineHub(new EngineClient(pipe), _app.Dispatcher, toaster);
        _shell = new ShellViewModel(_hub, toaster, _icons);
        _window = new MainWindow
        {
            DataContext = _shell,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = 0,
            Width = _options.Width,
            Height = _options.Height,
            ShowActivated = false,
            ShowInTaskbar = false,
        };

        try
        {
            _window.Show();
            _hub.Start();
            WarmUp();

            foreach (var page in _shell.Pages.ToList())
            {
                string prefix = $"{page.Number}-{Slug(page.Title)}";
                Step(prefix, () =>
                {
                    _shell.CurrentPage = page;
                    Pump(TimeSpan.FromSeconds(0.8));
                    Shot(prefix);
                    ShotScrolled(prefix);
                });
                if (mode == RenderMode.Offline) continue;
                Step(prefix + " (details)", () => ExercisePage(page, prefix));
            }

            if (mode != RenderMode.Offline) Step("toasts", ShowToasts);
        }
        finally
        {
            _window.Close();
            _hub.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            engine?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Polls as fast as the engine answers until the charts have a full history (or, offline, the failure has shown).</summary>
    private void WarmUp()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            bool ready = _mode == RenderMode.Offline
                ? _hub.IsOffline
                : _hub.CpuHistory.Length > 0 && !_hub.CpuHistory.Any(double.IsNaN) && !_hub.MemoryHistory.Any(double.IsNaN);
            if (ready) break;
            _hub.RefreshNow();
            Pump(TimeSpan.FromMilliseconds(40));
        }
        Pump(TimeSpan.FromSeconds(1.5)); // icons arrive on a background thread
    }

    private void ShowToasts()
    {
        _shell.CurrentPage = _shell.Pages[0];
        Pump(TimeSpan.FromMilliseconds(600));
        _shell.Toaster.Show("Discord.exe capped at 10%");
        _shell.Toaster.Show("RULE SAVED · chrome.exe");
        _shell.Toaster.Show("Windows denied access to lsass. Run ProGuard as administrator to change system processes.", isError: true);
        Shot("99-toasts", keepToasts: true);
    }

    // ------------------------------------------------------------------ dialogs and editors

    private void ExercisePage(PageItem page, string prefix)
    {
        if (page.Page is AdvancedViewModel advanced)
        {
            ExerciseAdvanced(advanced, prefix);
            return;
        }
        if (page.Page is not ProcessesViewModel processes)
        {
            RunOpeners(page, prefix, onlyMatching: null, tryConfirm: false);
            CaptureContextMenus(prefix);
            return;
        }

        var app = processes.Rows.FirstOrDefault(r => r.Name.StartsWith("Discord", StringComparison.OrdinalIgnoreCase) && r.CpuLimitPercent.HasValue)
            ?? processes.Rows.FirstOrDefault(r => !r.IsSystem);
        var system = processes.Rows.FirstOrDefault(r => r.Name.StartsWith("lsass", StringComparison.OrdinalIgnoreCase))
            ?? processes.Rows.FirstOrDefault(r => r.IsSystem);

        if (app != null)
        {
            processes.Selected = app;
            Pump(TimeSpan.FromMilliseconds(500));
            Shot(prefix + "--selected");
            CaptureContextMenus(prefix);
            RunOpeners(page, prefix, onlyMatching: null, tryConfirm: false);
        }
        if (system != null)
        {
            // Ending a system process asks first; on a standard user's engine, going ahead is refused.
            processes.Selected = system;
            Pump(TimeSpan.FromMilliseconds(500));
            RunOpeners(page, prefix + "--system", onlyMatching: new Regex("^End"), tryConfirm: true);
        }
        processes.Selected = null;
    }

    /// <summary>Each ADVANCED sub-section, and for CPU TUNING the battery values too.</summary>
    private void ExerciseAdvanced(AdvancedViewModel advanced, string prefix)
    {
        foreach (var section in advanced.Sections)
        {
            string name = $"{prefix}--{Slug(section.Label)}";
            advanced.CurrentSection = section;
            Pump(TimeSpan.FromSeconds(0.8));
            Shot(name);
            if (section.Page is not CpuTuningViewModel tuning)
            {
                ShotScrolled(name);
                continue;
            }

            // A long page of settings: every screenful, plugged in, then the settings again on battery.
            ShotPaged(name);
            tuning.IsDc = true;
            Pump(TimeSpan.FromMilliseconds(400));
            ShotPaged(name + "--battery");
            tuning.IsDc = false;
        }
        advanced.CurrentSection = advanced.Sections[0];
    }

    /// <summary>Runs each opener command (with a few sample parameters if it takes one) and renders whatever it opens.</summary>
    private void RunOpeners(PageItem page, string prefix, Regex? onlyMatching, bool tryConfirm)
    {
        var owner = page.Page;
        var commands = owner.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => (Name: p.Name.EndsWith("Command", StringComparison.Ordinal) ? p.Name[..^"Command".Length] : p.Name, Command: p.GetValue(owner) as ICommand))
            .Where(c => c.Command != null && Openers.IsMatch(c.Name) && (onlyMatching?.IsMatch(c.Name) ?? true))
            .ToList();

        foreach (var (name, command) in commands)
        {
            // A confirmation looks the same whichever row it is for, so one sample is enough there.
            int samples = name.StartsWith("Delete", StringComparison.Ordinal) || name.StartsWith("Remove", StringComparison.Ordinal) ? 1 : MaxParametersPerCommand;
            var parameters = ParametersFor(owner, command!).Take(samples).ToList();
            for (int i = 0; i < parameters.Count; i++)
            {
                string label = $"{prefix}--{name}{(parameters.Count > 1 ? "-" + (i + 1) : "")}";
                var parameter = parameters[i];
                bool first = i == 0;
                Step(label, () => Open(page, command!, parameter, label, tryConfirm, first));
            }
        }
    }

    private void Open(PageItem page, ICommand command, object? parameter, string label, bool tryConfirm, bool first)
    {
        if (!command.CanExecute(parameter))
        {
            Console.WriteLine($"   skipped {label}: not available right now");
            return;
        }

        command.Execute(parameter);
        Pump(TimeSpan.FromMilliseconds(450));

        var editors = OpenEditors().ToList();
        if (_shell.Dialog == null && editors.Count == 0)
        {
            Console.WriteLine($"   {label}: opened nothing");
            return;
        }

        Shot(label);
        if (first && _shell.Dialog == null) ShotScrolled(label); // behind a dialog, scrolling shows nothing new

        if (_mode == RenderMode.Standard && first)
        {
            // Go ahead the way a standard user would, to see how the refusal reads.
            ICommand? go = null;
            if (_shell.Dialog != null && tryConfirm) go = _shell.Dialog.GetType().GetProperty("ConfirmCommand")?.GetValue(_shell.Dialog) as ICommand;
            else if (_shell.Dialog == null) go = editors.Select(e => e.Owner.GetType().GetProperty("SaveCommand")?.GetValue(e.Owner) as ICommand).FirstOrDefault(c => c != null);
            if (go != null && go.CanExecute(null))
            {
                go.Execute(null);
                Pump(TimeSpan.FromMilliseconds(900));
                Shot(label + "-denied", keepToasts: true);
            }
        }

        CloseEverything(page);
    }

    /// <summary>Editors are properties named Editor on the page view models; open when not null.</summary>
    private IEnumerable<(object Owner, PropertyInfo Property)> OpenEditors()
    {
        foreach (var item in _shell.Pages)
        {
            foreach (var property in item.Page.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.Name.EndsWith("Editor", StringComparison.Ordinal) && property.GetIndexParameters().Length == 0 && property.GetValue(item.Page) != null)
                {
                    yield return (item.Page, property);
                }
            }
        }
    }

    private void CloseEverything(PageItem back)
    {
        _shell.CloseDialog();
        foreach (var (owner, property) in OpenEditors().ToList())
        {
            if (property.CanWrite) property.SetValue(owner, null);
        }
        _shell.Toaster.Items.Clear();
        _shell.CurrentPage = back;
        Pump(TimeSpan.FromMilliseconds(400));
    }

    /// <summary>Sample arguments for a command that takes one: a few items from a collection on the view model that holds that kind of item.</summary>
    private static IEnumerable<object?> ParametersFor(object owner, ICommand command)
    {
        var parameterType = command.GetType().GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRelayCommand<>))
            .Select(i => i.GetGenericArguments()[0])
            .FirstOrDefault();
        if (parameterType == null)
        {
            yield return null;
            yield break;
        }

        foreach (var property in owner.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(property.PropertyType)) continue;
            var items = (property.GetValue(owner) as IEnumerable)?.Cast<object>().Where(parameterType.IsInstanceOfType).ToList();
            if (items == null || items.Count == 0) continue;

            // Spread the samples over the list rather than taking the first few.
            int take = Math.Min(MaxParametersPerCommand, items.Count);
            foreach (var index in Enumerable.Range(0, take).Select(k => take == 1 ? 0 : k * (items.Count - 1) / (take - 1)).Distinct())
            {
                yield return items[index];
            }
            yield break;
        }
    }

    // ------------------------------------------------------------------ context menus

    /// <summary>
    /// Renders the context menus found on the current page. A menu in a popup would be a separate window that WPF
    /// moves on-screen, so the menu is laid out in place instead and drawn on its own.
    /// </summary>
    private void CaptureContextMenus(string prefix)
    {
        var owners = Descendants(_window).OfType<FrameworkElement>().Where(e => e.ContextMenu is { HasItems: true }).Take(3).ToList();
        for (int i = 0; i < owners.Count; i++)
        {
            string name = $"{prefix}--menu{(owners.Count > 1 ? "-" + (i + 1) : "")}";
            var owner = owners[i];
            Step(name, () =>
            {
                var menu = owner.ContextMenu!;
                menu.PlacementTarget = owner;
                menu.DataContext = owner.DataContext;
                menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = menu.DesiredSize;
                if (size.Width < 1 || size.Height < 1) return;
                menu.Arrange(new Rect(size));
                menu.UpdateLayout();
                SavePng(name, Capture(menu, size.Width, size.Height));
            });
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    // ------------------------------------------------------------------ images

    private void Shot(string name, bool keepToasts = false)
    {
        Pump(TimeSpan.FromMilliseconds(250));
        if (!keepToasts)
        {
            _shell.Toaster.Items.Clear();
            Pump(TimeSpan.FromMilliseconds(60));
        }
        _window.UpdateLayout();
        SavePng(name, Capture(_window, _window.ActualWidth, _window.ActualHeight));
    }

    /// <summary>For each region that scrolls (a long page, the process list, an editor panel), a shot with it scrolled to its end.</summary>
    private void ShotScrolled(string label)
    {
        var regions = Descendants(_window).OfType<ScrollViewer>().Where(s => s.IsVisible && s.ScrollableHeight > 8).Take(3).ToList();
        for (int i = 0; i < regions.Count; i++)
        {
            regions[i].ScrollToEnd();
            Shot($"{label}--scrolled{(regions.Count > 1 ? "-" + (i + 1) : "")}");
            regions[i].ScrollToHome();
        }
    }

    /// <summary>The page's scrolling region a screenful at a time below the first (up to six in all), back at the top afterwards.</summary>
    private void ShotPaged(string label)
    {
        var region = Descendants(_window).OfType<ScrollViewer>().FirstOrDefault(s => s.IsVisible && s.ScrollableHeight > 8);
        if (region == null) return;
        int pages = Math.Min(6, (int)Math.Ceiling(region.ExtentHeight / Math.Max(1, region.ViewportHeight)));
        for (int i = 1; i < pages; i++)
        {
            region.ScrollToVerticalOffset(i * region.ViewportHeight);
            Shot($"{label}--page{i + 1}");
        }
        region.ScrollToHome();
    }

    private BitmapSource Capture(Visual visual, double width, double height)
    {
        double scale = _options.Scale;
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private void SavePng(string name, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var path = Path.Combine(_modeDir, name + ".png");
        using (var file = File.Create(path)) encoder.Save(file);
        _written.Add(path);
        Console.WriteLine($"   {_mode.ToString().ToLowerInvariant()}/{name}.png");
    }

    // ------------------------------------------------------------------ plumbing

    private void Step(string name, Action action)
    {
        _step = name;
        _bindingWarnings.Context = $"{_mode}/{name}";
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Problem($"[{_mode}] {name}: {ex.Message}");
            try { CloseEverything(_shell.CurrentPage); } catch (Exception) { /* keep going */ }
        }
    }

    private void Problem(string text)
    {
        _problems.Add(text);
        Console.Error.WriteLine("   PROBLEM: " + text);
    }

    private static string Slug(string title) => Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        // Let layout, bindings and animations that were queued meanwhile finish before anyone looks.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>Collects WPF's data-binding warnings (a binding path that doesn't exist, say), which are invisible otherwise.</summary>
    private sealed class BindingWarnings : TraceListener
    {
        private readonly Dictionary<string, (int Count, string Where)> _seen = new();
        private readonly StringBuilder _line = new();

        public string Context { get; set; } = string.Empty;

        public void Attach()
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            PresentationTraceSources.DataBindingSource.Listeners.Add(this);
        }

        public override void Write(string? message) => _line.Append(message);

        public override void WriteLine(string? message)
        {
            _line.Append(message);
            var text = _line.ToString().Trim();
            _line.Clear();
            if (text.Length == 0) return;
            lock (_seen)
            {
                _seen[text] = _seen.TryGetValue(text, out var existing) ? (existing.Count + 1, existing.Where) : (1, Context);
            }
        }

        public IEnumerable<string> Summary()
        {
            lock (_seen)
            {
                return _seen.Select(kv => $"{kv.Value.Count}x (first in {kv.Value.Where}) {kv.Key}").ToList();
            }
        }
    }
}
