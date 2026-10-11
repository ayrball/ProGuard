using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;

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
                if (d.DisallowExecution) parts.Add("BLOCKED");
                if (d.KeepRunning) parts.Add("KEEP RUNNING");
                if (d.PriorityClass is { } p) parts.Add($"PRIORITY {RuleText.PriorityName(p)}");
                if (d.AffinityMask is { } m) parts.Add($"CORES {Fmt.Affinity(m, logical)}");
                if (d.ExcludeFromProBalance) parts.Add("PROBALANCE OFF");
                break;

            case RuleSection.Cpu:
                if (d.CpuLimitPercent is { } cap) parts.Add($"CAP {EditorNumbers.Text(cap)}%");
                if (d.CpuLimitThresholdPercent is { } threshold)
                {
                    int cores = d.CpuLimitCoresToRemove ?? RuleText.DefaultLimiterCores;
                    int seconds = d.CpuLimitDurationSeconds ?? RuleText.DefaultLimiterDurationSeconds;
                    parts.Add($"LIMITER ≥{EditorNumbers.Text(threshold)}% →-{cores} {(cores == 1 ? "CORE" : "CORES")} {RuleText.Duration(seconds)}");
                }
                if (d.CpuSetIds is { } sets)
                {
                    int count = sets.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    parts.Add(count == 0 ? "CPU SETS CLEARED" : $"CPU SETS {count}");
                }
                break;

            case RuleSection.Instances:
                if (d.MaxInstances is { } max) parts.Add($"MAX {max}{(d.MaxInstancesAllSessions ? " (ALL SESSIONS)" : "")}");
                if (d.InstanceBalancerMode is { } mode)
                {
                    parts.Add(mode == BalancerMode.FixedCoresPerInstance
                        ? $"BALANCE FIXED {d.InstanceBalancerCoresPerInstance ?? RuleText.DefaultCoresPerInstance}"
                        : "BALANCE EQUAL");
                    if (d.InstanceBalancerAvoidSmt) parts.Add("AVOID SMT");
                    if (d.InstanceBalancerReservedCores is > 0 and var reserved) parts.Add($"RESERVE {reserved}");
                    if (d.InstanceBalancerMinCpuPercent is > 0 and var minCpu) parts.Add($"BALANCE ≥{EditorNumbers.Text(minCpu)}% CPU");
                }
                break;

            case RuleSection.Boost:
                if (d.StartupBoostPriorityClass is { } boost)
                    parts.Add($"BOOST {RuleText.PriorityName(boost)} {RuleText.Duration(d.StartupBoostSeconds ?? RuleText.DefaultBoostSeconds)}");
                break;

            case RuleSection.Advanced:
                if (d.IoPriority is { } io) parts.Add($"I/O {RuleText.IoPriorityName(io)}");
                if (d.MemoryPriority is { } mem) parts.Add($"MEMORY {RuleText.MemoryPriorityName(mem)}");
                if (d.EfficiencyMode is { } eco) parts.Add(eco ? "ECO ON" : "ECO OFF");
                if (d.DisablePriorityBoost is { } noBoost) parts.Add(noBoost ? "THREAD BOOST OFF" : "THREAD BOOST ON");
                if (d.GpuPriority is { } gpu) parts.Add($"GPU {RuleText.GpuPriorityName(gpu)}");
                if (d.GpuPreference is { } pref) parts.Add($"GPU PREF {RuleText.GpuPreferenceName(pref)}");
                if (d.ReapplyIfChanged) parts.Add("RE-APPLY");
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
        if (effects.Count == 0) effects.Add("NO EFFECT");
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
    public string Heading => IsNew ? "NEW RULE" : "EDIT RULE";

    // ---- the choices on offer (a stored value that isn't one of them is added, so loading never changes it) ----
    public IReadOnlyList<Choice<ProcessPriorityClass?>> Priorities { get; }
    public IReadOnlyList<Choice<ProcessPriorityClass?>> BoostPriorities { get; }
    public IReadOnlyList<Choice<BalancerMode?>> BalancerModes { get; } = new Choice<BalancerMode?>[]
    {
        new("OFF", null),
        new("EQUAL", BalancerMode.EqualCoresPerInstance),
        new("FIXED", BalancerMode.FixedCoresPerInstance),
    };
    public IReadOnlyList<Choice<int?>> IoPriorities { get; }
    public IReadOnlyList<Choice<int?>> MemoryPriorities { get; }
    public IReadOnlyList<Choice<int?>> GpuPriorities { get; }
    public IReadOnlyList<Choice<int?>> GpuPreferences { get; }
    public IReadOnlyList<Choice<bool?>> EfficiencyModes { get; } = new Choice<bool?>[] { new("LEAVE", null), new("ON", true), new("OFF", false) };
    public IReadOnlyList<Choice<bool?>> ThreadBoosts { get; } = new Choice<bool?>[] { new("LEAVE", null), new("DISABLE", true), new("ALLOW", false) };

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

    // ---- access ----
    /// <summary>True for a user without administrator rights: the engine refuses some contents, so those are marked and the limits listed.</summary>
    [ObservableProperty] private bool isRestricted;

    /// <summary>What the engine refuses a standard user. Shown as one line in the editor while <see cref="IsRestricted"/>.</summary>
    public const string LimitsHint =
        "STANDARD RIGHTS. THE ENGINE REFUSES: SYSTEM PROGRAMS AND PROGUARD ITSELF, REALTIME PRIORITY, BLOCKING OR LIMITING PROGRAMS YOU COULDN'T END YOURSELF, AND CHANGING RULES YOU COULDN'T HAVE MADE.";
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

    public string CpuSetsText => CpuSetIds == null ? "LEAVE ALONE"
        : CpuSetIds.Trim().Length == 0 ? "CLEARED: NO CPU SET RESTRICTION"
        : $"{CpuSetCount(CpuSetIds)} CPU SETS · IDS {CpuSetIds.Trim()}";

    public RuleEditor(int logical, ProcessRuleDto? from = null)
    {
        _logical = logical;
        var priorities = PriorityChoices("LEAVE");
        var boostPriorities = PriorityChoices("OFF");
        var io = IntChoices("LEAVE", RuleText.IoPriorityName, 0, 3);
        var memory = IntChoices("LEAVE", RuleText.MemoryPriorityName, 1, 5);
        var gpu = IntChoices("LEAVE", RuleText.GpuPriorityName, 0, 4);
        var preference = IntChoices("LEAVE", RuleText.GpuPreferenceName, 0, 2);

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
            BasicsSummary = Summarise(draft, RuleSection.Basics, "NOTHING SET");
            CpuSummary = Summarise(draft, RuleSection.Cpu, "NO LIMITS");
            InstancesSummary = Summarise(draft, RuleSection.Instances, "NO LIMITS");
            BoostSummary = Summarise(draft, RuleSection.Boost, "OFF");
            AdvancedSummary = Summarise(draft, RuleSection.Advanced, "UNCHANGED");
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
        if (name.Length == 0) Fail("Name the program the rule is for, e.g. chrome.exe.");
        d.ProcessName = RuleText.Display(name);

        d.PriorityClass = (int?)Priority.Value;
        if (d.PriorityClass is { } pc && !Enum.IsDefined(typeof(ProcessPriorityClass), pc))
            Fail($"Priority {pc} isn't a Windows priority class. Pick one of the priority buttons.");

        if (AffinityEnabled)
        {
            if (!CorePicker.AnyOn)
            {
                Fail("Pick at least one core, or switch off \"Keep it on certain cores\".");
                d.AffinityMask = null;
            }
            else d.AffinityMask = CorePicker.MaskKeeping(_original.AffinityMask);
        }
        else d.AffinityMask = null;

        d.ExcludeFromProBalance = ExcludeFromProBalance;
        d.DisallowExecution = DisallowExecution;
        d.KeepRunning = KeepRunning;
        if (DisallowExecution && KeepRunning)
            Fail("A rule can't both block a program and keep it running. Switch one of them off.");

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
                Fail("The CPU Limiter threshold is a percentage above 0 and up to 100, e.g. 40. Empty it to switch the limiter off.");
            }
            d.CpuLimitOverThresholdSeconds = OptionalInt(LimiterSeconds, 0, 86400,
                "The CPU Limiter's \"over for\" time is a whole number of seconds, 0 or more.");
            d.CpuLimitCoresToRemove = OptionalInt(LimiterCores, 1, maxRemove,
                _logical > 1 ? $"The CPU Limiter can remove 1 to {maxRemove} cores on this {_logical}-thread machine."
                             : "The CPU Limiter needs at least 1 core to remove.");
            d.CpuLimitDurationSeconds = OptionalInt(LimiterDuration, 1, 86400,
                "The CPU Limiter's duration is a whole number of seconds, 1 or more.");
        }

        // ---- cpu: CPU Sets
        d.CpuSetIds = CpuSetIds;
        if (!string.IsNullOrWhiteSpace(CpuSetIds) && !CpuSetDialogViewModel.TryParseIds(CpuSetIds, out _))
            Fail("CPU Sets has to be a comma-separated list of CPU Set IDs. Use Pick to choose them.");

        // ---- instances
        d.MaxInstances = OptionalInt(MaxInstances, 1, 100000, "Max instances is a whole number of 1 or more. Leave it empty for no limit.");
        d.MaxInstancesAllSessions = MaxInstancesAllSessions;

        d.InstanceBalancerMode = Balancer.Value;
        if (Balancer.Value is { } mode)
        {
            if (mode == BalancerMode.FixedCoresPerInstance)
                d.InstanceBalancerCoresPerInstance = OptionalInt(BalancerCores, 1, Math.Max(1, _logical),
                    $"Cores per instance is a whole number from 1 to {Math.Max(1, _logical)}.");
            d.InstanceBalancerAvoidSmt = BalancerAvoidSmt;
            d.InstanceBalancerReservedCores = OptionalInt(BalancerReserved, 0, Math.Max(0, _logical - 1),
                _logical > 1 ? $"Reserved cores is a whole number from 0 to {_logical - 1}: at least one core has to stay free for the instances."
                             : "There is only one core, so none can be reserved.");
            d.InstanceBalancerMinCpuPercent = OptionalPercent(BalancerMinCpu, 0, 100, "The minimum CPU is a percentage from 0 to 100.");
        }

        // ---- startup boost
        d.StartupBoostPriorityClass = (int?)BoostPriority.Value;
        if (d.StartupBoostPriorityClass is { } boost)
        {
            if (!Enum.IsDefined(typeof(ProcessPriorityClass), boost))
                Fail($"Startup boost priority {boost} isn't a Windows priority class. Pick one of the buttons.");
            d.StartupBoostSeconds = OptionalInt(BoostSeconds, 1, 3600, "A startup boost lasts 1 to 3600 seconds.");
        }

        // ---- advanced priorities
        d.IoPriority = IoPriority.Value;
        d.MemoryPriority = MemoryPriority.Value;
        d.EfficiencyMode = EfficiencyMode.Value;
        d.DisablePriorityBoost = ThreadBoost.Value;
        d.GpuPriority = GpuPriority.Value;
        d.GpuPreference = GpuPreference.Value;
        d.ReapplyIfChanged = ReapplyIfChanged;

        if (d.IoPriority is < 0 or > 3) Fail("I/O priority is 0 (very low) to 3 (high). Pick one of the buttons.");
        if (d.MemoryPriority is < 1 or > 5) Fail("Memory priority is 1 (very low) to 5 (normal). Pick one of the buttons.");
        if (d.GpuPriority is < 0 or > 4) Fail("GPU priority is 0 (idle) to 4 (high). Pick one of the buttons.");
        if (d.GpuPreference is < 0 or > 2) Fail("GPU preference is 0 (let Windows decide) to 2 (high performance). Pick one of the buttons.");

        dto = d;
        error = problem;
        return problem == null;
    }

    private static int CpuSetCount(string ids) => ids.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;

    private static List<Choice<ProcessPriorityClass?>> PriorityChoices(string none) => new()
    {
        new(none, null),
        new("IDLE", ProcessPriorityClass.Idle),
        new("LOW", ProcessPriorityClass.BelowNormal),
        new("NORMAL", ProcessPriorityClass.Normal),
        new("ABOVE", ProcessPriorityClass.AboveNormal),
        new("HIGH", ProcessPriorityClass.High),
        new("REALTIME", ProcessPriorityClass.RealTime, AdminOnly: true),
    };

    private static List<Choice<int?>> IntChoices(string none, Func<int, string> name, int min, int max)
    {
        var list = new List<Choice<int?>> { new(none, null) };
        for (int i = min; i <= max; i++) list.Add(new Choice<int?>(name(i), i));
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
        var extra = new Choice<int?>($"OUT OF RANGE ({current})", current);
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
    [ObservableProperty] private bool isRestricted;

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
    /// Opens the editor pre-filled from a running process (the "make a rule" action), or on that program's existing
    /// rule. The list loads only when the Rules page is shown, so it is fetched first: otherwise a program that
    /// already has a rule would get a NEW RULE editor, which the engine then refuses as a duplicate.
    /// </summary>
    public async Task StartNewAsync(string processName, ProcessPriorityClass? priority, double? cpuLimit)
    {
        await LoadAsync();
        var existing = Rules.FirstOrDefault(r => string.Equals(RuleText.Display(r.Dto.ProcessName), RuleText.Display(processName), StringComparison.OrdinalIgnoreCase));
        Editor = existing != null
            ? new RuleEditor(Logical, existing.Dto)
            : new RuleEditor(Logical, new ProcessRuleDto { ProcessName = processName, PriorityClass = (int?)priority, CpuLimitPercent = cpuLimit });
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
            _shell.Toaster.Show("Windows reports no CPU Sets on this machine.", isError: true);
            return;
        }

        var program = editor.ProcessName.Trim();
        _shell.ShowDialog(new CpuSetDialogViewModel(_shell, program.Length == 0 ? "THIS RULE" : RuleText.Display(program).ToUpperInvariant(),
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

        _shell.Toaster.Show($"RULE SAVED · {RuleText.Display(saved.ProcessName)}");
        Editor = null;
        await LoadAsync();
        _hub.RefreshNow();
    }

    [RelayCommand]
    private void Delete(RuleRow row)
    {
        _shell.Confirm("DELETE RULE?", $"{row.Name} goes back to running however Windows starts it. Processes already running keep their current settings.", "DELETE",
            async () =>
            {
                if (await _hub.RunAsync(CommandTypes.DeleteProcessRule, argument: row.Dto.Id, success: $"RULE DELETED · {row.Name}"))
                {
                    if (Editor?.Id == row.Dto.Id) Editor = null;
                    await LoadAsync();
                }
            });
    }
}
