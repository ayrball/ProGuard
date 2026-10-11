using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

public sealed partial class SensitivityStep : ObservableObject
{
    public int Level { get; }
    [ObservableProperty] private bool isLit;
    [ObservableProperty] private bool isCurrent;
    public SensitivityStep(int level) => Level = level;
}

public sealed partial class PowerPlanRow : ObservableObject
{
    public Guid Id { get; }
    public string Name { get; }
    [ObservableProperty] private bool isActive;

    public PowerPlanRow(PowerPlanDto plan, bool isActive)
    {
        Id = plan.Id;
        Name = plan.Name.ToUpperInvariant();
        this.isActive = isActive;
    }
}

/// <summary>
/// Everything the engine can be configured with, in sections. Each change is saved as soon as it is made (typing is
/// debounced). A save sends a copy of the settings as last loaded with only the values this page shows replaced, and
/// values the user hasn't touched are sent back exactly as they came, so nothing is ever reset by saving something else.
/// </summary>
public sealed partial class SystemViewModel : ObservableObject, IPage
{
    // Properties whose change is a setting to save. (Fields that wrap a number or a list raise their own Committed event.)
    private static readonly HashSet<string> Persisted = new()
    {
        nameof(ProBalanceEnabled), nameof(SensitivityLevel), nameof(RestrainByAffinity), nameof(DropOneRandomCore),
        nameof(LowerIoPriority), nameof(UseEfficiencyMode), nameof(DoNotLowerPriorityClass), nameof(PauseWhenIdle),
        nameof(ForegroundBoostEnabled),
        nameof(SmartTrimEnabled), nameof(TrimLogNoAction), nameof(StandbyPurgeEnabled), nameof(StandbyOnlyInPerformanceMode),
        nameof(FileCachePurgeEnabled),
        nameof(IdleSaverEnabled),
        nameof(DetectGames), nameof(EngageForSteam),
        nameof(LoadSwitcherEnabled), nameof(DeferLowPlan),
        nameof(StartWithWindows), nameof(MinimizeToTray),
    };

    private readonly EngineHub _hub;
    private readonly ShellViewModel _shell;
    private readonly IStartupRegistration _startup;
    private bool _loading;
    private int _logical;
    private bool _coresEdited;
    private bool? _startupChoice;
    private int _startupRefused; // StartWithWindows change notifications still to ignore after Windows refused
    private CancellationTokenSource? _saveCts;
    private IReadOnlyList<PowerPlanDto> _plans = Array.Empty<PowerPlanDto>();
    private Guid _activePlan;

    // The settings as last loaded or saved. Saves change only what this page shows and send the rest back as they were.
    private EngineSettingsDto? _settings;

    public ObservableCollection<SensitivityStep> Sensitivity { get; } = new();
    public ObservableCollection<PowerPlanRow> Plans { get; } = new();
    public ObservableCollection<CoreToggle> RestraintCores { get; } = new();

    // ProBalance
    public NumberField TriggerPercent { get; } = new("USE ABOVE", "%", 1, 100, integer: false);
    public NumberField RecoveryPercent { get; } = new("RESTORE BELOW", "%", 0, 100, integer: false);
    public NumberField IdlePauseMs { get; } = new("PAUSE AFTER IDLE FOR", "MS", 1000, 3_600_000);

    // SmartTrim
    public NumberField TrimInterval { get; } = new("EVERY", "MIN", 0.5, 1440, integer: false, scale: 60);
    public NumberField TrimThresholdMb { get; } = new("WORKING SET ABOVE", "MB", 1, 1_048_576);
    public NumberField TrimMinRamLoad { get; } = new("ONLY WHEN RAM IS AT LEAST", "%", 0, 100);
    public ListField TrimExclusions { get; } = new("NEVER TRIM THESE", ',', ';');
    public NumberField StandbyFreeRamMb { get; } = new("FREE RAM BELOW", "MB", 0, 1_048_576);

