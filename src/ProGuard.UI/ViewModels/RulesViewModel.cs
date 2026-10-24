using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;
using ProGuard.UI.Resources;

namespace ProGuard.UI.ViewModels;

/// <summary>A labelled value for a row of chip-style choices.</summary>
/// <param name="AdminOnly">The engine only accepts this choice from an administrator: shown with an ADMIN tag and disabled for anyone else.</param>
public sealed record Choice<T>(string Label, T Value, bool AdminOnly = false);

/// <summary>The groups the rule editor is divided into.</summary>
public enum RuleSection { Basics, Cpu, Instances, Boost, Advanced }

/// <summary>What a rule does, as the short tags shown on its row and on its editor section headers.</summary>
internal static class RuleEffects
{
    public static List<string> Parts(ProcessRuleDto d, int logical, RuleSection section)
    {
        var parts = new List<string>();
        switch (section)
        {
            case RuleSection.Basics:
                if (d.DisallowExecution) parts.Add(Strings.Rule_Blocked);
                if (d.KeepRunning) parts.Add(Strings.Rule_KeepRunning);
                if (d.PriorityClass is { } p) parts.Add(string.Format(Strings.Proc_Priority, RuleText.PriorityName(p)));
                if (d.AffinityMask is { } m) parts.Add(string.Format(Strings.Dialog_Cores, Fmt.Affinity(m, logical)));
                if (d.ExcludeFromProBalance) parts.Add(Strings.ProbalanceOff);
                break;

            case RuleSection.Cpu:
                if (d.CpuLimitPercent is { } cap) parts.Add(string.Format(Strings.Dialog_Cap, EditorNumbers.Text(cap)));
                if (d.CpuLimitThresholdPercent is { } threshold)
                {
                    int cores = d.CpuLimitCoresToRemove ?? RuleText.DefaultLimiterCores;
                    int seconds = d.CpuLimitDurationSeconds ?? RuleText.DefaultLimiterDurationSeconds;
                    parts.Add(string.Format(cores == 1 ? Strings.Rule_LimiterOneCore : Strings.Rule_LimiterCores, EditorNumbers.Text(threshold), cores, RuleText.Duration(seconds)));
                }
                if (d.CpuSetIds is { } sets)
                {
                    int count = sets.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    parts.Add(count == 0 ? Strings.Rule_CpuSetsCleared : string.Format(Strings.Rule_CpuSets, count));
                }
                break;

            case RuleSection.Instances:
                if (d.MaxInstances is { } max) parts.Add(string.Format(d.MaxInstancesAllSessions ? Strings.Rule_MaxAllSessions : Strings.Rule_Max, max));
                if (d.InstanceBalancerMode is { } mode)
                {
                    parts.Add(mode == BalancerMode.FixedCoresPerInstance
                        ? string.Format(Strings.Rule_BalanceFixed, d.InstanceBalancerCoresPerInstance ?? RuleText.DefaultCoresPerInstance)
                        : Strings.Rule_BalanceEqual);
                    if (d.InstanceBalancerAvoidSmt) parts.Add(Strings.AvoidSmt);
                    if (d.InstanceBalancerReservedCores is > 0 and var reserved) parts.Add(string.Format(Strings.Rule_Reserve, reserved));
                    if (d.InstanceBalancerMinCpuPercent is > 0 and var minCpu) parts.Add(string.Format(Strings.Rule_BalanceMinCpu, EditorNumbers.Text(minCpu)));
                }
                break;

            case RuleSection.Boost:
                if (d.StartupBoostPriorityClass is { } boost)
                    parts.Add(string.Format(Strings.Rule_Boost, RuleText.PriorityName(boost), RuleText.Duration(d.StartupBoostSeconds ?? RuleText.DefaultBoostSeconds)));
                break;

            case RuleSection.Advanced:
                if (d.IoPriority is { } io) parts.Add(string.Format(Strings.Proc_Io, RuleText.IoPriorityName(io)));
                if (d.MemoryPriority is { } mem) parts.Add(string.Format(Strings.Proc_Memory, RuleText.MemoryPriorityName(mem)));
                if (d.EfficiencyMode is { } eco) parts.Add(eco ? Strings.Rule_EcoOn : Strings.Rule_EcoOff);
                if (d.DisablePriorityBoost is { } noBoost) parts.Add(noBoost ? Strings.Rule_ThreadBoostOff : Strings.Rule_ThreadBoostOn);
                if (d.GpuPriority is { } gpu) parts.Add(string.Format(Strings.Rule_Gpu, RuleText.GpuPriorityName(gpu)));
                if (d.GpuPreference is { } pref) parts.Add(string.Format(Strings.Rule_GpuPref, RuleText.GpuPreferenceName(pref)));
                if (d.ReapplyIfChanged) parts.Add(Strings.Rule_Reapply);
                if (d.ApplyPrioritiesAtCreation) parts.Add(Strings.Rule_AtLaunch);
                break;
        }
        return parts;
    }

