using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Shared.Messages;
using ProGuard.UI.Controls;
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

    // ProBalance restraint actions ([OutOfControlProcessRestraint]); all off by default, as in Process Lasso.
    [ObservableProperty]
    private bool proBalanceRestrainByAffinity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProBalanceRestraintAffinityText))]
    private long proBalanceRestraintAffinity;

    [ObservableProperty]
    private bool proBalanceDropOneRandomCore;

    [ObservableProperty]
    private bool proBalanceLowerIoPriority;

    [ObservableProperty]
    private bool proBalanceUseEfficiencyMode;

    [ObservableProperty]
    private bool proBalanceDoNotLowerPriorityClass;

    [ObservableProperty]
    private bool proBalanceDisableWhenSysIdle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProBalanceDisableIfSysIdleSeconds))]
    private int proBalanceDisableIfSysIdleMs = 30000;

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

    /// <summary>The CPUs a restrained process is limited to, as the page shows them.</summary>
    public string ProBalanceRestraintAffinityText => ProBalanceRestraintAffinity == 0
        ? "No CPUs chosen"
        : ProcessListViewModel.FormatAffinityText(ProBalanceRestraintAffinity);

    /// <summary>The page edits the idle time in seconds.</summary>
    public int ProBalanceDisableIfSysIdleSeconds
    {
        get => ProBalanceDisableIfSysIdleMs / 1000;
        set => ProBalanceDisableIfSysIdleMs = Math.Max(1, value) * 1000;
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

    [RelayCommand]
    private void ChooseRestraintAffinity()
    {
        var dialog = new AffinityPickerDialog(ProBalanceRestraintAffinity == 0 ? -1L : ProBalanceRestraintAffinity, "processes ProBalance restrains")
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() == true) ProBalanceRestraintAffinity = dialog.SelectedMask;
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
            ProBalanceRestrainByAffinity = s.ProBalanceRestrainByAffinity;
            ProBalanceRestraintAffinity = s.ProBalanceRestraintAffinity;
            ProBalanceDropOneRandomCore = s.ProBalanceDropOneRandomCore;
            ProBalanceLowerIoPriority = s.ProBalanceLowerIoPriority;
            ProBalanceUseEfficiencyMode = s.ProBalanceUseEfficiencyMode;
            ProBalanceDoNotLowerPriorityClass = s.ProBalanceDoNotLowerPriorityClass;
            ProBalanceDisableWhenSysIdle = s.ProBalanceDisableWhenSysIdle;
            ProBalanceDisableIfSysIdleMs = s.ProBalanceDisableIfSysIdleMs;
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
                s.ProBalanceRestrainByAffinity = ProBalanceRestrainByAffinity;
                s.ProBalanceRestraintAffinity = ProBalanceRestraintAffinity;
                s.ProBalanceDropOneRandomCore = ProBalanceDropOneRandomCore;
                s.ProBalanceLowerIoPriority = ProBalanceLowerIoPriority;
                s.ProBalanceUseEfficiencyMode = ProBalanceUseEfficiencyMode;
                s.ProBalanceDoNotLowerPriorityClass = ProBalanceDoNotLowerPriorityClass;
                s.ProBalanceDisableWhenSysIdle = ProBalanceDisableWhenSysIdle;
                s.ProBalanceDisableIfSysIdleMs = Math.Max(1000, ProBalanceDisableIfSysIdleMs);
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
    partial void OnProBalanceRestrainByAffinityChanged(bool value) => SaveSettings();
    partial void OnProBalanceRestraintAffinityChanged(long value) => SaveSettings();
    partial void OnProBalanceDropOneRandomCoreChanged(bool value) => SaveSettings();
    partial void OnProBalanceLowerIoPriorityChanged(bool value) => SaveSettings();
    partial void OnProBalanceUseEfficiencyModeChanged(bool value) => SaveSettings();
    partial void OnProBalanceDoNotLowerPriorityClassChanged(bool value) => SaveSettings();
    partial void OnProBalanceDisableWhenSysIdleChanged(bool value) => SaveSettings();
    partial void OnProBalanceDisableIfSysIdleMsChanged(int value) => SaveSettings();
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