    // IdleSaver
    public NumberField IdleTimeout { get; } = new("AFTER", "MIN", 1, 1440);
    public PlanPicker IdlePlan { get; } = new("WHILE AWAY, USE", "DEFAULT · POWER SAVER");

    // Performance Mode
    public PlanPicker PerformancePlan { get; } = new("SWITCH TO", "DEFAULT · HIGH PERFORMANCE");
    public ListField AutoProcesses { get; } = new("TURN ON AUTOMATICALLY FOR", ';');
    public ListField AutoExclusions { get; } = new("NEVER FOR", ';');

    // Load switcher
    public NumberField LowLoad { get; } = new("LOW BELOW", "%", 0, 100, integer: false);
    public NumberField HighLoad { get; } = new("HIGH ABOVE", "%", 0, 100, integer: false);
    public NumberField AveragingSamples { get; } = new("AVERAGE OVER", "SAMPLES", 1, 600);
    public NumberField MinimumTimeInPlanMs { get; } = new("STAY AT LEAST", "MS", 0, 3_600_000);
    public PlanPicker LowPlan { get; } = new("AT LOW LOAD", "DEFAULT · BALANCED");
    public PlanPicker HighPlan { get; } = new("AT HIGH LOAD", "DEFAULT · HIGH PERFORMANCE");

    // Power and general
    public PlanPicker StartWithPlan { get; } = new("WHEN THE ENGINE STARTS", "LEAVE IT");
    public NumberField PollingInterval { get; } = new("REFRESH EVERY", "MS", 250, 60_000);

    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private bool isAdministrator = true;

    // ProBalance
    [ObservableProperty] private bool proBalanceEnabled;
    [ObservableProperty] private int sensitivityLevel = 5;
    [ObservableProperty] private bool restrainByAffinity;
    [ObservableProperty] private bool dropOneRandomCore;
    [ObservableProperty] private bool lowerIoPriority;
    [ObservableProperty] private bool useEfficiencyMode;
    [ObservableProperty] private bool doNotLowerPriorityClass;
    [ObservableProperty] private bool pauseWhenIdle;
    [ObservableProperty] private string restraintCoresSummary = string.Empty;

    [ObservableProperty] private bool foregroundBoostEnabled;

    // SmartTrim
    [ObservableProperty] private bool smartTrimEnabled;
    [ObservableProperty] private bool trimLogNoAction;
    [ObservableProperty] private bool standbyPurgeEnabled;
    [ObservableProperty] private bool standbyOnlyInPerformanceMode;
    [ObservableProperty] private bool fileCachePurgeEnabled;
    [ObservableProperty] private string purgeSummary = string.Empty;

    // IdleSaver
    [ObservableProperty] private bool idleSaverEnabled;

    // Performance Mode
    [ObservableProperty] private bool performanceMode;
    [ObservableProperty] private bool performanceModeAutomatic;
    [ObservableProperty] private bool detectGames;
    [ObservableProperty] private bool engageForSteam;

    // Load switcher
    [ObservableProperty] private bool loadSwitcherEnabled;
    [ObservableProperty] private bool deferLowPlan;
    [ObservableProperty] private string loadSwitcherError = string.Empty;

    // Power
    [ObservableProperty] private string planOwnerText = "—";

    // General
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool minimizeToTray;

    public string SensitivityDescription => SensitivityLevel switch
    {
        <= 2 => "STEPS IN ONLY WHEN THE MACHINE IS PINNED",
        <= 4 => "STEPS IN UNDER HEAVY LOAD",
        <= 6 => "BALANCED — STEPS IN WHEN THINGS GET BUSY",
        <= 8 => "STEPS IN EARLY",
        _ => "STEPS IN AT THE FIRST SIGN OF CONTENTION"
    };

    /// <summary>How long a burst of typing waits before saving. Tests set it to zero.</summary>
    internal TimeSpan SaveDelay { get; set; } = TimeSpan.FromMilliseconds(450);

