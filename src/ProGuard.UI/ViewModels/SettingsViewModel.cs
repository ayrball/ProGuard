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
    private bool isProBalanceEnabled = true;

    [ObservableProperty]
    private int sensitivity = 5;

    [ObservableProperty]
    private int cpuThreshold = 25;

    [ObservableProperty]
    private bool isSmartTrimEnabled = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmartTrimIntervalMinutes))]
    private int smartTrimIntervalSeconds = 900;

    [ObservableProperty]
    private long smartTrimThresholdMb = 196;

    [ObservableProperty]
    private int smartTrimMinimumRamLoadPercent = 65;

    [ObservableProperty]
    private string smartTrimExclusions = string.Empty;

    [ObservableProperty]
    private bool smartTrimLogNoAction;

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

    /// <summary>The page edits the interval in minutes, as Process Lasso does (SmartTrimIntervalMins).</summary>
    public int SmartTrimIntervalMinutes
    {
        get => Math.Max(1, SmartTrimIntervalSeconds / 60);
        set => SmartTrimIntervalSeconds = Math.Max(1, value) * 60;
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

            IsProBalanceEnabled = s.ProBalanceEnabled;
            Sensitivity = s.ProBalanceSensitivity;
            CpuThreshold = (int)s.ProBalanceCpuThreshold;
            IsSmartTrimEnabled = s.SmartTrimEnabled;
            SmartTrimIntervalSeconds = s.SmartTrimIntervalSeconds;
            SmartTrimThresholdMb = s.SmartTrimMemoryThresholdMB;
            SmartTrimMinimumRamLoadPercent = s.SmartTrimMinimumRamLoadPercent;
            SmartTrimExclusions = s.SmartTrimExclusions ?? string.Empty;
            SmartTrimLogNoAction = s.SmartTrimLogNoAction;
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
                s.ProBalanceEnabled = IsProBalanceEnabled;
                s.ProBalanceSensitivity = Sensitivity;
                s.ProBalanceCpuThreshold = CpuThreshold;
                s.SmartTrimEnabled = IsSmartTrimEnabled;
                s.SmartTrimIntervalSeconds = SmartTrimIntervalSeconds;
                s.SmartTrimMemoryThresholdMB = SmartTrimThresholdMb;
                s.SmartTrimMinimumRamLoadPercent = Math.Clamp(SmartTrimMinimumRamLoadPercent, 0, 100);
                s.SmartTrimExclusions = string.IsNullOrWhiteSpace(SmartTrimExclusions) ? null : SmartTrimExclusions.Trim();
                s.SmartTrimLogNoAction = SmartTrimLogNoAction;
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

    partial void OnIsProBalanceEnabledChanged(bool value) => SaveSettings();
    partial void OnSensitivityChanged(int value) => SaveSettings();
    partial void OnCpuThresholdChanged(int value) => SaveSettings();
    partial void OnIsSmartTrimEnabledChanged(bool value) => SaveSettings();
    partial void OnSmartTrimIntervalSecondsChanged(int value) => SaveSettings();
    partial void OnSmartTrimThresholdMbChanged(long value) => SaveSettings();
    partial void OnSmartTrimMinimumRamLoadPercentChanged(int value) => SaveSettings();
    partial void OnSmartTrimExclusionsChanged(string value) => SaveSettings();
    partial void OnSmartTrimLogNoActionChanged(bool value) => SaveSettings();
    partial void OnIsStandbyPurgeEnabledChanged(bool value) => SaveSettings();
    partial void OnStandbyPurgeFreeRamThresholdMbChanged(long value) => SaveSettings();
    partial void OnStandbyPurgeOnlyInPerformanceModeChanged(bool value) => SaveSettings();
    partial void OnIsFileCachePurgeEnabledChanged(bool value) => SaveSettings();
    partial void OnStartWithWindowsChanged(bool value) => SaveSettings();
    partial void OnMinimizeToTrayChanged(bool value) => SaveSettings();
    partial void OnSelectedThemeIndexChanged(int value) => SaveSettings();
}
