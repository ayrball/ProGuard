using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.Defaults;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Timers;
using System;
using System.Windows;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;

namespace ProGuard.UI.ViewModels;

public partial class DashboardViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty] private string cpuUsageText = "0%";
    [ObservableProperty] private string ramUsageText = "0%";
    [ObservableProperty] private string proBalanceCount = "0";
    [ObservableProperty] private string activeRulesCount = "0";
    [ObservableProperty] private bool isActive = true;

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
        {
            _ = Task.Run(UpdateDashboardMetrics);
        }
    }

    public ObservableCollection<ISeries> CpuSeries { get; set; }
    public ObservableCollection<ISeries> RamSeries { get; set; }
    public Axis[] XAxes { get; set; }
    public Axis[] YAxes { get; set; }

    public ObservableCollection<TopConsumerItem> TopConsumers { get; set; } = new();

    private readonly ObservableCollection<ObservableValue> _cpuValues = new();
    private readonly ObservableCollection<ObservableValue> _ramValues = new();
    private readonly CpuUsageTracker _cpuTracker = new();
    private readonly System.Timers.Timer _timer;
    private static bool s_dbInitialized;
    private int _dbCheckCounter;
    private int _cachedRulesCount;
    private int _cachedProBalanceCount;
    private bool _disposed;

    public DashboardViewModel()
    {
        for (int i = 0; i < 60; i++)
        {
            _cpuValues.Add(new ObservableValue(0));
            _ramValues.Add(new ObservableValue(0));
        }

        CpuSeries = new ObservableCollection<ISeries>
        {
            new LineSeries<ObservableValue>
            {
                Values = _cpuValues,
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0.5
            }
        };

        RamSeries = new ObservableCollection<ISeries>
        {
            new LineSeries<ObservableValue>
            {
                Values = _ramValues,
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0.5
            }
        };

        XAxes = new Axis[] { new Axis { IsVisible = false } };
        YAxes = new Axis[] { new Axis { MinLimit = 0, MaxLimit = 100 } };

        // Initial populate so collections are not empty
        UpdateDashboardMetrics();

        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += Timer_Elapsed;
        _timer.Start();
    }

    private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        if (IsActive)
        {
            UpdateDashboardMetrics();
        }
    }

    private void UpdateDashboardMetrics()
    {
        double cpu = SystemInfo.GetOverallCpuPercent();
        double ram = SystemInfo.GetMemoryLoadPercent();

        if (!s_dbInitialized || (_dbCheckCounter++ % 10 == 0))
        {
            try
            {
                using var db = new ProGuardDbContext();
                if (!s_dbInitialized)
                {
                    db.Database.EnsureCreated();
                    s_dbInitialized = true;
                }
                _cachedRulesCount = db.ProcessRules.Count();
                _cachedProBalanceCount = db.ProBalanceActions.Count();
            }
            catch { }
        }

        int rulesCount = _cachedRulesCount;
        int proBalanceCount = _cachedProBalanceCount;

        var consumers = SampleTopConsumers();

        void ApplyUpdate()
        {
            CpuUsageText = $"{cpu:0}%";
            RamUsageText = $"{ram:0}%";
            ActiveRulesCount = rulesCount.ToString();
            ProBalanceCount = proBalanceCount.ToString();

            if (_cpuValues.Count >= 60) _cpuValues.RemoveAt(0);
            _cpuValues.Add(new ObservableValue(cpu));

            if (_ramValues.Count >= 60) _ramValues.RemoveAt(0);
            _ramValues.Add(new ObservableValue(ram));

            TopConsumers.Clear();
            foreach (var item in consumers)
            {
                TopConsumers.Add(item);
            }
        }

        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            try
            {
                Application.Current.Dispatcher.Invoke(ApplyUpdate);
            }
            catch { }
        }
        else
        {
            ApplyUpdate();
        }
    }

    private List<TopConsumerItem> SampleTopConsumers()
    {
        var list = new List<TopConsumerItem>();
        try
        {
            var processes = Process.GetProcesses();
            var tracked = new List<(string Name, int Pid, double Cpu, long Memory)>();

            foreach (var p in processes)
            {
                try
                {
                    if (p.Id == 0 || p.Id == 4) continue;
                    double cpu = _cpuTracker.GetCpuUsage(p);
                    tracked.Add((p.ProcessName + ".exe", p.Id, cpu, p.WorkingSet64));
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            var sorted = tracked.OrderByDescending(t => t.Cpu).ThenByDescending(t => t.Memory).Take(5);
            foreach (var item in sorted)
            {
                list.Add(new TopConsumerItem
                {
                    Name = item.Name,
                    Pid = item.Pid,
                    CpuPercent = $"{item.Cpu:0.0}%"
                });
            }
        }
        catch { }

        return list;
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

public class TopConsumerItem
{
    public string Name { get; set; } = string.Empty;
    public int Pid { get; set; }
    public string CpuPercent { get; set; } = string.Empty;
}
