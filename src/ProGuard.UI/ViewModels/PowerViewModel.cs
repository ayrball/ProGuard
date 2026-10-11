using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Shared.Messages;
using ProGuard.UI.IPC;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

public partial class PowerViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool isPerformanceModeEnabled;

    [ObservableProperty]
    private bool isIdleSaverEnabled;

    [ObservableProperty]
    private int idleTimeout = 300;

    [ObservableProperty]
    private int selectedIdlePlanIndex = -1;

    [ObservableProperty]
    private string currentPowerPlanText = "Current Power Plan: Detecting...";

    [ObservableProperty]
    private int selectedPlanIndex = 0;

    /// <summary>The power plans installed on this machine; every plan picker on the page lists these.</summary>
    public ObservableCollection<PowerPlanItem> PowerPlans { get; set; } = new();

    // ---------- Performance Mode: target plan and automatic triggers ----------

    [ObservableProperty]
    private int performanceModePlanIndex = -1;

    /// <summary>Processes that turn Performance Mode on, one per line.</summary>
    [ObservableProperty]
    private string autoPerformanceModeProcesses = string.Empty;

    /// <summary>Processes that never turn it on, one per line.</summary>
    [ObservableProperty]
    private string autoPerformanceModeExclusions = string.Empty;

    [ObservableProperty]
    private bool detectGames;

    [ObservableProperty]
    private bool engageForSteam;

    // ---------- Load-based power switcher ----------

    [ObservableProperty]
    private bool isLoadSwitcherEnabled;

    [ObservableProperty]
    private double loadSwitcherLowThreshold = 10;

    [ObservableProperty]
    private double loadSwitcherHighThreshold = 50;

    [ObservableProperty]
    private int loadSwitcherAveragingSamples = 5;

    [ObservableProperty]
    private int loadSwitcherMinimumTimeInPlanMs = 5000;

    [ObservableProperty]
    private int loadSwitcherLowPlanIndex = -1;

    [ObservableProperty]
    private int loadSwitcherHighPlanIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadSwitcherLowPlanUsed))]
    private bool loadSwitcherDeferLowPlan = true;

    /// <summary>With the low plan deferred, low load goes back to the plan from before, so the picker is unused.</summary>
    public bool IsLoadSwitcherLowPlanUsed => !LoadSwitcherDeferLowPlan;

    /// <summary>Why the load switcher settings shown can't be used (they aren't saved), or null.</summary>
    [ObservableProperty]
    private string? loadSwitcherError;

    // ---------- Start with power plan ----------

    /// <summary>Index 0 leaves the plan alone; index i is <see cref="PowerPlans"/>[i - 1].</summary>
    public ObservableCollection<string> StartupPlanChoices { get; } = new();

    [ObservableProperty]
    private int startupPlanIndex;

    private readonly EngineProxy _engine;
    private readonly Action<bool> _applyPerformanceModeLocally;
    private readonly ConfigWriter _config;

    /// <summary>The most recent Performance Mode change; tests await it.</summary>
    public Task PerformanceModeChange { get; private set; } = Task.CompletedTask;

    public PowerViewModel() : this(new EngineProxy()) { }

    /// <param name="applyPerformanceModeLocally">
    /// What to do when the engine can't be reached; defaults to switching the power plan from this process.
    /// </param>
    /// <param name="plans">The installed plans; defaults to asking Windows.</param>
    public PowerViewModel(EngineProxy engine, Action<bool>? applyPerformanceModeLocally = null, IReadOnlyList<PowerPlanInfo>? plans = null,
        ConfigWriter? config = null)
    {
        _engine = engine;
        _config = config ?? new ConfigWriter(engine);
        _applyPerformanceModeLocally = applyPerformanceModeLocally ?? SetPerformancePlanLocally;
        _loading = true;
        try
        {
            InitializePowerPlans(plans ?? Core.Helpers.PowerPlans.Enumerate());
        }
        finally
        {
            _loading = false;
        }
        LoadSettings();
    }

    private void InitializePowerPlans(IReadOnlyList<PowerPlanInfo> plans)
    {
        PowerPlans.Clear();
        foreach (var plan in plans)
            PowerPlans.Add(new PowerPlanItem { Name = plan.Name, Guid = plan.Id, Index = PowerPlans.Count });

        StartupPlanChoices.Clear();
        StartupPlanChoices.Add("Leave the plan unchanged");
        foreach (var plan in PowerPlans) StartupPlanChoices.Add(plan.Name);

        // The defaults, until saved settings say otherwise.
        SelectedIdlePlanIndex = IndexOfPlan(Powrprof.PowerSaver);
        PerformanceModePlanIndex = IndexOfPlan(Powrprof.HighPerformance);
        LoadSwitcherLowPlanIndex = IndexOfPlan(Powrprof.Balanced);
        LoadSwitcherHighPlanIndex = IndexOfPlan(Powrprof.HighPerformance);

        RefreshActivePlan();
    }

    private int IndexOfPlan(Guid id) => PowerPlans.FirstOrDefault(p => p.Guid == id)?.Index ?? -1;

    private int IndexOfPlan(string? id, Guid fallback) => IndexOfPlan(Guid.TryParse(id, out var plan) ? plan : fallback);

    private Guid PlanAt(int index) => index >= 0 && index < PowerPlans.Count ? PowerPlans[index].Guid : Guid.Empty;

    /// <summary>The plan picked at <paramref name="index"/>, as stored in the settings; <paramref name="keep"/> if none
    /// is picked (the saved plan is no longer installed), so opening the page doesn't lose it.</summary>
    private string? PlanGuidAt(int index, string? keep) => PlanAt(index) is var id && id != Guid.Empty ? id.ToString() : keep;

    public void RefreshActivePlan()
    {
        try
        {
            if (Powrprof.PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) == 0 && ptr != IntPtr.Zero)
            {
                var activeGuid = Marshal.PtrToStructure<Guid>(ptr);
                Marshal.FreeHGlobal(ptr);

                var match = PowerPlans.FirstOrDefault(p => p.Guid == activeGuid);
                if (match != null)
                {
                    CurrentPowerPlanText = $"Current Power Plan: {match.Name}";
                    SelectedPlanIndex = match.Index;
                    foreach (var p in PowerPlans) p.IsChecked = (p.Guid == activeGuid);
                }
                else
                {
                    CurrentPowerPlanText = $"Current Power Plan: Custom ({activeGuid.ToString().Substring(0, 8)}...)";
                }
            }
            else
            {
                CurrentPowerPlanText = "Current Power Plan: Balanced";
            }
        }
        catch
        {
            CurrentPowerPlanText = "Current Power Plan: Balanced";
        }
    }

    [RelayCommand]
    public void SelectPlan(int index)
    {
        if (index < 0 || index >= PowerPlans.Count) return;

        var target = PowerPlans[index];
        var guid = target.Guid;
        try
        {
            Powrprof.PowerSetActiveScheme(IntPtr.Zero, ref guid);
            CurrentPowerPlanText = $"Current Power Plan: {target.Name}";
            SelectedPlanIndex = index;

            foreach (var p in PowerPlans)
            {
                p.IsChecked = (p.Index == index);
            }
        }
        catch { }
    }

    partial void OnIsPerformanceModeEnabledChanged(bool value)
    {
        PerformanceModeChange = ApplyPerformanceModeAsync(PerformanceModeChange, value);
    }

    private async Task ApplyPerformanceModeAsync(Task previous, bool enable)
    {
        // Rapid toggles must reach the engine in order.
        await previous;

        // The engine owns Performance Mode: it runs with the service's privileges and remembers the plan to restore.
        CommandResult? result = null;
        try
        {
            result = await _engine.EnablePerformanceModeAsync(enable);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Performance Mode command failed: {ex.Message}");
        }

        if (result == null)
        {
            try { _applyPerformanceModeLocally(enable); }
            catch (Exception ex) { Debug.WriteLine($"Local Performance Mode switch failed: {ex.Message}"); }
        }
        else if (!result.Success)
        {
            Debug.WriteLine($"Engine refused Performance Mode change: {result.Error}");
        }

        RefreshActivePlan();
    }

    private void SetPerformancePlanLocally(bool enable)
    {
        var plan = !enable ? Powrprof.Balanced : PlanAt(PerformanceModePlanIndex) is var target && target != Guid.Empty ? target : Powrprof.HighPerformance;
        Powrprof.PowerSetActiveScheme(IntPtr.Zero, ref plan);
    }

    partial void OnIsIdleSaverEnabledChanged(bool value) => SaveSettings();
    partial void OnIdleTimeoutChanged(int value) => SaveSettings();
    partial void OnSelectedIdlePlanIndexChanged(int value) => SaveSettings();
    partial void OnPerformanceModePlanIndexChanged(int value) => SaveSettings();
    partial void OnAutoPerformanceModeProcessesChanged(string value) => SaveSettings();
    partial void OnAutoPerformanceModeExclusionsChanged(string value) => SaveSettings();
    partial void OnDetectGamesChanged(bool value) => SaveSettings();
    partial void OnEngageForSteamChanged(bool value) => SaveSettings();
    partial void OnIsLoadSwitcherEnabledChanged(bool value) => SaveSettings();
    partial void OnLoadSwitcherLowThresholdChanged(double value) => SaveSettings();
    partial void OnLoadSwitcherHighThresholdChanged(double value) => SaveSettings();
    partial void OnLoadSwitcherAveragingSamplesChanged(int value) => SaveSettings();
    partial void OnLoadSwitcherMinimumTimeInPlanMsChanged(int value) => SaveSettings();
    partial void OnLoadSwitcherLowPlanIndexChanged(int value) => SaveSettings();
    partial void OnLoadSwitcherHighPlanIndexChanged(int value) => SaveSettings();
    partial void OnLoadSwitcherDeferLowPlanChanged(bool value) => SaveSettings();
    partial void OnStartupPlanIndexChanged(int value) => SaveSettings();

    /// <summary>The load switcher settings as shown on the page.</summary>
    public LoadSwitcherOptions LoadSwitcherOptions => new()
    {
        Enabled = IsLoadSwitcherEnabled,
        LowThreshold = LoadSwitcherLowThreshold,
        HighThreshold = LoadSwitcherHighThreshold,
        AveragingWindowSamples = LoadSwitcherAveragingSamples,
        MinimumTimeInPlanMs = LoadSwitcherMinimumTimeInPlanMs,
        LowPlan = PlanAt(LoadSwitcherLowPlanIndex),
        HighPlan = PlanAt(LoadSwitcherHighPlanIndex),
        DeferLowPlan = LoadSwitcherDeferLowPlan,
    };

    private bool _loading;

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();
            var s = db.Settings.FirstOrDefault();
            if (s != null)
            {
                IsIdleSaverEnabled = s.IdleSaverEnabled;
                IdleTimeout = s.IdleTimeoutMinutes * 60;
                if (Guid.TryParse(s.IdlePowerPlanGuid, out var idlePlan))
                {
                    var match = PowerPlans.FirstOrDefault(p => p.Guid == idlePlan);
                    if (match != null) SelectedIdlePlanIndex = match.Index;
                }

                PerformanceModePlanIndex = IndexOfPlan(s.PerformanceModePlanGuid, Powrprof.HighPerformance);
                AutoPerformanceModeProcesses = string.Join(Environment.NewLine, SplitList(s.AutoPerformanceModeProcesses));
                AutoPerformanceModeExclusions = string.Join(Environment.NewLine, SplitList(s.AutoPerformanceModeExclusions));
                DetectGames = s.AutoPerformanceModeDetectGames;
                EngageForSteam = s.PerformanceModeEngageForSteam;

                IsLoadSwitcherEnabled = s.LoadSwitcherEnabled;
                LoadSwitcherLowThreshold = s.LoadSwitcherLowThreshold;
                LoadSwitcherHighThreshold = s.LoadSwitcherHighThreshold;
                LoadSwitcherAveragingSamples = s.LoadSwitcherAveragingWindowSamples;
                LoadSwitcherMinimumTimeInPlanMs = s.LoadSwitcherMinimumTimeInPlanMs;
                LoadSwitcherLowPlanIndex = IndexOfPlan(s.LoadSwitcherLowPlanGuid, Powrprof.Balanced);
                LoadSwitcherHighPlanIndex = IndexOfPlan(s.LoadSwitcherHighPlanGuid, Powrprof.HighPerformance);
                LoadSwitcherDeferLowPlan = s.LoadSwitcherDeferLowPlan;

                StartupPlanIndex = Guid.TryParse(s.StartWithPowerPlanGuid, out var startPlan) ? IndexOfPlan(startPlan) + 1 : 0;
            }
        }
        catch { }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Why the last change wasn't saved; empty once a save succeeds.</summary>
    [ObservableProperty]
    private string saveError = string.Empty;

    /// <summary>The most recent save; tests await it.</summary>
    public Task SaveTask { get; private set; } = Task.CompletedTask;

    private void SaveSettings()
    {
        // Shown at once, even while loading, so a bad saved combination is flagged when the page opens.
        LoadSwitcherError = IsLoadSwitcherEnabled ? LoadSwitcherOptions.Validate() : null;
        if (_loading) return;
        SaveTask = SaveAsync();
    }

    // The engine owns the database, so this goes over its pipe (see ConfigWriter); only changed values are sent.
    private async Task SaveAsync()
    {
        var loadSwitcherValid = LoadSwitcherError == null;
        var result = await _config.UpdateSettingsAsync(s =>
        {
            s.IdleSaverEnabled = IsIdleSaverEnabled;
            s.IdleTimeoutMinutes = Math.Max(1, IdleTimeout / 60);
            // The engine reads the plan from the database; without this the idle plan choice never reached it.
            if (SelectedIdlePlanIndex >= 0 && SelectedIdlePlanIndex < PowerPlans.Count)
                s.IdlePowerPlanGuid = PowerPlans[SelectedIdlePlanIndex].Guid.ToString();

            s.PerformanceModePlanGuid = PlanGuidAt(PerformanceModePlanIndex, s.PerformanceModePlanGuid);
            s.AutoPerformanceModeProcesses = JoinList(AutoPerformanceModeProcesses);
            s.AutoPerformanceModeExclusions = JoinList(AutoPerformanceModeExclusions);
            s.AutoPerformanceModeDetectGames = DetectGames;
            s.PerformanceModeEngageForSteam = EngageForSteam;

            // Settings the engine would refuse stay unsaved (the last good ones keep running) until they're fixed.
            if (loadSwitcherValid)
            {
                s.LoadSwitcherEnabled = IsLoadSwitcherEnabled;
                s.LoadSwitcherLowThreshold = LoadSwitcherLowThreshold;
                s.LoadSwitcherHighThreshold = LoadSwitcherHighThreshold;
                s.LoadSwitcherAveragingWindowSamples = LoadSwitcherAveragingSamples;
                s.LoadSwitcherMinimumTimeInPlanMs = LoadSwitcherMinimumTimeInPlanMs;
                s.LoadSwitcherLowPlanGuid = PlanGuidAt(LoadSwitcherLowPlanIndex, s.LoadSwitcherLowPlanGuid);
                s.LoadSwitcherHighPlanGuid = PlanGuidAt(LoadSwitcherHighPlanIndex, s.LoadSwitcherHighPlanGuid);
                s.LoadSwitcherDeferLowPlan = LoadSwitcherDeferLowPlan;
            }

            s.StartWithPowerPlanGuid = StartupPlanIndex <= 0 ? null : PlanGuidAt(StartupPlanIndex - 1, s.StartWithPowerPlanGuid);
        });
        SaveError = result.Success ? string.Empty : "Power settings not saved: " + result.Error;
    }

    private static string[] SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? JoinList(string text) => SplitList(text) is { Length: > 0 } items ? string.Join(";", items) : null;
}

public class PowerPlanItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public Guid Guid { get; set; }
    public int Index { get; set; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}
