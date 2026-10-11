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
    private bool isResponsivenessGuardEnabled = true;

    [ObservableProperty]
    private int sensitivity = 5;

    [ObservableProperty]
    private int cpuThreshold = 25;

    [ObservableProperty]
    private bool isMemoryTrimEnabled = false;

    [ObservableProperty]
    private int memory trimIntervalSeconds = 300;

    [ObservableProperty]
    private long memory trimThresholdMb = 1024;

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
            db.EnsureSchema();
            var s = db.Settings.FirstOrDefault();
            if (s == null)
            {
                s = new AppSettings { Id = 1 };
                db.Settings.Add(s);
                db.SaveChanges();
            }

            IsResponsivenessGuardEnabled = s.ResponsivenessGuardEnabled;
            Sensitivity = s.ResponsivenessGuardSensitivity;
            CpuThreshold = (int)s.ResponsivenessGuardCpuThreshold;
            IsMemoryTrimEnabled = s.MemoryTrimEnabled;
            MemoryTrimIntervalSeconds = s.MemoryTrimIntervalSeconds;
            MemoryTrimThresholdMb = s.MemoryTrimMemoryThresholdMB;
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
            db.EnsureSchema();
            var s = db.Settings.FirstOrDefault();
            if (s != null)
            {
                s.ResponsivenessGuardEnabled = IsResponsivenessGuardEnabled;
                s.ResponsivenessGuardSensitivity = Sensitivity;
                s.ResponsivenessGuardCpuThreshold = CpuThreshold;
                s.MemoryTrimEnabled = IsMemoryTrimEnabled;
                s.MemoryTrimIntervalSeconds = MemoryTrimIntervalSeconds;
                s.MemoryTrimMemoryThresholdMB = MemoryTrimThresholdMb;
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

    partial void OnIsResponsivenessGuardEnabledChanged(bool value) => SaveSettings();
    partial void OnSensitivityChanged(int value) => SaveSettings();
    partial void OnCpuThresholdChanged(int value) => SaveSettings();
    partial void OnIsMemoryTrimEnabledChanged(bool value) => SaveSettings();
    partial void OnMemoryTrimIntervalSecondsChanged(int value) => SaveSettings();
    partial void OnMemoryTrimThresholdMbChanged(long value) => SaveSettings();
    partial void OnStartWithWindowsChanged(bool value) => SaveSettings();
    partial void OnMinimizeToTrayChanged(bool value) => SaveSettings();
    partial void OnSelectedThemeIndexChanged(int value) => SaveSettings();
}