    public static List<string> All(ProcessRuleDto d, int logical) =>
        Enum.GetValues<RuleSection>().SelectMany(s => Parts(d, logical, s)).ToList();
}

public sealed class RuleRow
{
    public ProcessRuleDto Dto { get; }
    public string Name => RuleText.Display(Dto.ProcessName).ToUpperInvariant();
    public IReadOnlyList<string> Effects { get; }

    public RuleRow(ProcessRuleDto dto, int logical)
    {
        Dto = dto;
        var effects = RuleEffects.All(dto, logical);
        if (effects.Count == 0) effects.Add(Strings.Rule_NoEffect);
        Effects = effects;
    }
}

/// <summary>The rule being created or changed in the side panel. Every <see cref="ProcessRuleDto"/> setting is here.</summary>
public sealed partial class RuleEditor : ObservableObject
{
    private readonly int _logical;
    private bool _ready;
    private bool _refreshing;

    /// <summary>The rule being edited, as loaded; anything a hidden control owns is sent back unchanged.</summary>
    private readonly ProcessRuleDto _original = new();

    public int Id { get; init; }
    public bool IsNew => Id == 0;
    public string Heading => IsNew ? Strings.Rule_NewRule : Strings.Rule_EditRule;

    // ---- the choices on offer (a stored value that isn't one of them is added, so loading never changes it) ----
    public IReadOnlyList<Choice<ProcessPriorityClass?>> Priorities { get; }
    public IReadOnlyList<Choice<ProcessPriorityClass?>> BoostPriorities { get; }
    public IReadOnlyList<Choice<BalancerMode?>> BalancerModes { get; } = new Choice<BalancerMode?>[]
    {
        new(Strings.Off, null),
        new(Strings.Rule_Equal, BalancerMode.EqualCoresPerInstance),
        new(Strings.Rule_Fixed, BalancerMode.FixedCoresPerInstance),
    };
    public IReadOnlyList<Choice<int?>> IoPriorities { get; }
    public IReadOnlyList<Choice<int?>> MemoryPriorities { get; }
    public IReadOnlyList<Choice<int?>> GpuPriorities { get; }
    public IReadOnlyList<Choice<int?>> GpuPreferences { get; }
    public IReadOnlyList<Choice<bool?>> EfficiencyModes { get; } = new Choice<bool?>[] { new(Strings.Rule_Leave, null), new(Strings.On, true), new(Strings.Off, false) };
    public IReadOnlyList<Choice<bool?>> ThreadBoosts { get; } = new Choice<bool?>[] { new(Strings.Rule_Leave, null), new(Strings.Rule_Disable, true), new(Strings.Rule_Allow, false) };

    public CorePicker CorePicker { get; }
    public ObservableCollection<CoreToggle> Cores => CorePicker.Cores;

    // ---- basics ----
    [ObservableProperty] private string processName = string.Empty;
    [ObservableProperty] private Choice<ProcessPriorityClass?> priority;
    [ObservableProperty] private bool affinityEnabled;
    [ObservableProperty] private bool excludeFromProBalance;
    [ObservableProperty] private bool disallowExecution;
    [ObservableProperty] private bool keepRunning;

