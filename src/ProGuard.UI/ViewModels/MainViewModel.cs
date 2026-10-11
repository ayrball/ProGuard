using System;
using System.Diagnostics;
using System.Linq;
using System.Timers;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;

namespace ProGuard.UI.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private static readonly SolidColorBrush GreenBrush = CreateFrozenBrush(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush BlueBrush = CreateFrozenBrush(Color.FromRgb(59, 130, 246));

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private readonly System.Timers.Timer _timer;
    private int _engineCheckCounter;
    private bool _cachedEngineRunning;
    private bool _disposed;

    [ObservableProperty]
    private SolidColorBrush engineStatusColor = BlueBrush;

    [ObservableProperty]
    private string engineStatusText = "ProGuard Active";

    [ObservableProperty]
    private string globalCpuText = "CPU: 0%";

    [ObservableProperty]
    private string globalRamText = "RAM: 0%";

    [ObservableProperty]
    private string proBalanceStatus = "ProBalance Active";

    public MainViewModel()
    {
        UpdateMetrics();

        _timer = new System.Timers.Timer(1500);
        _timer.Elapsed += (s, e) => UpdateMetrics();
        _timer.Start();
    }

    private void UpdateMetrics()
    {
        double cpu = SystemInfo.GetOverallCpuPercent();
        double ram = SystemInfo.GetMemoryLoadPercent();

        if (_engineCheckCounter++ % 6 == 0)
        {
            try
            {
                _cachedEngineRunning = Process.GetProcessesByName("ProGuard.Engine").Length > 0;
            }
            catch { }
        }

        string cpuText = $"CPU: {cpu:0}%";
        string ramText = $"RAM: {ram:0}%";
        string statusText = _cachedEngineRunning ? "Engine Active" : "Active (Integrated)";
        SolidColorBrush statusColor = _cachedEngineRunning ? GreenBrush : BlueBrush;

        void Apply()
        {
            if (GlobalCpuText != cpuText) GlobalCpuText = cpuText;
            if (GlobalRamText != ramText) GlobalRamText = ramText;
            if (EngineStatusText != statusText) EngineStatusText = statusText;
            if (!ReferenceEquals(EngineStatusColor, statusColor)) EngineStatusColor = statusColor;
        }

        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            try
            {
                Application.Current.Dispatcher.Invoke(Apply);
            }
            catch { }
        }
        else
        {
            Apply();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _timer?.Stop();
            _timer?.Dispose();
        }
    }
}
