using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Shared.Messages;
using ProGuard.UI.IPC;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly EngineProxy _engine;
    private readonly ConfigWriter _config;
    private bool _loading;

    /// <summary>Why the last change wasn't saved; empty once a save succeeds.</summary>
    [ObservableProperty]
    private string saveError = string.Empty;

    /// <summary>The most recent save; tests await it.</summary>
    public Task SaveTask { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private bool isForegroundBoostEnabled;

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

    public SettingsViewModel(EngineProxy engine) : this(engine, new ConfigWriter(engine)) { }

    public SettingsViewModel(EngineProxy engine, ConfigWriter config)
    {
        _engine = engine;
        _config = config;
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
            // The engine seeds the row; the UI may not be able to write the database, so it just shows the defaults.
            var s = db.Settings.FirstOrDefault() ?? new AppSettings { Id = 1 };

            IsProBalanceEnabled = s.ProBalanceEnabled;
            IsForegroundBoostEnabled = s.ForegroundBoostEnabled;
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
        SaveTask = SaveAsync();
    }

    // The engine owns the database, so this goes over its pipe (see ConfigWriter); only the values that differ from the
    // stored ones are sent.
    private async Task SaveAsync()
    {
        var result = await _config.UpdateSettingsAsync(s =>
        {
            s.ProBalanceEnabled = IsProBalanceEnabled;
            s.ForegroundBoostEnabled = IsForegroundBoostEnabled;
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
        });
        SaveError = result.Success ? string.Empty : "Settings not saved: " + result.Error;
    }

    partial void OnIsProBalanceEnabledChanged(bool value) => SaveSettings();
    partial void OnIsForegroundBoostEnabledChanged(bool value) => SaveSettings();
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