    // ---- cpu ----
    [ObservableProperty] private bool limitEnabled;
    [ObservableProperty] private double limit = 25;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LimiterOn))] private string limiterThreshold = string.Empty;
    [ObservableProperty] private string limiterSeconds = string.Empty;
    [ObservableProperty] private string limiterCores = string.Empty;
    [ObservableProperty] private string limiterDuration = string.Empty;
    /// <summary>Null leaves the process's CPU Sets alone; empty clears them; otherwise comma-separated CPU Set IDs.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CpuSetsText))] private string? cpuSetIds;

    // ---- instances ----
    [ObservableProperty] private string maxInstances = string.Empty;
    [ObservableProperty] private bool maxInstancesAllSessions;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BalancerOn), nameof(BalancerFixed))] private Choice<BalancerMode?> balancer;
    [ObservableProperty] private string balancerCores = string.Empty;
    [ObservableProperty] private bool balancerAvoidSmt;
    [ObservableProperty] private string balancerReserved = string.Empty;
    [ObservableProperty] private string balancerMinCpu = string.Empty;

    // ---- startup boost ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BoostOn))] private Choice<ProcessPriorityClass?> boostPriority;
    [ObservableProperty] private string boostSeconds = string.Empty;

    // ---- advanced priorities ----
    [ObservableProperty] private Choice<int?> ioPriority;
    [ObservableProperty] private Choice<int?> memoryPriority;
    [ObservableProperty] private Choice<bool?> efficiencyMode;
    [ObservableProperty] private Choice<bool?> threadBoost;
    [ObservableProperty] private Choice<int?> gpuPriority;
    [ObservableProperty] private Choice<int?> gpuPreference;
    [ObservableProperty] private bool reapplyIfChanged;
    /// <summary>Have Windows apply the priorities as the program starts (Image File Execution Options). Administrators only.</summary>
    [ObservableProperty] private bool applyAtLaunch;

    // ---- access ----
    /// <summary>True for a user without administrator rights: the engine refuses some contents, so those are marked and the limits listed.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSetAtLaunch))] private bool isRestricted;

    /// <summary>"Set at launch" writes a machine-wide registry setting, which the engine takes only from administrators.</summary>
    public bool CanSetAtLaunch => !IsRestricted;

    /// <summary>What the engine refuses a standard user. Shown as one line in the editor while <see cref="IsRestricted"/>.</summary>
    public static string LimitsHint =>
        Strings.Rule_LimitsHint;
    public string RestrictionHint => LimitsHint;

    // ---- collapsed-section summaries ----
    [ObservableProperty] private string basicsSummary = string.Empty;
    [ObservableProperty] private string cpuSummary = string.Empty;
    [ObservableProperty] private string instancesSummary = string.Empty;
    [ObservableProperty] private string boostSummary = string.Empty;
    [ObservableProperty] private string advancedSummary = string.Empty;

    public bool LimiterOn => !string.IsNullOrWhiteSpace(LimiterThreshold);
    public bool BalancerOn => Balancer.Value.HasValue;
    public bool BalancerFixed => Balancer.Value == BalancerMode.FixedCoresPerInstance;
    public bool BoostOn => BoostPriority.Value.HasValue;

    public string CpuSetsText => CpuSetIds == null ? Strings.LeaveAlone
        : CpuSetIds.Trim().Length == 0 ? Strings.Rule_CpuSetsNone
        : string.Format(Strings.Rule_CpuSetsIds, CpuSetCount(CpuSetIds), CpuSetIds.Trim());

    public RuleEditor(int logical, ProcessRuleDto? from = null)
    {
        _logical = logical;
        var priorities = PriorityChoices(Strings.Rule_Leave);
        var boostPriorities = PriorityChoices(Strings.Off);
        var io = IntChoices(Strings.Rule_Leave, RuleText.IoPriorityName, 0, 3, adminFrom: 3);
        var memory = IntChoices(Strings.Rule_Leave, RuleText.MemoryPriorityName, 1, 5);
        var gpu = IntChoices(Strings.Rule_Leave, RuleText.GpuPriorityName, 0, 4);
        var preference = IntChoices(Strings.Rule_Leave, RuleText.GpuPreferenceName, 0, 2);

        if (from != null)
        {
            _original = DtoCopy.Clone(from);
            Id = from.Id;
        }

        priority = Pick(priorities, from?.PriorityClass);
        boostPriority = Pick(boostPriorities, from?.StartupBoostPriorityClass);
        ioPriority = Pick(io, from?.IoPriority);
        memoryPriority = Pick(memory, from?.MemoryPriority);
        gpuPriority = Pick(gpu, from?.GpuPriority);
        gpuPreference = Pick(preference, from?.GpuPreference);
        balancer = BalancerModes.FirstOrDefault(c => c.Value == from?.InstanceBalancerMode) ?? BalancerModes[0];
        efficiencyMode = EfficiencyModes.First(c => c.Value == from?.EfficiencyMode);
        threadBoost = ThreadBoosts.First(c => c.Value == from?.DisablePriorityBoost);

        Priorities = priorities;
        BoostPriorities = boostPriorities;
        IoPriorities = io;
        MemoryPriorities = memory;
        GpuPriorities = gpu;
        GpuPreferences = preference;

        CorePicker = new CorePicker(logical, from?.AffinityMask);
        CorePicker.Changed += Refresh;

        if (from != null)
        {
            processName = RuleText.Display(from.ProcessName);
            affinityEnabled = from.AffinityMask.HasValue;
            excludeFromProBalance = from.ExcludeFromProBalance;
            disallowExecution = from.DisallowExecution;
            keepRunning = from.KeepRunning;

            limitEnabled = from.CpuLimitPercent.HasValue;
            limit = from.CpuLimitPercent ?? 25;
            limiterThreshold = from.CpuLimitThresholdPercent is { } t ? EditorNumbers.Text(t) : string.Empty;
            limiterSeconds = from.CpuLimitOverThresholdSeconds?.ToString() ?? string.Empty;
            limiterCores = from.CpuLimitCoresToRemove?.ToString() ?? string.Empty;
            limiterDuration = from.CpuLimitDurationSeconds?.ToString() ?? string.Empty;
            cpuSetIds = from.CpuSetIds;

            maxInstances = from.MaxInstances?.ToString() ?? string.Empty;
            maxInstancesAllSessions = from.MaxInstancesAllSessions;
            balancerCores = from.InstanceBalancerCoresPerInstance?.ToString() ?? string.Empty;
            balancerAvoidSmt = from.InstanceBalancerAvoidSmt;
            balancerReserved = from.InstanceBalancerReservedCores?.ToString() ?? string.Empty;
            balancerMinCpu = from.InstanceBalancerMinCpuPercent is { } min ? EditorNumbers.Text(min) : string.Empty;

            boostSeconds = from.StartupBoostSeconds?.ToString() ?? string.Empty;
            reapplyIfChanged = from.ReapplyIfChanged;
            applyAtLaunch = from.ApplyPrioritiesAtCreation;
        }

        _ready = true;
        Refresh();
    }

    public int LimitWhole => (int)Math.Round(Limit);
    partial void OnLimitChanged(double value) => OnPropertyChanged(nameof(LimitWhole));

    /// <summary>Sends the editor back to leaving the process's CPU Sets as they are.</summary>
    [RelayCommand] private void LeaveCpuSets() => CpuSetIds = null;

    /// <summary>An empty CPU Set list: the rule actively clears any CPU Sets the process has.</summary>
    [RelayCommand] private void ClearCpuSets() => CpuSetIds = string.Empty;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_ready || _refreshing) return;
        if (e.PropertyName is nameof(BasicsSummary) or nameof(CpuSummary) or nameof(InstancesSummary) or nameof(BoostSummary) or nameof(AdvancedSummary)) return;
        Refresh();
    }

    private void Refresh()
    {
        if (!_ready || _refreshing) return;
        _refreshing = true;
        try
        {
            TryBuild(out var draft, out _);
            BasicsSummary = Summarise(draft, RuleSection.Basics, Strings.Rule_NothingSet);
            CpuSummary = Summarise(draft, RuleSection.Cpu, Strings.Rule_NoLimits);
            InstancesSummary = Summarise(draft, RuleSection.Instances, Strings.Rule_NoLimits);
            BoostSummary = Summarise(draft, RuleSection.Boost, Strings.Off);
            AdvancedSummary = Summarise(draft, RuleSection.Advanced, Strings.Rule_Unchanged);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private string Summarise(ProcessRuleDto draft, RuleSection section, string none)
    {
        var parts = RuleEffects.Parts(draft, _logical, section);
        return parts.Count == 0 ? none : string.Join(" · ", parts);
    }

    /// <summary>The rule as it stands, ignoring any mistakes. Use <see cref="TryBuild"/> to find out about them.</summary>
    public ProcessRuleDto ToDto()
    {
        TryBuild(out var dto, out _);
        return dto;
    }

    /// <summary>
    /// Builds the rule to save: a copy of the loaded one with the editor's values written over it. Returns false, with
    /// a message that says what to fix, when a value is out of range; the dto is still filled in as far as it can be.
    /// </summary>
    public bool TryBuild(out ProcessRuleDto dto, out string? error)
    {
        string? problem = null;
        void Fail(string message) => problem ??= message;

        int? OptionalInt(string? text, int min, int max, string message)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (EditorNumbers.TryInt(text, out var n) && n >= min && n <= max) return n;
            Fail(message);
            return null;
        }

        double? OptionalPercent(string? text, double min, double max, string message)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (EditorNumbers.TryDouble(text, out var n) && n >= min && n <= max) return n;
            Fail(message);
            return null;
        }

        var d = DtoCopy.Clone(_original);
        d.Id = Id;

        // ---- basics
        var name = ProcessName.Trim();
        if (name.Length == 0) Fail(Strings.Rule_NeedName);
        d.ProcessName = RuleText.Display(name);

        d.PriorityClass = (int?)Priority.Value;
        if (d.PriorityClass is { } pc && !Enum.IsDefined(typeof(ProcessPriorityClass), pc))
            Fail(string.Format(Strings.Rule_BadPriority, pc));

        if (AffinityEnabled)
        {
            if (!CorePicker.AnyOn)
            {
                Fail(Strings.Rule_NeedCore);
                d.AffinityMask = null;
            }
            else d.AffinityMask = CorePicker.MaskKeeping(_original.AffinityMask);
        }
        else d.AffinityMask = null;

        d.ExcludeFromProBalance = ExcludeFromProBalance;
        d.DisallowExecution = DisallowExecution;
        d.KeepRunning = KeepRunning;
        if (DisallowExecution && KeepRunning)
            Fail(Strings.Rule_BlockAndKeep);

        // ---- cpu: hard cap
        d.CpuLimitPercent = LimitEnabled ? Math.Clamp(LimitWhole, 1, 99) : null;

        // ---- cpu: limiter (affinity reduction). Off while the threshold is empty.
        int maxRemove = Math.Max(1, _logical - 1);
        if (string.IsNullOrWhiteSpace(LimiterThreshold))
        {
            d.CpuLimitThresholdPercent = null;
            d.CpuLimitOverThresholdSeconds = null;
            d.CpuLimitCoresToRemove = null;
            d.CpuLimitDurationSeconds = null;
        }
        else
        {
            if (EditorNumbers.TryDouble(LimiterThreshold, out var threshold) && threshold > 0 && threshold <= 100)
                d.CpuLimitThresholdPercent = threshold;
            else
            {
                d.CpuLimitThresholdPercent = null;
                Fail(Strings.Rule_BadThreshold);
            }
            d.CpuLimitOverThresholdSeconds = OptionalInt(LimiterSeconds, 0, 86400,
                Strings.Rule_BadOverFor);
            d.CpuLimitCoresToRemove = OptionalInt(LimiterCores, 1, maxRemove,
                _logical > 1 ? string.Format(Strings.Rule_BadCoresToRemove, maxRemove, _logical)
                             : Strings.Rule_NeedCoreToRemove);
            d.CpuLimitDurationSeconds = OptionalInt(LimiterDuration, 1, 86400,
                Strings.Rule_BadDuration);
        }

        // ---- cpu: CPU Sets
        d.CpuSetIds = CpuSetIds;
        if (!string.IsNullOrWhiteSpace(CpuSetIds) && !CpuSetDialogViewModel.TryParseIds(CpuSetIds, out _))
            Fail(Strings.Rule_BadCpuSets);

        // ---- instances
        d.MaxInstances = OptionalInt(MaxInstances, 1, 100000, Strings.Rule_BadMaxInstances);
        d.MaxInstancesAllSessions = MaxInstancesAllSessions;

        d.InstanceBalancerMode = Balancer.Value;
        if (Balancer.Value is { } mode)
        {
            if (mode == BalancerMode.FixedCoresPerInstance)
                d.InstanceBalancerCoresPerInstance = OptionalInt(BalancerCores, 1, Math.Max(1, _logical),
                    string.Format(Strings.Rule_BadCoresPerInstance, Math.Max(1, _logical)));
            d.InstanceBalancerAvoidSmt = BalancerAvoidSmt;
            d.InstanceBalancerReservedCores = OptionalInt(BalancerReserved, 0, Math.Max(0, _logical - 1),
                _logical > 1 ? string.Format(Strings.Rule_BadReserved, _logical - 1)
                             : Strings.Rule_OneCoreNoReserve);
            d.InstanceBalancerMinCpuPercent = OptionalPercent(BalancerMinCpu, 0, 100, Strings.Rule_BadMinCpu);
        }

        // ---- startup boost
        d.StartupBoostPriorityClass = (int?)BoostPriority.Value;
        if (d.StartupBoostPriorityClass is { } boost)
        {
            if (!Enum.IsDefined(typeof(ProcessPriorityClass), boost))
                Fail(string.Format(Strings.Rule_BadBoostPriority, boost));
            d.StartupBoostSeconds = OptionalInt(BoostSeconds, 1, 3600, Strings.Rule_BadBoostSeconds);
        }

        // ---- advanced priorities
        d.IoPriority = IoPriority.Value;
        d.MemoryPriority = MemoryPriority.Value;
        d.EfficiencyMode = EfficiencyMode.Value;
        d.DisablePriorityBoost = ThreadBoost.Value;
        d.GpuPriority = GpuPriority.Value;
        d.GpuPreference = GpuPreference.Value;
        d.ReapplyIfChanged = ReapplyIfChanged;
        d.ApplyPrioritiesAtCreation = ApplyAtLaunch;

        if (d.IoPriority is < 0 or > 3) Fail(Strings.Rule_BadIo);
        if (d.MemoryPriority is < 1 or > 5) Fail(Strings.Rule_BadMemory);
        if (d.GpuPriority is < 0 or > 4) Fail(Strings.Rule_BadGpu);
        if (d.GpuPreference is < 0 or > 2) Fail(Strings.Rule_BadGpuPref);

        if (d.ApplyPrioritiesAtCreation)
        {
            if (!RuleText.IsPlainProgramName(d.ProcessName))
                Fail(Strings.Rule_LaunchNeedsPlainName);
            else if (d.PriorityClass == (int)ProcessPriorityClass.RealTime)
                Fail(Strings.Rule_LaunchNoRealtime);
            else if (d.PriorityClass == null && d.IoPriority == null && d.MemoryPriority == null)
                Fail(Strings.Rule_LaunchNeedsSomething);
        }

        dto = d;
        error = problem;
        return problem == null;
    }

    private static int CpuSetCount(string ids) => ids.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;

    private static List<Choice<ProcessPriorityClass?>> PriorityChoices(string none) => new()
    {
        new(none, null),
        new(Strings.Idle2, ProcessPriorityClass.Idle),
        new(Strings.Low2, ProcessPriorityClass.BelowNormal),
        new(Strings.Normal2, ProcessPriorityClass.Normal),
        new(Strings.Above, ProcessPriorityClass.AboveNormal),
        new(Strings.High2, ProcessPriorityClass.High),
        new(Strings.Realtime2, ProcessPriorityClass.RealTime, AdminOnly: true),
    };

    /// <param name="adminFrom">Values from this one up are administrator-only (I/O priority High).</param>
    private static List<Choice<int?>> IntChoices(string none, Func<int, string> name, int min, int max, int? adminFrom = null)
    {
        var list = new List<Choice<int?>> { new(none, null) };
        for (int i = min; i <= max; i++) list.Add(new Choice<int?>(name(i), i, AdminOnly: i >= adminFrom));
        return list;
    }

    private static Choice<ProcessPriorityClass?> Pick(List<Choice<ProcessPriorityClass?>> list, int? current)
    {
        if (current == null) return list[0];
        var found = list.FirstOrDefault(c => (int?)c.Value == current);
        if (found != null) return found;
        var extra = new Choice<ProcessPriorityClass?>(RuleText.PriorityName(current.Value), (ProcessPriorityClass)current.Value);
        list.Add(extra);
        return extra;
    }

    private static Choice<int?> Pick(List<Choice<int?>> list, int? current)
    {
        if (current == null) return list[0];
        var found = list.FirstOrDefault(c => c.Value == current);
        if (found != null) return found;
        var extra = new Choice<int?>(string.Format(Strings.Rule_OutOfRange, current), current);
        list.Add(extra);
        return extra;
    }
}

