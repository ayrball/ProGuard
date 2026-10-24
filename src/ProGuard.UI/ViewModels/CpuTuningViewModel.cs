using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;
using ProGuard.UI.Resources;

namespace ProGuard.UI.ViewModels;

/// <summary>One processor power setting of the plan on screen, for the power source on screen (plugged in or battery).</summary>
public sealed partial class CpuTuningRow : ObservableObject
{
    private readonly CpuTuningViewModel _owner;
    private bool _loading;

    public CpuTuningSettingDto Setting { get; private set; }
    public string Key => Setting.Key;
    public string Name => Setting.Name;
    public string Description => Setting.Description;
    public bool IsChoice => Setting.Kind == CpuTuningKind.Choice;
    public bool IsAvailable => Setting.Available;
    /// <summary>The value is spelled out beside the slider; a choice shows as its lit chip instead.</summary>
    public bool ShowsValue => Setting.Available && !IsChoice;
    public string? UnavailableReason => Setting.UnavailableReason?.ToUpperInvariant();
    public double Minimum => Setting.Min;
    public double Maximum => Setting.Max;
    public double Step => Math.Max(1, Setting.Increment);

    /// <summary>"P-CORES" / "E-CORES" for a setting with a twin for the other kind of core on a hybrid CPU; empty when it covers every core.</summary>
    public string ClassLabel { get; }

    public ObservableCollection<ChoiceOption<uint>> Choices { get; } = new();

    /// <summary>The value for the power source on screen. Moving the slider (or picking a choice) saves it, shortly after.</summary>
    [ObservableProperty] private double value;
    [ObservableProperty] private string valueText = string.Empty;
    /// <summary>"WINDOWS DEFAULT 10 % · BEFORE PROGUARD 5 %".</summary>
    [ObservableProperty] private string reference = string.Empty;
    /// <summary>Set while ProBalance holds another value in force for now.</summary>
    [ObservableProperty] private string liveNote = string.Empty;
    [ObservableProperty] private bool hiddenInWindows;

    public CpuTuningRow(CpuTuningViewModel owner, CpuTuningSettingDto setting, bool perClass)
    {
        _owner = owner;
        Setting = setting;
        ClassLabel = !perClass ? string.Empty : setting.EfficiencyClass > 0 ? Strings.PCores : Strings.ECores;
        foreach (var choice in setting.Choices) Choices.Add(new ChoiceOption<uint>(choice.Name.ToUpperInvariant(), choice.Value, false));
    }