    /// <summary>Sends settings to the engine and returns them as stored, or null when that failed. Replaceable in tests.</summary>
    internal Func<EngineSettingsDto, Task<EngineSettingsDto?>> Saver { get; set; }

    /// <summary>Completes when every save queued so far has been sent.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    public SystemViewModel(EngineHub hub, ShellViewModel shell, IStartupRegistration? startup = null)
    {
        _hub = hub;
        _shell = shell;
        _startup = startup ?? new RunKeyStartup();
        Saver = settings => _hub.QueryAsync<EngineSettingsDto>(CommandTypes.SaveSettings, argument: settings);

        for (int i = 1; i <= 10; i++) Sensitivity.Add(new SensitivityStep(i));
        PaintSensitivity();
        _logical = Math.Max(1, hub.Snapshot?.LogicalProcessors ?? Environment.ProcessorCount);
        BuildCores(0);

        foreach (var field in new[] { TriggerPercent, RecoveryPercent, IdlePauseMs, TrimInterval, TrimThresholdMb, TrimMinRamLoad, StandbyFreeRamMb,
                                       IdleTimeout, AveragingSamples, MinimumTimeInPlanMs, PollingInterval })
        {
            field.Committed += QueueSave;
        }
        // The load switcher's two thresholds are also checked against each other, whatever is typed.
        LowLoad.Edited += CheckLoadSwitcher;
        HighLoad.Edited += CheckLoadSwitcher;
        LowLoad.Committed += QueueSave;
        HighLoad.Committed += QueueSave;
        foreach (var list in new[] { TrimExclusions, AutoProcesses, AutoExclusions }) list.Committed += QueueSave;
        foreach (var picker in new[] { IdlePlan, PerformancePlan, LowPlan, HighPlan, StartWithPlan }) picker.Committed += QueueSave;

        hub.SnapshotApplied += OnSnapshot;
    }

    public void OnShown() => _ = LoadAsync();
    public void OnHidden() { }

    internal async Task LoadAsync()
    {
        var settings = await _hub.QueryAsync<EngineSettingsDto>(CommandTypes.GetSettings, quiet: true);
        var power = await _hub.QueryAsync<PowerStateDto>(CommandTypes.GetPower, quiet: true);
        Load(settings, power);
    }

