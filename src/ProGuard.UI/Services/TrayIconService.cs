using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProGuard.Shared.Contracts;
using ProGuard.UI.ViewModels;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ProGuard.UI.Services;

/// <summary>
/// ProGuard's notification-area icon: click to show or hide the window, a menu for Performance Mode, Purge now and Exit,
/// and balloons for Watchdog events. WPF has no tray icon of its own, so this wraps WinForms' NotifyIcon (the one
/// place WinForms is used). Must be created and used on the UI thread, except <see cref="ShowBalloon"/>.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly Dispatcher _dispatcher;
    private readonly Action _toggleWindow;
    private readonly Action _showWindow;
    private readonly Action _exit;

    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _showItem;
    private readonly WinForms.ToolStripMenuItem _performanceItem;
    private readonly WinForms.ToolStripMenuItem _purgeItem;
    private bool _disposed;

    public TrayIconService(ShellViewModel shell, Dispatcher dispatcher, Action toggleWindow, Action showWindow, Action exit)
    {
        _shell = shell;
        _dispatcher = dispatcher;
        _toggleWindow = toggleWindow;
        _showWindow = showWindow;
        _exit = exit;

        _showItem = new WinForms.ToolStripMenuItem("SHOW PROGUARD", null, (_, _) => _toggleWindow());
        _performanceItem = new WinForms.ToolStripMenuItem("PERFORMANCE MODE", null, async (_, _) => await TogglePerformanceModeAsync()) { CheckOnClick = false };
        _purgeItem = new WinForms.ToolStripMenuItem("PURGE NOW", null, async (_, _) => await PurgeNowAsync());
        var exitItem = new WinForms.ToolStripMenuItem("EXIT", null, (_, _) => _exit());

        _menu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new VoltColors()),
            BackColor = VoltColors.Ink,
            ForeColor = VoltColors.Volt,
            Font = new Drawing.Font("Bahnschrift SemiBold", 10f),
            ShowImageMargin = false,
            ShowCheckMargin = true
        };
        _menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            _showItem, new WinForms.ToolStripSeparator(), _performanceItem, _purgeItem, new WinForms.ToolStripSeparator(), exitItem
        });
        _menu.Opening += (_, _) => RefreshMenu();

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "ProGuard",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) _toggleWindow();
        };
        _icon.DoubleClick += (_, _) => _showWindow();

        _shell.Hub.SnapshotApplied += OnSnapshot;
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/proguard-icon.ico"))?.Stream;
            if (stream != null)
            {
                using (stream) return new Drawing.Icon(stream);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { /* fall through */ }

        try
        {
            if (Environment.ProcessPath is { } path && Drawing.Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { /* fall through */ }

        return Drawing.SystemIcons.Application;
    }

    /// <summary>The menu as the engine's latest snapshot says it should be.</summary>
    private void RefreshMenu()
    {
        var window = Application.Current?.MainWindow;
        _showItem.Text = window is { IsVisible: true } ? "HIDE PROGUARD" : "SHOW PROGUARD";

        var snapshot = _shell.Hub.Snapshot;
        _performanceItem.Checked = snapshot?.PerformanceModeActive == true;
        _performanceItem.Enabled = snapshot != null;
        // Purge now needs administrator rights; the engine would refuse anyone else.
        _purgeItem.Enabled = snapshot?.CallerIsAdministrator == true;
        _purgeItem.ToolTipText = _purgeItem.Enabled ? string.Empty : "Needs administrator rights";
    }

    private void OnSnapshot(EngineSnapshot snapshot)
    {
        // The notification-area tooltip is limited to 63 characters.
        var text = $"PROGUARD · CPU {snapshot.CpuPercent:0}% · MEM {snapshot.MemoryPercent:0}%";
        if (_icon.Text != text) _icon.Text = text;
    }

    private async Task TogglePerformanceModeAsync()
    {
        if (await _shell.TogglePerformanceModeAsync() is bool on)
        {
            ShowBalloon("PERFORMANCE MODE", on ? "On." : "Off.");
        }
    }

    private async Task PurgeNowAsync()
    {
        if (await _shell.PurgeNowAsync() is { } result)
        {
            ShowBalloon("PURGE NOW", Fmt.PurgeSummary(result), warning: result.Errors.Count > 0);
        }
    }

    /// <summary>Shows a balloon (a toast on Windows 10 and 11). Safe to call from any thread.</summary>
    public void ShowBalloon(string title, string text, bool warning = false)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            try
            {
                _icon.ShowBalloonTip(6000, Truncate(string.IsNullOrWhiteSpace(title) ? "ProGuard" : title, 63),
                    Truncate(string.IsNullOrWhiteSpace(text) ? " " : text, 255),
                    warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tray balloon failed: {ex.Message}");
            }
        });
    }

    /// <summary>The balloon for a watchdog event.</summary>
    public void ShowWatchdogEvent(WatchdogEventDto e) =>
        ShowBalloon(e.Title, e.Message, warning: e.Kind == WatchdogEventKinds.Failed);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shell.Hub.SnapshotApplied -= OnSnapshot;
        _icon.Visible = false; // otherwise the icon lingers in the tray until the mouse passes over it
        _icon.Dispose();
        _menu.Dispose();
    }

    /// <summary>Ink and volt for the menu, like the rest of the app.</summary>
    private sealed class VoltColors : WinForms.ProfessionalColorTable
    {
        public static readonly Drawing.Color Ink = Drawing.Color.FromArgb(0x0A, 0x0A, 0x0B);
        public static readonly Drawing.Color Volt = Drawing.Color.FromArgb(0xDF, 0xFF, 0x20);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x2B, 0x31, 0x13);

        public VoltColors() => UseSystemColors = false;

        public override Drawing.Color ToolStripDropDownBackground => Ink;
        public override Drawing.Color MenuBorder => Volt;
        public override Drawing.Color MenuItemBorder => Volt;
        public override Drawing.Color MenuItemSelected => Line;
        public override Drawing.Color MenuItemSelectedGradientBegin => Line;
        public override Drawing.Color MenuItemSelectedGradientEnd => Line;
        public override Drawing.Color ImageMarginGradientBegin => Ink;
        public override Drawing.Color ImageMarginGradientMiddle => Ink;
        public override Drawing.Color ImageMarginGradientEnd => Ink;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Line;
        public override Drawing.Color CheckBackground => Line;
        public override Drawing.Color CheckSelectedBackground => Line;
        public override Drawing.Color CheckPressedBackground => Line;
    }
}
