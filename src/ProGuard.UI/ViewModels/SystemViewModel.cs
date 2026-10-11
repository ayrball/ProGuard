using System.Collections.ObjectModel;
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

/// <summary>Engine behaviour and power. Every change is saved to the engine as soon as it is made.</summary>
public sealed partial class SystemViewModel : ObservableObject, IPage
{
    private readonly EngineHub _hub;
    private readonly ShellViewModel _shell;
    private bool _loading;

    // The settings as last loaded. Saves change only what this page shows and send the rest back as they were,
    // so settings without a control here (SmartTrim details, IdleSaver, ...) are never reset.
    private EngineSettingsDto? _settings;

    public ObservableCollection<SensitivityStep> Sensitivity { get; } = new();
    public ObservableCollection<PowerPlanRow> Plans { get; } = new();
    public IReadOnlyList<Choice<long>> TrimThresholds { get; } = new[] { 256L, 512, 1024, 2048, 4096 }
        .Select(mb => new Choice<long>(mb >= 1024 ? $"{mb / 1024} GB" : $"{mb} MB", mb)).ToList();

    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private bool proBalanceEnabled;
    [ObservableProperty] private int sensitivityLevel = 5;
    [ObservableProperty] private bool foregroundBoostEnabled;
    [ObservableProperty] private bool smartTrimEnabled;
    [ObservableProperty] private Choice<long>? trimThreshold;
    [ObservableProperty] private bool performanceMode;

    public string SensitivityDescription => SensitivityLevel switch
    {
        <= 2 => "STEPS IN ONLY WHEN THE MACHINE IS PINNED",
        <= 4 => "STEPS IN UNDER HEAVY LOAD",
        <= 6 => "BALANCED — STEPS IN WHEN THINGS GET BUSY",
        <= 8 => "STEPS IN EARLY",
        _ => "STEPS IN AT THE FIRST SIGN OF CONTENTION"
    };

    public SystemViewModel(EngineHub hub, ShellViewModel shell)
    {
        _hub = hub;
        _shell = shell;
        for (int i = 1; i <= 10; i++) Sensitivity.Add(new SensitivityStep(i));
        PaintSensitivity();
    }

    public void OnShown() => _ = LoadAsync();
    public void OnHidden() { }

    private async Task LoadAsync()
    {
        var settings = await _hub.QueryAsync<EngineSettingsDto>(CommandTypes.GetSettings, quiet: true);
        var power = await _hub.QueryAsync<PowerStateDto>(CommandTypes.GetPower, quiet: true);

        _loading = true;
        try
        {
            if (settings != null)
            {
                _settings = settings;
                ProBalanceEnabled = settings.ProBalanceEnabled;
                SensitivityLevel = Math.Clamp(settings.ProBalanceSensitivity, 1, 10);
                ForegroundBoostEnabled = settings.ForegroundBoostEnabled;
                SmartTrimEnabled = settings.SmartTrimEnabled;
                TrimThreshold = TrimThresholds.OrderBy(t => Math.Abs(t.Value - settings.SmartTrimMemoryThresholdMB)).First();
            }

            if (power != null)
            {
                Plans.Clear();
                foreach (var plan in power.Plans) Plans.Add(new PowerPlanRow(plan, plan.Id == power.ActivePlanId));
                PerformanceMode = power.PerformanceMode;
            }

            IsLoaded = settings != null;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnProBalanceEnabledChanged(bool value) => _ = SaveAsync();
    partial void OnForegroundBoostEnabledChanged(bool value) => _ = SaveAsync();
    partial void OnSmartTrimEnabledChanged(bool value) => _ = SaveAsync();
    partial void OnTrimThresholdChanged(Choice<long>? value) => _ = SaveAsync();
    partial void OnSensitivityLevelChanged(int value)
    {
        PaintSensitivity();
        OnPropertyChanged(nameof(SensitivityDescription));
        _ = SaveAsync();
    }

    partial void OnPerformanceModeChanged(bool value)
    {
        if (_loading) return;
        _ = SetPerformanceModeAsync(value);
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

    private async Task SaveAsync()
    {
        if (_loading || !IsLoaded || _settings == null) return;
        var settings = DtoCopy.Clone(_settings);
        settings.ProBalanceEnabled = ProBalanceEnabled;
        settings.ProBalanceSensitivity = SensitivityLevel;
        settings.ForegroundBoostEnabled = ForegroundBoostEnabled;
        settings.SmartTrimEnabled = SmartTrimEnabled;
        settings.SmartTrimMemoryThresholdMB = TrimThreshold?.Value ?? settings.SmartTrimMemoryThresholdMB;
        if (await _hub.QueryAsync<EngineSettingsDto>(CommandTypes.SaveSettings, argument: settings) is { } saved)
        {
            _settings = saved;
            _hub.RefreshNow();
        }
    }

    private async Task SetPerformanceModeAsync(bool enabled)
    {
        if (await _hub.RunAsync(CommandTypes.EnablePerformanceMode, argument: enabled, success: enabled ? "PERFORMANCE MODE ON" : "PERFORMANCE MODE OFF"))
        {
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task ActivatePlanAsync(PowerPlanRow plan)
    {
        if (plan.IsActive) return;
        if (await _hub.RunAsync(CommandTypes.SetPowerPlan, argument: plan.Id, success: $"POWER PLAN · {plan.Name}"))
        {
            foreach (var p in Plans) p.IsActive = p.Id == plan.Id;
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
}