    /// <summary>Shows what the engine sent. Either may be null when it couldn't be fetched; the page keeps what it had for that part.</summary>
    internal void Load(EngineSettingsDto? settings, PowerStateDto? power)
    {
        _loading = true;
        try
        {
            if (power != null) ApplyPower(power);

            if (settings != null)
            {
                _settings = settings;
                _startupChoice = null;
                _coresEdited = false;

                ProBalanceEnabled = settings.ProBalanceEnabled;
                SensitivityLevel = Math.Clamp(settings.ProBalanceSensitivity, 1, 10);
                TriggerPercent.Load(settings.ProBalanceCpuThreshold);
                RecoveryPercent.Load(settings.ProBalanceRecoveryThreshold);
                RestrainByAffinity = settings.ProBalanceRestrainByAffinity;
                BuildCores(settings.ProBalanceRestraintAffinity);
                DropOneRandomCore = settings.ProBalanceDropOneRandomCore;
                LowerIoPriority = settings.ProBalanceLowerIoPriority;
                UseEfficiencyMode = settings.ProBalanceUseEfficiencyMode;
                DoNotLowerPriorityClass = settings.ProBalanceDoNotLowerPriorityClass;
                PauseWhenIdle = settings.ProBalanceDisableWhenSysIdle;
                IdlePauseMs.Load(settings.ProBalanceDisableIfSysIdleMs);

                ForegroundBoostEnabled = settings.ForegroundBoostEnabled;

                SmartTrimEnabled = settings.SmartTrimEnabled;
                TrimInterval.Load(settings.SmartTrimIntervalSeconds);
                TrimThresholdMb.Load(settings.SmartTrimMemoryThresholdMB);
                TrimMinRamLoad.Load(settings.SmartTrimMinimumRamLoadPercent);
                TrimExclusions.Load(settings.SmartTrimExclusions);
                TrimLogNoAction = settings.SmartTrimLogNoAction;
                StandbyPurgeEnabled = settings.StandbyPurgeEnabled;
                StandbyFreeRamMb.Load(settings.StandbyPurgeFreeRamThresholdMB);
                StandbyOnlyInPerformanceMode = settings.StandbyPurgeOnlyInPerformanceMode;
                FileCachePurgeEnabled = settings.FileCachePurgeEnabled;

                IdleSaverEnabled = settings.IdleSaverEnabled;
                IdleTimeout.Load(settings.IdleTimeoutMinutes);
                IdlePlan.Load(_plans, settings.IdlePowerPlanGuid);

                PerformancePlan.Load(_plans, settings.PerformanceModePlanGuid);
                AutoProcesses.Load(settings.AutoPerformanceModeProcesses);
                AutoExclusions.Load(settings.AutoPerformanceModeExclusions);
                DetectGames = settings.AutoPerformanceModeDetectGames;
                EngageForSteam = settings.PerformanceModeEngageForSteam;

                LoadSwitcherEnabled = settings.LoadSwitcherEnabled;
                LowLoad.Load(settings.LoadSwitcherLowThreshold);
                HighLoad.Load(settings.LoadSwitcherHighThreshold);
                AveragingSamples.Load(settings.LoadSwitcherAveragingWindowSamples);
                MinimumTimeInPlanMs.Load(settings.LoadSwitcherMinimumTimeInPlanMs);
                LowPlan.Load(_plans, settings.LoadSwitcherLowPlanGuid);
                HighPlan.Load(_plans, settings.LoadSwitcherHighPlanGuid);
                DeferLowPlan = settings.LoadSwitcherDeferLowPlan;
                CheckLoadSwitcher();

                StartWithPlan.Load(_plans, settings.StartWithPowerPlanGuid);

                // What this user's Windows will really do, not what the engine remembers: only the toggle changes the setting.
                StartWithWindows = _startup.IsEnabled;
                MinimizeToTray = settings.MinimizeToTray;
                PollingInterval.Load(settings.PollingIntervalMs);

                _shell.MinimizeToTray = settings.MinimizeToTray;
            }

            IsLoaded = _settings != null;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyPower(PowerStateDto power)
    {
        _plans = power.Plans;
        _activePlan = power.ActivePlanId;

        Plans.Clear();
        foreach (var plan in power.Plans) Plans.Add(new PowerPlanRow(plan, plan.Id == power.ActivePlanId));
        PerformanceMode = power.PerformanceMode;
        PerformanceModeAutomatic = power.PerformanceMode && power.PerformanceModeAutomatic;
        PlanOwnerText = Fmt.PlanOwner(power.Owner);

        foreach (var picker in new[] { IdlePlan, PerformancePlan, LowPlan, HighPlan, StartWithPlan }) picker.SetPlans(power.Plans);
    }

    // ------------------------------------------------------------------ keeping up with the engine

    internal void OnSnapshot(EngineSnapshot s)
    {
        IsAdministrator = s.CallerIsAdministrator;

        int logical = Math.Max(1, s.LogicalProcessors);
        if (logical != _logical)
        {
            _logical = logical;
            var mask = CurrentMask();
            _loading = true;
            try { BuildCores(mask); }
            finally { _loading = false; }
        }

        // Performance Mode or the active plan changed (automatic mode, IdleSaver, the load switcher...): refresh the power section.
        if (IsLoaded && (s.PerformanceModeActive != PerformanceMode || (s.ActivePowerPlanId != Guid.Empty && s.ActivePowerPlanId != _activePlan)))
        {
            _ = RefreshPowerAsync();
        }
    }

    private async Task RefreshPowerAsync()
    {
        if (await _hub.QueryAsync<PowerStateDto>(CommandTypes.GetPower, quiet: true) is not { } power) return;
        _loading = true;
        try { ApplyPower(power); }
        finally { _loading = false; }
    }

    // ------------------------------------------------------------------ ProBalance

    partial void OnSensitivityLevelChanged(int value)
    {
        PaintSensitivity();
        OnPropertyChanged(nameof(SensitivityDescription));
    }

    private void PaintSensitivity()
    {
        foreach (var step in Sensitivity)
        {
            step.IsLit = step.Level <= SensitivityLevel;
            step.IsCurrent = step.Level == SensitivityLevel;
        }
    }

    [RelayCommand]
    private void SetSensitivity(int level) => SensitivityLevel = level;

    private void BuildCores(long mask)
    {
        RestraintCores.Clear();
        int count = Math.Clamp(_logical, 1, 64);
        for (int i = 0; i < count; i++)
        {
            var core = new CoreToggle(i, (mask & (1L << i)) != 0);
            core.PropertyChanged += OnCoreChanged;
            RestraintCores.Add(core);
        }
        RefreshCoreSummary();
    }

    private long CurrentMask() => MergeMask(_settings?.ProBalanceRestraintAffinity ?? 0);

    /// <summary>The restraint mask: the stored one, with the bits of the cores shown replaced when the user changed them.</summary>
    private long MergeMask(long stored)
    {
        if (!_coresEdited) return stored;
        int count = RestraintCores.Count;
        long shown = count >= 64 ? -1L : (1L << count) - 1;
        long picked = RestraintCores.Where(c => c.IsOn).Aggregate(0L, (m, c) => m | (1L << c.Index));
        return (stored & ~shown) | picked;
    }

    private void OnCoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading) return;
        _coresEdited = true;
        RefreshCoreSummary();
        QueueSave();
    }

