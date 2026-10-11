using System;
using System.Diagnostics;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using ProGuard.Core.Helpers;
using ProGuard.Core.Native;
using Wpf.Ui.Tray.Controls;

namespace ProGuard.UI.Services;

public class TrayIconService : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private System.Timers.Timer? _metricsTimer;
    private MenuItem? _statusHeaderItem;
    private TrayBalloon? _balloon;
    private bool _disposed;

    public void Initialize()
    {
        var menu = new ContextMenu();
        
        _statusHeaderItem = new MenuItem 
        { 
            Header = "ProGuard | System Monitoring Active", 
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold
        };
        menu.Items.Add(_statusHeaderItem);
        menu.Items.Add(new Separator());

        var openItem = new MenuItem { Header = "Open ProGuard" };
        openItem.Click += (s, e) => ShowMainWindow();
        menu.Items.Add(openItem);
        
        menu.Items.Add(new Separator());
        
        var trimItem = new MenuItem { Header = "Flush Working Sets (SmartTrim All)" };
        trimItem.Click += (s, e) => FlushAllMemory();
        menu.Items.Add(trimItem);

        var perfItem = new MenuItem { Header = "Toggle Performance Mode" };
        perfItem.Click += (s, e) => TogglePerformanceMode();
        menu.Items.Add(perfItem);
        
        var proBalanceItem = new MenuItem { Header = "Toggle ProBalance" };
        menu.Items.Add(proBalanceItem);
        
        menu.Items.Add(new Separator());
        
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (s, e) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            FocusOnLeftClick = true,
            MenuOnRightClick = true,
            TooltipText = "ProGuard - Process Optimizer",
            Menu = menu
        };

#pragma warning disable CS8622
        _notifyIcon.LeftDoubleClick += (sender, e) => ShowMainWindow();
#pragma warning restore CS8622
        _notifyIcon.Register();

        // Real-Time Tooltip & Menu Status Updates
        _metricsTimer = new System.Timers.Timer(2000);
        _metricsTimer.Elapsed += (s, e) => UpdateMetrics();
        _metricsTimer.Start();
        UpdateMetrics();
    }

    private void UpdateMetrics()
    {
        if (_disposed) return;
        try
        {
            double cpu = SystemInfo.GetOverallCpuPercent();
            double ram = SystemInfo.GetMemoryLoadPercent();
            string status = $"ProGuard | CPU: {cpu:0.0}% | RAM: {ram:0}%";

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.TooltipText = status;
                }
                if (_statusHeaderItem != null)
                {
                    _statusHeaderItem.Header = status;
                }
            });
        }
        catch { }
    }

    private void FlushAllMemory()
    {
        try
        {
            long reclaimed = 0;
            var processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    long before = p.WorkingSet64;
                    if (Kernel32.EmptyWorkingSet(p.Handle))
                    {
                        p.Refresh();
                        reclaimed += Math.Max(0, before - p.WorkingSet64);
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
            MessageBox.Show($"SmartTrim flushed system working sets!\nReclaimed {ProcessExtensions.ToReadableSize(reclaimed)} RAM across active processes.", "SmartTrim Global Flush", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { }
    }

    private void TogglePerformanceMode()
    {
        try
        {
            Powrprof.PowerGetActiveScheme(IntPtr.Zero, out var activeGuidPtr);
            if (activeGuidPtr != IntPtr.Zero)
            {
                var activeGuid = System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(activeGuidPtr);
                Kernel32.LocalFree(activeGuidPtr);
                
                var targetGuid = activeGuid == Powrprof.HighPerformance 
                    ? Powrprof.Balanced 
                    : Powrprof.HighPerformance;

                Powrprof.PowerSetActiveScheme(IntPtr.Zero, ref targetGuid);
                string modeName = targetGuid == Powrprof.HighPerformance ? "Ultimate Performance" : "Balanced";
                MessageBox.Show($"Power Plan switched to: {modeName}", "Performance Mode", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch { }
    }

    private void ShowMainWindow()
    {
        var window = Application.Current.MainWindow;
        if (window != null)
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }
            window.Activate();
        }
    }

    /// <summary>Shows a tray notification. Safe to call from any thread.</summary>
    public void ShowNotification(string title, string text)
    {
        if (_disposed) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            try
            {
                _balloon ??= new TrayBalloon();
                _balloon.Show(title, text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tray notification failed: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _metricsTimer?.Stop();
            _metricsTimer?.Dispose();
            _notifyIcon?.Unregister();
            _notifyIcon = null;
            _balloon?.Dispose();
            _balloon = null;
        }
    }
}