/// <summary>Persistent per-program rules: listed on the left, edited in a panel on the right.</summary>
public sealed partial class RulesViewModel : ObservableObject, IPage
{
    private readonly EngineHub _hub;
    private readonly ShellViewModel _shell;

    public ObservableCollection<RuleRow> Rules { get; } = new();

    [ObservableProperty] private RuleEditor? editor;
    [ObservableProperty] private bool isLoaded;

    /// <summary>True when the engine says this UI isn't an administrator: editing stays open, but some contents are refused.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSetAtLaunch))] private bool isRestricted;

    /// <summary>"Set at launch" writes a machine-wide registry setting, which the engine takes only from administrators.</summary>
    public bool CanSetAtLaunch => !IsRestricted;

    public RulesViewModel(EngineHub hub, ShellViewModel shell)
    {
        _hub = hub;
        _shell = shell;
        hub.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EngineHub.Snapshot)) UpdateAccess();
        };
        UpdateAccess();
    }

    private int Logical => _hub.Snapshot?.LogicalProcessors is > 0 and var n ? n : Environment.ProcessorCount;

    /// <summary>Re-reads whether the caller is an administrator from the newest snapshot.</summary>
    internal void UpdateAccess() => IsRestricted = _hub.Snapshot?.CallerIsAdministrator == false;

    partial void OnIsRestrictedChanged(bool value)
    {
        if (Editor != null) Editor.IsRestricted = value;
    }

    partial void OnEditorChanged(RuleEditor? value)
    {
        if (value != null) value.IsRestricted = IsRestricted;
    }

    public void OnShown() => _ = LoadAsync();
    public void OnHidden() { }

    private async Task LoadAsync()
    {
        var rules = await _hub.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules, quiet: true);
        if (rules == null) return;
        Rules.Clear();
        foreach (var rule in rules.OrderBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)) Rules.Add(new RuleRow(rule, Logical));
        IsLoaded = true;
    }

    /// <summary>
    /// Opens the editor pre-filled from a running process (the "make a rule" action, <paramref name="seed"/> holding what
    /// the process has now), or on that program's existing rule. The list loads only when the Rules page is shown, so it
    /// is fetched first: otherwise a program that already has a rule would get a NEW RULE editor, which the engine then
    /// refuses as a duplicate.
    /// </summary>
    public async Task StartNewAsync(ProcessRuleDto seed)
    {
        await LoadAsync();
        Editor = new RuleEditor(Logical, RuleText.RuleFor(Rules.Select(r => r.Dto), seed.ProcessName) ?? seed);
    }

    [RelayCommand]
    private void New() => Editor = new RuleEditor(Logical);

    [RelayCommand]
    private void Edit(RuleRow row) => Editor = new RuleEditor(Logical, row.Dto);

    [RelayCommand]
    private void Cancel() => Editor = null;

    [RelayCommand]
    private async Task PickCpuSetsAsync()
    {
        if (Editor is not { } editor) return;
        var sets = await _hub.QueryAsync<List<CpuSetDto>>(CommandTypes.GetCpuSets);
        if (sets == null) return;
        if (sets.Count == 0)
        {
            _shell.Toaster.Show(Strings.Rule_NoCpuSets, isError: true);
            return;
        }

        var program = editor.ProcessName.Trim();
        _shell.ShowDialog(new CpuSetDialogViewModel(_shell, program.Length == 0 ? Strings.Rule_ThisRule : RuleText.Display(program).ToUpperInvariant(),
            sets, editor.CpuSetIds, ids => editor.CpuSetIds = ids));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editor is not { } editor) return;
        if (!editor.TryBuild(out var dto, out var error))
        {
            _shell.Toaster.Show(error!, isError: true);
            return;
        }

        var saved = await _hub.QueryAsync<ProcessRuleDto>(CommandTypes.SaveProcessRule, argument: dto);
        if (saved == null) return;

        _shell.Toaster.Show(string.Format(Strings.Rule_Saved, RuleText.Display(saved.ProcessName)));
        Editor = null;
        await LoadAsync();
        _hub.RefreshNow();
    }

    [RelayCommand]
    private void Delete(RuleRow row)
    {
        _shell.Confirm(Strings.Rule_DeleteTitle, string.Format(Strings.Rule_DeleteWarning, row.Name), Strings.Delete,
            async () =>
            {
                if (await _hub.RunAsync(CommandTypes.DeleteProcessRule, argument: row.Dto.Id, success: string.Format(Strings.Rule_Deleted, row.Name)))
                {
                    if (Editor?.Id == row.Dto.Id) Editor = null;
                    await LoadAsync();
                }
            });
    }
}