    private void RefreshCoreSummary()
    {
        int on = RestraintCores.Count(c => c.IsOn);
        RestraintCoresSummary = on == 0 ? "NONE PICKED — NOTHING IS RESTRICTED" : $"{on} OF {RestraintCores.Count} THREADS";
    }

    private void SetCores(Func<int, bool> on)
    {
        foreach (var core in RestraintCores) core.IsOn = on(core.Index);
    }

    [RelayCommand] private void CoresAll() => SetCores(_ => true);
    [RelayCommand] private void CoresFirstHalf() => SetCores(i => i < RestraintCores.Count / 2);
    [RelayCommand] private void CoresEven() => SetCores(i => i % 2 == 0);
    [RelayCommand] private void CoresNone() => SetCores(_ => false);

    // ------------------------------------------------------------------ load switcher

    /// <summary>The thresholds that would be saved: what is on screen, or the stored value where the text isn't a number.</summary>
    private (double Low, double High) EffectiveThresholds() =>
        (LowLoad.ToStored(_settings?.LoadSwitcherLowThreshold ?? LowLoad.Value),
         HighLoad.ToStored(_settings?.LoadSwitcherHighThreshold ?? HighLoad.Value));

    /// <summary>The low threshold must be below the high one. Until it is, neither is saved (the engine would refuse the lot).</summary>
    private void CheckLoadSwitcher()
    {
        var (low, high) = EffectiveThresholds();
        LoadSwitcherError = high <= low ? "HIGH MUST BE ABOVE LOW — THESE TWO AREN'T SAVED UNTIL IT IS" : string.Empty;
    }

