using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ProGuard.Core.Data;
using ProGuard.Core.Models;

namespace ProGuard.UI.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private bool _loading;

    [ObservableProperty]
    private bool isProBalanceEnabled = true;

    [ObservableProperty]
    private int sensitivity = 5;

    [ObservableProperty]
    private int cpuThreshold = 25;

    [ObservableProperty]
    private bool isSmartTrimEnabled = false;

    [ObservableProperty]
    private int smartTrimIntervalSeconds = 300;

    [ObservableProperty]
    private long smartTrimThresholdMb = 1024;

    [ObservableProperty]
    private int selectedThemeIndex = 0;

    [ObservableProperty]
    private bool startWithWindows = true;

    [ObservableProperty]
    private bool minimizeToTray = true;

    public SettingsViewModel()
    {
        LoadSettings();
    }

    public void LoadSettings()
    {
        _loading = true;
        try
        {
            using var db = new ProGuardDbContext();
            db.Database.EnsureCreated();
            var s = db.Settings.FirstOrDefault();
            if (s == null)
            {
                s = new AppSettings { Id = 1 };
                db.Settings.Add(s);
                db.SaveChanges();
            }

            IsProBalanceEnabled = s.ProBalanceEnabled;
            Sensitivity = s.ProBalanceSensitivity;
            CpuThreshold = (int)s.ProBalanceCpuThreshold;
            IsSmartTrimEnabled = s.SmartTrimEnabled;
            SmartTrimIntervalSeconds = s.SmartTrimIntervalSeconds;
            SmartTrimThresholdMb = s.SmartTrimMemoryThresholdMB;
            StartWithWindows = s.StartWithWindows;
            MinimizeToTray = s.MinimizeToTray;

            SelectedThemeIndex = s.Theme switch
            {
                "Light" => 1,
                "System" => 2,
                _ => 0
            };
        }
        catch { }
        finally
        {
            _loading = false;
        }
    }

    private void SaveSettings()
    {
        if (_loading) return;

        try
        {
            using var db = new ProGuardDbContext();
            db.Database.EnsureCreated();
            var s = db.Settings.FirstOrDefault();
            if (s != null)
            {
                s.ProBalanceEnabled = IsProBalanceEnabled;
                s.ProBalanceSensitivity = Sensitivity;
                s.ProBalanceCpuThreshold = CpuThreshold;
                s.SmartTrimEnabled = IsSmartTrimEnabled;
                s.SmartTrimIntervalSeconds = SmartTrimIntervalSeconds;
                s.SmartTrimMemoryThresholdMB = SmartTrimThresholdMb;
                s.StartWithWindows = StartWithWindows;
                s.MinimizeToTray = MinimizeToTray;
                s.Theme = SelectedThemeIndex switch
                {
                    1 => "Light",
                    2 => "System",
                    _ => "Dark"
                };

                db.SaveChanges();
            }
        }
        catch { }
    }

    partial void OnIsProBalanceEnabledChanged(bool value) => SaveSettings();
    partial void OnSensitivityChanged(int value) => SaveSettings();
    partial void OnCpuThresholdChanged(int value) => SaveSettings();
    partial void OnIsSmartTrimEnabledChanged(bool value) => SaveSettings();
    partial void OnSmartTrimIntervalSecondsChanged(int value) => SaveSettings();
    partial void OnSmartTrimThresholdMbChanged(long value) => SaveSettings();
    partial void OnStartWithWindowsChanged(bool value) => SaveSettings();
    partial void OnMinimizeToTrayChanged(bool value) => SaveSettings();
    partial void OnSelectedThemeIndexChanged(int value) => SaveSettings();
}