    /// <summary>Shows the engine's values for <paramref name="dc"/>. Not an edit.</summary>
    public void Load(CpuTuningSettingDto setting, bool dc)
    {
        Setting = setting;
        _loading = true;
        try
        {
            Value = dc ? setting.Dc : setting.Ac;
            HiddenInWindows = setting.Hidden;
            var live = dc ? setting.LiveDc : setting.LiveAc;
            LiveNote = live is { } l && l != (uint)Value ? string.Format(Strings.Tune_ProBalanceHolds, Format(l)) : string.Empty;
            var parts = new List<string>();
            if ((dc ? setting.DefaultDc : setting.DefaultAc) is { } d) parts.Add(string.Format(Strings.Tune_WindowsDefault, Format(d)));
            if ((dc ? setting.OriginalDc : setting.OriginalAc) is { } o) parts.Add(string.Format(Strings.Tune_BeforeProGuard, Format(o)));
            Reference = string.Join("  ·  ", parts);
            Refresh();
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnValueChanged(double value)
    {
        var rounded = Math.Clamp(Math.Round(value / Step) * Step, Minimum, Maximum);
        if (rounded != value)
        {
            Value = rounded; // snaps; comes back through here
            return;
        }
        Refresh();
        if (!_loading) _owner.Edit(this, (uint)rounded);
    }

    [RelayCommand]
    private void Pick(ChoiceOption<uint> choice) => Value = choice.Value;

    private void Refresh()
    {
        ValueText = Format((uint)Value);
        foreach (var choice in Choices) choice.IsOn = choice.Value == (uint)Value;
    }

    internal string Format(uint v) => IsChoice
        ? Setting.Choices.FirstOrDefault(c => c.Value == v)?.Name.ToUpperInvariant() ?? v.ToString(CultureInfo.InvariantCulture)
        : string.IsNullOrEmpty(Setting.Unit) ? v.ToString(CultureInfo.InvariantCulture)
        : Setting.Unit == "%" ? $"{v} %" : $"{v} {Setting.Unit}";
}

public sealed record CpuTuningGroup(string Title, IReadOnlyList<CpuTuningRow> Rows);

/// <summary>A preset button: what it does in a word or two, and in a line.</summary>
public sealed record CpuTuningPreset(string Id, string Label, string Blurb);

/// <summary>One kind of core in the live parking view: each logical processor's load, and whether Windows has it parked.</summary>
public sealed partial class CoreParkGroup : ObservableObject
{
    public string Label { get; }
    public IReadOnlyList<int> Indexes { get; }
    [ObservableProperty] private double[] loads = Array.Empty<double>();
    [ObservableProperty] private bool[] parked = Array.Empty<bool>();
    [ObservableProperty] private string summary = string.Empty;

    public CoreParkGroup(string label, IReadOnlyList<int> indexes)
    {
        Label = label;
        Indexes = indexes;
    }

    public void Apply(IReadOnlyList<double> allLoads, IReadOnlyList<bool> allParked)
    {
        Loads = Indexes.Select(i => i < allLoads.Count ? allLoads[i] : 0).ToArray();
        Parked = Indexes.Select(i => i < allParked.Count && allParked[i]).ToArray();
        int unparked = Parked.Count(p => !p);
        Summary = allParked.Count == 0 ? Strings.Tune_ParkingUnavailable : string.Format(Strings.Tune_Unparked, unparked, Indexes.Count);
    }
}

/// <summary>
/// ADVANCED · CPU TUNING: core parking and processor frequency for one power plan at a time, plugged in and on battery,
/// with the live parking state of every core. Changes are saved as they are made (a slider shortly after it stops),
/// machine-wide; the engine refuses them from a standard user, so the page is read-only then.
/// </summary>
public sealed partial class CpuTuningViewModel : ObservableObject, IPage
{
    private readonly EngineHub _hub;
    private readonly ShellViewModel? _shell;
    private readonly Dictionary<string, (uint? Ac, uint? Dc)> _pending = new();
    private CancellationTokenSource? _saveCts;
    private CpuTuningDto? _tuning;
    private bool _coresLoaded;

    /// <summary>How long after the last change a save goes out; tests set zero.</summary>
    internal TimeSpan SaveDelay { get; set; } = TimeSpan.FromMilliseconds(400);
    internal Func<Guid?, Task<CpuTuningDto?>> Loader { get; set; }
    internal Func<CpuTuningArgument, Task<CpuTuningDto?>> Sender { get; set; }
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    public ObservableCollection<ChoiceOption<Guid>> Plans { get; } = new();
    public ObservableCollection<CpuTuningGroup> Groups { get; } = new();
    public ObservableCollection<CoreParkGroup> CoreGroups { get; } = new();

    public IReadOnlyList<CpuTuningPreset> Presets { get; } = new[]
    {
        new CpuTuningPreset(CpuTuningPresets.DisableParking, Strings.Tune_DisableParking, Strings.Tune_DisableParkingBlurb),
        new CpuTuningPreset(CpuTuningPresets.BalancedParking, Strings.Tune_BalancedParking, Strings.Tune_BalancedParkingBlurb),
        new CpuTuningPreset(CpuTuningPresets.WindowsDefaults, Strings.Tune_WindowsDefaults, Strings.Tune_WindowsDefaultsBlurb),
    };

    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private bool isAdministrator = true;
    [ObservableProperty] private bool isDc;
    [ObservableProperty] private Guid planId;
    [ObservableProperty] private string planName = string.Empty;
    [ObservableProperty] private bool isActivePlan;
    [ObservableProperty] private bool hasOriginals;
    [ObservableProperty] private string originalsNote = string.Empty;
    [ObservableProperty] private bool parkingOverrideActive;
    [ObservableProperty] private string warning = string.Empty;

    public CpuTuningViewModel(EngineHub hub, ShellViewModel? shell = null)
    {
        _hub = hub;
        _shell = shell;
        Loader = plan => _hub.QueryAsync<CpuTuningDto>(CommandTypes.GetCpuTuning, argument: plan?.ToString(), quiet: true);
        Sender = argument => _hub.QueryAsync<CpuTuningDto>(CommandTypes.SetCpuTuning, argument: argument);
        hub.SnapshotApplied += OnSnapshot;
    }

    public void OnShown()
    {
        _ = LoadAsync(IsLoaded ? PlanId : null);
        if (!_coresLoaded) _ = LoadCoresAsync();
    }

    public void OnHidden() { }

    private async Task LoadAsync(Guid? plan)
    {
        if (await Loader(plan) is { } tuning) Load(tuning);
    }

    /// <summary>Which logical processors are P-cores and which E-cores, for grouping the live bars.</summary>
    private async Task LoadCoresAsync()
    {
        var sets = await _hub.QueryAsync<List<CpuSetDto>>(CommandTypes.GetCpuSets, quiet: true);
        if (sets == null) return;
        _coresLoaded = true;
        SetCores(sets);
        if (_hub.Snapshot is { } snapshot) OnSnapshot(snapshot);
    }

    internal void SetCores(IReadOnlyList<CpuSetDto> sets)
    {
        CoreGroups.Clear();
        var group0 = sets.Where(s => s.Group == 0).OrderBy(s => s.LogicalProcessorIndex).ToList();
        var classes = group0.Select(s => s.EfficiencyClass).Distinct().OrderByDescending(c => c).ToList();
        if (classes.Count > 1)
        {
            // Higher efficiency class = faster cores.
            CoreGroups.Add(new CoreParkGroup(Strings.PCores, group0.Where(s => s.EfficiencyClass == classes[0]).Select(s => s.LogicalProcessorIndex).ToList()));
            CoreGroups.Add(new CoreParkGroup(Strings.ECores, group0.Where(s => s.EfficiencyClass != classes[0]).Select(s => s.LogicalProcessorIndex).ToList()));
        }
        else
        {
            CoreGroups.Add(new CoreParkGroup(Strings.Tune_AllCores, group0.Select(s => s.LogicalProcessorIndex).ToList()));
        }
    }

    internal void OnSnapshot(EngineSnapshot snapshot)
    {
        IsAdministrator = snapshot.CallerIsAdministrator;
        if (!_coresLoaded && CoreGroups.Count == 0 && snapshot.CoreCpuPercent.Count > 0)
            CoreGroups.Add(new CoreParkGroup(Strings.Tune_AllCores, Enumerable.Range(0, snapshot.CoreCpuPercent.Count).ToList()));
        foreach (var group in CoreGroups) group.Apply(snapshot.CoreCpuPercent, snapshot.CoreParked);
        UpdateWarning();
    }

    /// <summary>Shows a plan's tuning. Rows with a change still to be saved keep what the user set.</summary>
    public void Load(CpuTuningDto tuning)
    {
        bool samePlan = _tuning?.PlanId == tuning.PlanId;
        _tuning = tuning;
        PlanId = tuning.PlanId;
        PlanName = tuning.PlanName.ToUpperInvariant();
        IsActivePlan = tuning.IsActivePlan;
        HasOriginals = tuning.HasOriginals;
        OriginalsNote = tuning.OriginalsTakenUtc is { } taken
            ? string.Format(Strings.Tune_OriginalsKept, taken.ToLocalTime()).ToUpper(CultureInfo.CurrentCulture)
            : Strings.Tune_NoOriginals;
        ParkingOverrideActive = tuning.ParkingOverrideActive;

        Plans.Clear();
        foreach (var plan in tuning.Plans)
            Plans.Add(new ChoiceOption<Guid>(plan.Name.ToUpperInvariant(), plan.Id, plan.Id == tuning.PlanId));

        if (!samePlan || Groups.Count == 0) BuildRows(tuning);
        else
        {
            foreach (var row in Groups.SelectMany(g => g.Rows))
            {
                if (_pending.ContainsKey(row.Key)) continue;
                if (tuning.Settings.FirstOrDefault(s => s.Key == row.Key) is { } setting) row.Load(setting, IsDc);
            }
        }
        IsLoaded = true;
        UpdateWarning();
    }

    private void BuildRows(CpuTuningDto tuning)
    {
        Groups.Clear();
        _pending.Clear();
        foreach (var title in CpuTuningCatalog.Groups)
        {
            // A CPU with one kind of core has no use for the class 1 twins: they're left out rather than listed as unavailable.
            var rows = tuning.Settings.Where(s => s.Group == title && (tuning.IsHybrid || s.EfficiencyClass == 0))
                .Select(s =>
                {
                    // Only a setting Windows has twice (once per efficiency class) is about one kind of core.
                    bool perClass = tuning.IsHybrid && tuning.Settings.Any(o => o.Name == s.Name && o.EfficiencyClass != s.EfficiencyClass);
                    var row = new CpuTuningRow(this, s, perClass);
                    row.Load(s, IsDc);
                    return row;
                }).ToList();
            if (rows.Count > 0) Groups.Add(new CpuTuningGroup(title, rows));
        }
    }

    private void UpdateWarning()
    {
        if (!IsLoaded) return;
        var scope = string.Format(Strings.Tune_Scope, PlanName);
        Warning = string.Format(!IsAdministrator ? Strings.Tune_ReadOnly
            : IsActivePlan ? Strings.Tune_ActivePlan
            : Strings.Tune_InactivePlan, scope);
    }

    partial void OnIsDcChanged(bool value)
    {
        if (_tuning == null) return;
        foreach (var row in Groups.SelectMany(g => g.Rows))
            if (_tuning.Settings.FirstOrDefault(s => s.Key == row.Key) is { } setting) row.Load(setting, value);
    }

    [RelayCommand]
    private void UsePowerSource(string source) => IsDc = source == "DC";

    [RelayCommand]
    private async Task PickPlanAsync(ChoiceOption<Guid> plan)
    {
        if (plan.Value == PlanId) return;
        await PendingSave;
        await LoadAsync(plan.Value);
    }

    /// <summary>A row's value changed on screen: saved shortly, together with any other change made meanwhile.</summary>
    internal void Edit(CpuTuningRow row, uint value)
    {
        var (ac, dc) = _pending.GetValueOrDefault(row.Key);
        _pending[row.Key] = IsDc ? (ac, value) : (value, dc);
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
            await previous; // a newer save carries this change
            return;
        }
        await previous;
        if (_pending.Count == 0) return;

        var argument = new CpuTuningArgument
        {
            PlanId = PlanId,
            Values = _pending.Select(p => new CpuTuningValueDto { Key = p.Key, Ac = p.Value.Ac, Dc = p.Value.Dc }).ToList(),
        };
        _pending.Clear();
        var saved = await Sender(argument);
        if (saved != null) Load(saved);
        else await LoadAsync(PlanId); // refused: show what the plan really has
    }

    [RelayCommand]
    private void ApplyPreset(string preset)
    {
        var info = Presets.FirstOrDefault(p => p.Id == preset);
        if (info == null) return;
        Confirm(info.Label, string.Format(Strings.Tune_PresetConfirm, info.Blurb, PlanName),
            Strings.Apply, new CpuTuningArgument { PlanId = PlanId, Preset = preset });
    }

    [RelayCommand]
    private void Revert() =>
        Confirm(Strings.Revert, string.Format(Strings.Tune_RevertConfirm, PlanName),
            Strings.Revert, new CpuTuningArgument { PlanId = PlanId, Preset = CpuTuningPresets.Revert });

    private void Confirm(string title, string message, string confirm, CpuTuningArgument argument)
    {
        async Task Run()
        {
            await PendingSave;
            if (await Sender(argument) is { } saved)
            {
                Load(saved);
                _shell?.Toaster.Show($"{title} · {PlanName}");
            }
        }

        if (_shell == null) { _ = Run(); return; }
        _shell.Confirm(title, message, confirm, Run);
    }
}