    // ------------------------------------------------------------------ saving

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(StartWithWindows) && _startupRefused > 0)
        {
            _startupRefused--; // Windows said no and the switch went back: nothing changed, nothing to save
            return;
        }
        if (e.PropertyName != null && Persisted.Contains(e.PropertyName)) QueueSave();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        try
        {
            if (value) _startup.Enable();
            else _startup.Disable();
            _startupChoice = value;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            _shell.Toaster.Show($"Windows wouldn't change the startup entry: {ex.Message}", isError: true);
            _startupRefused = 2; // the revert below raises one notification, the refused change itself the other
            _loading = true;
            try { StartWithWindows = !value; }
            finally { _loading = false; }
        }
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (!_loading) _shell.MinimizeToTray = value;
    }

    private void QueueSave()
    {
        if (_loading || !IsLoaded) return;

        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        PendingSave = SaveSoonAsync(PendingSave, cts.Token);
    }

    private async Task SaveSoonAsync(Task previous, CancellationToken token)
    {
        try
        {
            if (SaveDelay > TimeSpan.Zero) await Task.Delay(SaveDelay, token);
        }
        catch (OperationCanceledException)
        {
            await previous; // a newer save will carry this change
            return;
        }

        await previous; // one save at a time, in order
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (_settings == null) return;

        if (await Saver(BuildSettings()) is { } saved)
        {
            _settings = saved;
            _shell.MinimizeToTray = saved.MinimizeToTray;
            _hub.RefreshNow();
        }
        else
        {
            // The engine refused (or isn't there): show what it really has instead of values that didn't stick.
            await LoadAsync();
        }
    }

    /// <summary>The settings to send: the ones last loaded, with every value this page shows replaced by what's on screen.</summary>
    internal EngineSettingsDto BuildSettings()
    {
        var s = DtoCopy.Clone(_settings ?? throw new InvalidOperationException("The settings haven't been loaded."));

        s.ProBalanceEnabled = ProBalanceEnabled;
        if (Math.Clamp(s.ProBalanceSensitivity, 1, 10) != SensitivityLevel) s.ProBalanceSensitivity = SensitivityLevel;
        s.ProBalanceCpuThreshold = TriggerPercent.ToStored(s.ProBalanceCpuThreshold);
        s.ProBalanceRecoveryThreshold = RecoveryPercent.ToStored(s.ProBalanceRecoveryThreshold);
        s.ProBalanceRestrainByAffinity = RestrainByAffinity;
        s.ProBalanceRestraintAffinity = MergeMask(s.ProBalanceRestraintAffinity);
        s.ProBalanceDropOneRandomCore = DropOneRandomCore;
        s.ProBalanceLowerIoPriority = LowerIoPriority;
        s.ProBalanceUseEfficiencyMode = UseEfficiencyMode;
        s.ProBalanceDoNotLowerPriorityClass = DoNotLowerPriorityClass;
        s.ProBalanceDisableWhenSysIdle = PauseWhenIdle;
        s.ProBalanceDisableIfSysIdleMs = IdlePauseMs.ToStored(s.ProBalanceDisableIfSysIdleMs);

        s.ForegroundBoostEnabled = ForegroundBoostEnabled;

        s.SmartTrimEnabled = SmartTrimEnabled;
        s.SmartTrimIntervalSeconds = TrimInterval.ToStored(s.SmartTrimIntervalSeconds);
        s.SmartTrimMemoryThresholdMB = TrimThresholdMb.ToStored(s.SmartTrimMemoryThresholdMB);
        s.SmartTrimMinimumRamLoadPercent = TrimMinRamLoad.ToStored(s.SmartTrimMinimumRamLoadPercent);
        s.SmartTrimExclusions = TrimExclusions.ToStored();
        s.SmartTrimLogNoAction = TrimLogNoAction;
        s.StandbyPurgeEnabled = StandbyPurgeEnabled;
        s.StandbyPurgeFreeRamThresholdMB = StandbyFreeRamMb.ToStored(s.StandbyPurgeFreeRamThresholdMB);
        s.StandbyPurgeOnlyInPerformanceMode = StandbyOnlyInPerformanceMode;
        s.FileCachePurgeEnabled = FileCachePurgeEnabled;

        s.IdleSaverEnabled = IdleSaverEnabled;
        s.IdleTimeoutMinutes = IdleTimeout.ToStored(s.IdleTimeoutMinutes);
        s.IdlePowerPlanGuid = IdlePlan.ToStored(s.IdlePowerPlanGuid);

        s.PerformanceModePlanGuid = PerformancePlan.ToStored(s.PerformanceModePlanGuid);
        s.AutoPerformanceModeProcesses = AutoProcesses.ToStored();
        s.AutoPerformanceModeExclusions = AutoExclusions.ToStored();
        s.AutoPerformanceModeDetectGames = DetectGames;
        s.PerformanceModeEngageForSteam = EngageForSteam;

        s.LoadSwitcherEnabled = LoadSwitcherEnabled;
        var (low, high) = EffectiveThresholds();
        if (high > low)
        {
            s.LoadSwitcherLowThreshold = low;
            s.LoadSwitcherHighThreshold = high;
        }
        s.LoadSwitcherAveragingWindowSamples = AveragingSamples.ToStored(s.LoadSwitcherAveragingWindowSamples);
        s.LoadSwitcherMinimumTimeInPlanMs = MinimumTimeInPlanMs.ToStored(s.LoadSwitcherMinimumTimeInPlanMs);
        s.LoadSwitcherLowPlanGuid = LowPlan.ToStored(s.LoadSwitcherLowPlanGuid);
        s.LoadSwitcherHighPlanGuid = HighPlan.ToStored(s.LoadSwitcherHighPlanGuid);
        s.LoadSwitcherDeferLowPlan = DeferLowPlan;

        s.StartWithPowerPlanGuid = StartWithPlan.ToStored(s.StartWithPowerPlanGuid);

        if (_startupChoice is bool start) s.StartWithWindows = start;
        s.MinimizeToTray = MinimizeToTray;
        s.PollingIntervalMs = PollingInterval.ToStored(s.PollingIntervalMs);

        // Theme has no control: the Volt look is the only look.
        return s;
    }

    // ------------------------------------------------------------------ power and memory

    partial void OnPerformanceModeChanged(bool value)
    {
        if (_loading) return;
        _ = SetPerformanceModeAsync(value);
    }

    private async Task SetPerformanceModeAsync(bool enabled)
    {
        bool ok = await _hub.RunAsync(enabled ? CommandTypes.EnablePerformanceMode : CommandTypes.DisablePerformanceMode,
            success: enabled ? "PERFORMANCE MODE ON" : "PERFORMANCE MODE OFF");
        // Success shows the new plan; failure puts the switch back where the engine says it is.
        await RefreshPowerAsync();
        if (!ok) _shell.Hub.RefreshNow();
    }

    [RelayCommand]
    private async Task ActivatePlanAsync(PowerPlanRow plan)
    {
        if (plan.IsActive) return;
        if (await _hub.RunAsync(CommandTypes.SetPowerPlan, argument: plan.Id, success: $"POWER PLAN · {plan.Name}"))
        {
            _activePlan = plan.Id;
            foreach (var p in Plans) p.IsActive = p.Id == plan.Id;
            PlanOwnerText = Fmt.PlanOwner("User");
        }
    }

    [RelayCommand]
    private void TrimAll()
    {
        _shell.Confirm("TRIM EVERY PROCESS?", "Pushes every process's idle memory out to the page file. RAM frees up at once, but apps may feel sluggish for a moment as they page back in.", "TRIM ALL",
            async () =>
            {
                var freed = await _hub.QueryAsync<long>(CommandTypes.TrimWorkingSet);
                _shell.Toaster.Show($"FREED {Fmt.Bytes(freed)}");
                _hub.RefreshNow();
            });
    }

    private bool CanPurgeNow() => IsAdministrator;

    partial void OnIsAdministratorChanged(bool value) => PurgeNowCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanPurgeNow))]
    private async Task PurgeNowAsync()
    {
        PurgeSummary = "PURGING…";
        var result = await _shell.PurgeNowAsync();
        PurgeSummary = result == null ? string.Empty : Fmt.PurgeSummary(result);
    }
}
