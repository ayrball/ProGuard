using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Shared.Messages;
using ProGuard.UI.IPC;

namespace ProGuard.UI.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly EngineProxy _engine;
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
    [NotifyPropertyChangedFor(nameof(MemoryTrimIntervalMinutes))]
    private int memory trimIntervalSeconds = 900;

    [ObservableProperty]
    private long memory trimThresholdMb = 196;

    [ObservableProperty]
    private int memory trimMinimumRamLoadPercent = 65;

    [ObservableProperty]
    private string memory trimExclusions = string.Empty;

    [ObservableProperty]
    private bool memory trimLogNoAction;

    [ObservableProperty]
    private bool isStandbyPurgeEnabled;

    [ObservableProperty]
    private long standbyPurgeFreeRamThresholdMb = 1024;

    [ObservableProperty]
    private bool standbyPurgeOnlyInPerformanceMode = true;

    [ObservableProperty]
    private bool isFileCachePurgeEnabled;

    /// <summary>Result of the last "Purge now", shown next to the button.</summary>
    [ObservableProperty]
    private string purgeResultText = string.Empty;

    [ObservableProperty]
    private int selectedThemeIndex = 0;

    [ObservableProperty]
    private bool startWithWindows = true;

    [ObservableProperty]
    private bool minimizeToTray = true;

    /// <summary>The page edits the interval in minutes, as another process manager does (MemoryTrimIntervalMins).</summary>
    public int MemoryTrimIntervalMinutes
    {
        get => Math.Max(1, MemoryTrimIntervalSeconds / 60);
        set => MemoryTrimIntervalSeconds = Math.Max(1, value) * 60;
    }

    public SettingsViewModel() : this(new EngineProxy()) { }

    public SettingsViewModel(EngineProxy engine)
    {
        _engine = engine;
        LoadSettings();
    }

    /// <summary>
    /// Asks the engine to trim working sets and purge the standby list (and the file cache, if enabled) now.
    /// The engine only accepts this from an administrator.
    /// </summary>
    [RelayCommand]
    private async Task PurgeNowAsync()
    {
        PurgeResultText = "Purging...";
        var (result, report) = await _engine.PurgeMemoryAsync();
        PurgeResultText = DescribePurge(result, report);
    }

    internal static string DescribePurge(CommandResult? result, MemoryPurgeReport? report)
    {
        if (result == null) return "The ProGuard engine is not running.";
        if (report == null) return result.Success ? "Done." : result.Error ?? "Purge failed.";
        var text = report.Summary();
        return report.AnyAction ? $"{text}. Reclaimed {MemoryPurgeReport.FormatMb(report.TotalBytesReclaimed)} in total." : text;
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
            MemoryTrimMinimumRamLoadPercent = s.MemoryTrimMinimumRamLoadPercent;
            MemoryTrimExclusions = s.MemoryTrimExclusions ?? string.Empty;
            MemoryTrimLogNoAction = s.MemoryTrimLogNoAction;
            IsStandbyPurgeEnabled = s.StandbyPurgeEnabled;
            StandbyPurgeFreeRamThresholdMb = s.StandbyPurgeFreeRamThresholdMB;
            StandbyPurgeOnlyInPerformanceMode = s.StandbyPurgeOnlyInPerformanceMode;
            IsFileCachePurgeEnabled = s.FileCachePurgeEnabled;
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
                s.MemoryTrimMinimumRamLoadPercent = Math.Clamp(MemoryTrimMinimumRamLoadPercent, 0, 100);
                s.MemoryTrimExclusions = string.IsNullOrWhiteSpace(MemoryTrimExclusions) ? null : MemoryTrimExclusions.Trim();
                s.MemoryTrimLogNoAction = MemoryTrimLogNoAction;
                s.StandbyPurgeEnabled = IsStandbyPurgeEnabled;
                s.StandbyPurgeFreeRamThresholdMB = Math.Max(0, StandbyPurgeFreeRamThresholdMb);
                s.StandbyPurgeOnlyInPerformanceMode = StandbyPurgeOnlyInPerformanceMode;
                s.FileCachePurgeEnabled = IsFileCachePurgeEnabled;
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
    partial void OnMemoryTrimMinimumRamLoadPercentChanged(int value) => SaveSettings();
    partial void OnMemoryTrimExclusionsChanged(string value) => SaveSettings();
    partial void OnMemoryTrimLogNoActionChanged(bool value) => SaveSettings();
    partial void OnIsStandbyPurgeEnabledChanged(bool value) => SaveSettings();
    partial void OnStandbyPurgeFreeRamThresholdMbChanged(long value) => SaveSettings();
    partial void OnStandbyPurgeOnlyInPerformanceModeChanged(bool value) => SaveSettings();
    partial void OnIsFileCachePurgeEnabledChanged(bool value) => SaveSettings();
    partial void OnStartWithWindowsChanged(bool value) => SaveSettings();
    partial void OnMinimizeToTrayChanged(bool value) => SaveSettings();
    partial void OnSelectedThemeIndexChanged(int value) => SaveSettings();
}
