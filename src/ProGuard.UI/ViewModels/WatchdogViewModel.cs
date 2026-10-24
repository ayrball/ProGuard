using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;
using ProGuard.UI.Resources;

namespace ProGuard.UI.ViewModels;

/// <summary>Reads <see cref="WatchdogRuleDto.ActionArgument"/> the way the engine does (priority name or number, I/O 0-3 or name, mask in decimal or hex).</summary>
internal static class WatchdogArgs
{
    public const string ContinuousTrim = "continuous";
    public const ProcessPriorityClass DefaultPriority = ProcessPriorityClass.Idle;
    public const int DefaultIoPriority = 1;

    public static bool TryParsePriority(string? text, out ProcessPriorityClass priority)
    {
        priority = DefaultPriority;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var compact = text.Replace(" ", string.Empty).Trim();
        if (compact.Equals("Realtime", StringComparison.OrdinalIgnoreCase)) compact = nameof(ProcessPriorityClass.RealTime);
        if (Enum.TryParse(compact, ignoreCase: true, out ProcessPriorityClass parsed) && Enum.IsDefined(parsed))
        {
            priority = parsed;
            return true;
        }
        return false;
    }

    public static bool TryParseIoPriority(string? text, out int value)
    {
        value = DefaultIoPriority;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var compact = text.Replace(" ", string.Empty).Trim();
        if (int.TryParse(compact, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            if (number < 0 || number > 3) return false;
            value = number;
            return true;
        }
        int? named = compact.ToLowerInvariant() switch { "verylow" => 0, "low" => 1, "normal" => 2, "high" => 3, _ => null };
        if (named == null) return false;
        value = named.Value;
        return true;
    }

    /// <summary>A non-zero mask in decimal or 0x-prefixed hex.</summary>
    public static bool TryParseAffinity(string? text, out long mask)
    {
        mask = 0;
        var trimmed = text?.Trim() ?? string.Empty;
        var ok = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask)
            : long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask);
        return ok && mask != 0;
    }

    public static bool IsContinuous(string? text) => string.Equals(text?.Trim(), ContinuousTrim, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The words of a watchdog rule: chips in the editor and the sentence on each row.</summary>
internal static class WatchdogText
{
    public static string Trigger(WatchdogTrigger t) => t switch
    {
        WatchdogTrigger.CpuAbove => Strings.Wd_CpuAbove,
        WatchdogTrigger.RamAbove => Strings.Wd_MemoryAbove,
        WatchdogTrigger.RunningLongerThan => Strings.Wd_RunningLongerThan,
        _ => Strings.Wd_InstancesAbove
    };

    public static string Unit(WatchdogTrigger t) => t switch
    {
        WatchdogTrigger.CpuAbove => "%",
        WatchdogTrigger.RamAbove => " MB",
        _ => ""
    };

    public static string Response(WatchdogResponse r) => r switch
    {
        WatchdogResponse.Terminate => Strings.Proc_EndIt,
        WatchdogResponse.Restart => Strings.Wd_RestartIt,
        WatchdogResponse.SetPriority => Strings.Wd_SetPriority,
        WatchdogResponse.SetAffinity => Strings.Wd_SetCores,
        WatchdogResponse.SetIoPriority => Strings.Wd_SetIoPriority,
        WatchdogResponse.TrimWorkingSet => Strings.Wd_TrimMemory,
        WatchdogResponse.Launch => Strings.Wd_LaunchAProgram,
        WatchdogResponse.Notify => Strings.Wd_NotifyMe,
        _ => Strings.Wd_JustLogIt
    };

    public static string Sustain(int seconds) => seconds <= 0 ? Strings.Wd_AtOnce : string.Format(Strings.Wd_For, RuleText.Duration(seconds));

    public static string Cooldown(int seconds) => seconds <= 0 ? Strings.Wd_CooldownUntilClear : string.Format(Strings.Wd_Cooldown, RuleText.Duration(seconds));

    /// <summary>"CPU ABOVE 80%", "MEMORY (PRIVATE) ABOVE 2048 MB", "RUNNING LONGER THAN 2h", "INSTANCES ABOVE 3".</summary>
    public static string Condition(WatchdogRuleDto d)
    {
        var value = EditorNumbers.Text(d.ThresholdValue);
        return d.ConditionType switch
        {
            WatchdogTrigger.CpuAbove => string.Format(Strings.Wd_CpuAboveValue, value),
            WatchdogTrigger.RamAbove => string.Format(d.MemoryMetric == MemoryMetric.PrivateBytes ? Strings.Wd_PrivateAboveValue : Strings.Wd_MemoryAboveValue, value),
            WatchdogTrigger.RunningLongerThan => string.Format(Strings.Wd_RunningLongerThanValue, RuleText.Duration((int)Math.Round(d.ThresholdValue))),
            _ => string.Format(Strings.Wd_InstancesAboveValue, value)
        };
    }

    /// <summary>What the rule does, including its argument and how long a change lasts: "SET CORES 0-3 FOR 5m".</summary>
    public static string Action(WatchdogRuleDto d)
    {
        var argument = d.ActionArgument?.Trim() ?? string.Empty;
        var restore = d.ActionDurationSeconds > 0 ? string.Format(Strings.Wd_RestoreFor, RuleText.Duration(d.ActionDurationSeconds)) : string.Empty;
        switch (d.ActionType)
        {
            case WatchdogResponse.Terminate: return Strings.Proc_EndIt;
            case WatchdogResponse.Restart: return Strings.Wd_RestartIt;
            case WatchdogResponse.LogAlert: return Strings.Wd_JustLogIt;
            case WatchdogResponse.SetPriority:
                var priority = argument.Length == 0 ? Strings.Idle2
                    : WatchdogArgs.TryParsePriority(argument, out var p) ? Fmt.Priority(p) : argument.ToUpperInvariant();
                return string.Format(Strings.Wd_SetPriorityTo, priority, restore);
            case WatchdogResponse.SetIoPriority:
                var io = argument.Length == 0 ? Strings.Low2
                    : WatchdogArgs.TryParseIoPriority(argument, out var i) ? RuleText.IoPriorityName(i) : argument.ToUpperInvariant();
                return string.Format(Strings.Wd_SetIoPriorityTo, io, restore);
            case WatchdogResponse.SetAffinity:
                return string.Format(Strings.Wd_SetCoresTo, WatchdogArgs.TryParseAffinity(argument, out var mask) ? RuleText.CoreRange(mask) : "?", restore);
            case WatchdogResponse.TrimWorkingSet:
                return WatchdogArgs.IsContinuous(argument) ? Strings.Wd_TrimMemoryContinuously : Strings.Wd_TrimMemory;
            case WatchdogResponse.Launch:
                return argument.Length == 0 ? Strings.Wd_LaunchAProgram : string.Format(Strings.Wd_Launch, Short(argument));
            case WatchdogResponse.Notify:
                return argument.Length == 0 ? Strings.Wd_NotifyMe : string.Format(Strings.Wd_NotifyMeWith, Short(argument));
            default: return d.ActionType.ToString().ToUpperInvariant();
        }
    }

    /// <summary>"WHEN chrome.exe · MEMORY (PRIVATE) ABOVE 2048 MB · FOR 30s → SET CORES 0-3 FOR 5m · COOLDOWN 60s".</summary>
    public static string Sentence(WatchdogRuleDto d) =>
        string.Format(Strings.Wd_Sentence, RuleText.Display(d.TargetProcessName), Condition(d), Sustain(d.DurationSeconds), Action(d), Cooldown(d.CooldownSeconds));

    private static string Short(string text)
    {
        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 40 ? oneLine : oneLine[..39] + "…";
    }
}

/// <summary>One watchdog rule shown as a sentence: WHEN x · CPU ABOVE 80% · FOR 30s → SET PRIORITY IDLE FOR 5m · COOLDOWN 60s.</summary>
public sealed partial class WatchdogRow : ObservableObject
{
    public WatchdogRuleDto Dto { get; }
    public string Name => RuleText.Display(Dto.TargetProcessName).ToUpperInvariant();
    public string Condition => WatchdogText.Condition(Dto);
    public string Sustain => WatchdogText.Sustain(Dto.DurationSeconds);
    public string Response => WatchdogText.Action(Dto);
    public string Cooldown => WatchdogText.Cooldown(Dto.CooldownSeconds);
    public string Sentence => WatchdogText.Sentence(Dto);

    /// <summary>Set while the row's switch is being put back after a failed save, so that doesn't save again.</summary>
    internal bool Silent { get; set; }

    [ObservableProperty] private bool enabled;

    public WatchdogRow(WatchdogRuleDto dto)
    {
        Dto = dto;
        enabled = dto.IsEnabled;
    }
}

/// <summary>The rule being created or changed in the side panel. Every <see cref="WatchdogRuleDto"/> setting is here.</summary>
public sealed partial class WatchdogEditor : ObservableObject
{
    private readonly int _logical;
    private bool _ready;
    private bool _refreshing;

    /// <summary>The rule being edited, as loaded; settings the chosen action doesn't use are sent back unchanged.</summary>
    private readonly WatchdogRuleDto? _original;

    public int Id { get; init; }
    public string Heading => Id == 0 ? Strings.Wd_NewWatch : Strings.Wd_EditWatch;

    public IReadOnlyList<Choice<WatchdogTrigger>> Triggers { get; } = Enum.GetValues<WatchdogTrigger>()
        .Select(t => new Choice<WatchdogTrigger>(WatchdogText.Trigger(t), t)).ToList();

    public IReadOnlyList<Choice<MemoryMetric>> Metrics { get; } = new[]
    {
        new Choice<MemoryMetric>(Strings.Wd_WorkingSet, MemoryMetric.WorkingSet),
        new Choice<MemoryMetric>(Strings.Wd_PrivateBytes, MemoryMetric.PrivateBytes),
    };

    /// <summary>All nine actions, safest first.</summary>
    public IReadOnlyList<Choice<WatchdogResponse>> Responses { get; } = new[]
    {
        WatchdogResponse.LogAlert, WatchdogResponse.Notify, WatchdogResponse.SetPriority, WatchdogResponse.SetIoPriority,
        WatchdogResponse.SetAffinity, WatchdogResponse.TrimWorkingSet, WatchdogResponse.Launch, WatchdogResponse.Restart, WatchdogResponse.Terminate
    }.Select(r => new Choice<WatchdogResponse>(WatchdogText.Response(r), r, AdminOnly: r == WatchdogResponse.Launch)).ToList();

    public IReadOnlyList<Choice<ProcessPriorityClass?>> PriorityChoices { get; }

    public IReadOnlyList<Choice<int?>> IoChoices { get; } = Enumerable.Range(0, 4)
        .Select(i => new Choice<int?>(RuleText.IoPriorityName(i), i, AdminOnly: i == 3)).ToList();

    public IReadOnlyList<Choice<bool>> TrimChoices { get; } = new[] { new Choice<bool>(Strings.Wd_Once, false), new Choice<bool>(Strings.Wd_Continuously, true) };

    public CorePicker CorePicker { get; }
    public ObservableCollection<CoreToggle> Cores => CorePicker.Cores;

    /// <summary>How long the condition must hold before the action runs.</summary>
    public DurationInput Sustain { get; }

    /// <summary>The run time for "running longer than".</summary>
    public DurationInput RunTime { get; }

    /// <summary>How long a priority, I/O priority or core change lasts before the old value goes back. 0 keeps it.</summary>
    public DurationInput Restore { get; }

    public DurationInput Cooldown { get; }

    [ObservableProperty] private string processName = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Unit), nameof(IsMemory), nameof(IsRunTime), nameof(IsNumber))] private Choice<WatchdogTrigger> trigger;
    [ObservableProperty] private string threshold = "80";
    [ObservableProperty] private Choice<MemoryMetric> metric;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPriorityAction), nameof(IsIoAction), nameof(IsAffinityAction), nameof(IsLaunch),
        nameof(IsNotify), nameof(IsTrim), nameof(TakesRestore))] private Choice<WatchdogResponse> response;
    [ObservableProperty] private Choice<ProcessPriorityClass?>? priorityArg;
    [ObservableProperty] private Choice<int?>? ioArg;
    [ObservableProperty] private string commandLine = string.Empty;
    [ObservableProperty] private string message = string.Empty;
    [ObservableProperty] private Choice<bool> trim;
    [ObservableProperty] private bool enabled = true;

    /// <summary>True for a user without administrator rights: the engine refuses some contents, so those are marked and the limits listed.</summary>
    [ObservableProperty] private bool isRestricted;

    /// <summary>What the engine refuses a standard user. Shown as one line in the editor while <see cref="IsRestricted"/>.</summary>
    public static string LimitsHint =>
        Strings.Wd_LimitsHint;
    public string RestrictionHint => LimitsHint;

    /// <summary>The rule as a sentence, updated as the editor changes.</summary>
    [ObservableProperty] private string preview = string.Empty;

    public string Unit => WatchdogText.Unit(Trigger.Value).Trim();
    public bool IsMemory => Trigger.Value == WatchdogTrigger.RamAbove;
    public bool IsRunTime => Trigger.Value == WatchdogTrigger.RunningLongerThan;
    public bool IsNumber => !IsRunTime;
    public bool IsPriorityAction => Response.Value == WatchdogResponse.SetPriority;
    public bool IsIoAction => Response.Value == WatchdogResponse.SetIoPriority;
    public bool IsAffinityAction => Response.Value == WatchdogResponse.SetAffinity;
    public bool IsLaunch => Response.Value == WatchdogResponse.Launch;
    public bool IsNotify => Response.Value == WatchdogResponse.Notify;
    public bool IsTrim => Response.Value == WatchdogResponse.TrimWorkingSet;
    public bool TakesRestore => Response.Value is WatchdogResponse.SetPriority or WatchdogResponse.SetIoPriority or WatchdogResponse.SetAffinity;

    private static IReadOnlyList<Choice<int>> Presets(params (int Seconds, string? Label)[] items) =>
        items.Select(i => new Choice<int>(i.Label ?? RuleText.Duration(i.Seconds), i.Seconds)).ToList();

    public WatchdogEditor(WatchdogRuleDto? from = null, int logical = 0)
    {
        _logical = logical > 0 ? logical : Environment.ProcessorCount;

        var priorities = new List<Choice<ProcessPriorityClass?>>
        {
            new(Strings.Idle2, ProcessPriorityClass.Idle),
            new(Strings.Low2, ProcessPriorityClass.BelowNormal),
            new(Strings.Normal2, ProcessPriorityClass.Normal),
            new(Strings.Above, ProcessPriorityClass.AboveNormal),
            new(Strings.High2, ProcessPriorityClass.High),
            new(Strings.Realtime2, ProcessPriorityClass.RealTime, AdminOnly: true),
        };
        PriorityChoices = priorities;

        Sustain = new DurationInput(Presets((0, Strings.Wd_AtOnce), (10, null), (30, null), (60, null), (300, null)), 30);
        RunTime = new DurationInput(Presets((300, null), (1800, null), (3600, null), (14400, null), (86400, null)), 3600);
        Restore = new DurationInput(Presets((0, Strings.Wd_Never), (30, null), (60, null), (300, null), (900, null), (3600, null)));
        Cooldown = new DurationInput(Presets((0, Strings.Wd_WhenItClears), (60, null), (300, null), (900, null), (3600, null)), 60);

        trigger = Triggers[0];
        metric = Metrics[0];
        response = Responses[0];
        priorityArg = priorities[0];
        ioArg = IoChoices[1];
        trim = TrimChoices[0];
        long? mask = null;

        if (from != null)
        {
            _original = DtoCopy.Clone(from);
            Id = from.Id;
            processName = RuleText.Display(from.TargetProcessName);
            trigger = Triggers.First(t => t.Value == from.ConditionType);
            metric = Metrics.First(m => m.Value == from.MemoryMetric);
            response = Responses.First(r => r.Value == from.ActionType);
            enabled = from.IsEnabled;
            Sustain.Seconds = from.DurationSeconds;
            Restore.Seconds = from.ActionDurationSeconds;
            Cooldown.Seconds = from.CooldownSeconds;

            if (from.ConditionType == WatchdogTrigger.RunningLongerThan)
            {
                RunTime.Seconds = (int)Math.Clamp(Math.Round(from.ThresholdValue), 0, RuleText.MaxSeconds);
                threshold = "1";
            }
            else threshold = EditorNumbers.Text(from.ThresholdValue);

            var argument = from.ActionArgument?.Trim() ?? string.Empty;
            switch (from.ActionType)
            {
                case WatchdogResponse.SetPriority:
                    if (argument.Length == 0) priorityArg = priorities[0];
                    else if (WatchdogArgs.TryParsePriority(argument, out var p))
                    {
                        priorityArg = priorities.FirstOrDefault(c => c.Value == p)
                                      ?? Extra(priorities, new Choice<ProcessPriorityClass?>(Fmt.Priority(p), p));
                    }
                    else priorityArg = null;
                    break;
                case WatchdogResponse.SetIoPriority:
                    ioArg = argument.Length == 0 ? IoChoices[1]
                        : WatchdogArgs.TryParseIoPriority(argument, out var io) ? IoChoices[io] : null;
                    break;
                case WatchdogResponse.SetAffinity:
                    if (WatchdogArgs.TryParseAffinity(argument, out var m)) mask = m;
                    break;
                case WatchdogResponse.Launch:
                    commandLine = from.ActionArgument ?? string.Empty;
                    break;
                case WatchdogResponse.Notify:
                    message = from.ActionArgument ?? string.Empty;
                    break;
                case WatchdogResponse.TrimWorkingSet:
                    trim = WatchdogArgs.IsContinuous(argument) ? TrimChoices[1] : TrimChoices[0];
                    break;
            }
        }

        // A new "set cores" rule starts with the first half of the machine lit, so it does something when applied.
        CorePicker = new CorePicker(_logical, mask ?? (_logical > 1 ? (1L << (Math.Min(_logical, 64) / 2)) - 1 : null));
        CorePicker.Changed += Refresh;
        foreach (var input in new[] { Sustain, RunTime, Restore, Cooldown }) input.PropertyChanged += (_, _) => Refresh();

        _ready = true;
        Refresh();
    }

    private static T Extra<T>(List<T> list, T item)
    {
        list.Add(item);
        return item;
    }

    partial void OnTriggerChanged(Choice<WatchdogTrigger> value)
    {
        // Offer a sensible starting number for the new kind of condition.
        Threshold = value.Value switch
        {
            WatchdogTrigger.CpuAbove => "80",
            WatchdogTrigger.RamAbove => "2048",
            _ => "1"
        };
        if (value.Value == WatchdogTrigger.RunningLongerThan) RunTime.Seconds = 3600;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(Preview)) Refresh();
    }

    private void Refresh()
    {
        if (!_ready || _refreshing) return;
        _refreshing = true;
        try
        {
            TryBuild(out var draft, out _);
            Preview = WatchdogText.Sentence(draft);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// Builds the rule to save: a copy of the loaded one with the editor's values written over it. Returns false, with a
    /// message that says what to fix, when something is missing or out of range; the dto is still filled in as far as it can be.
    /// </summary>
    public bool TryBuild(out WatchdogRuleDto dto, out string? error)
    {
        string? problem = null;
        void Fail(string message) => problem ??= message;

        var d = _original == null ? new WatchdogRuleDto() : DtoCopy.Clone(_original);
        d.Id = Id;

        var name = ProcessName.Trim();
        if (name.Length == 0) Fail(Strings.Wd_NeedName);
        d.TargetProcessName = RuleText.Display(name);
        d.IsEnabled = Enabled;
        d.ConditionType = Trigger.Value;

        // ---- condition
        switch (Trigger.Value)
        {
            case WatchdogTrigger.RunningLongerThan:
                if (RunTime.TryGetSeconds(out var run) && run > 0) d.ThresholdValue = run;
                else Fail(Strings.Wd_BadRunningLonger);
                break;
            case WatchdogTrigger.InstanceCountAbove:
                if (EditorNumbers.TryInt(Threshold, out var count) && count >= 1) d.ThresholdValue = count;
                else Fail(Strings.Wd_BadInstances);
                break;
            default:
                if (!EditorNumbers.TryDouble(Threshold, out var value) || value <= 0)
                    Fail(Trigger.Value == WatchdogTrigger.RamAbove ? Strings.Wd_BadMemoryThreshold : Strings.Wd_BadThreshold);
                else if (Trigger.Value == WatchdogTrigger.CpuAbove && value > 100)
                    Fail(Strings.Wd_CpuOver100);
                else d.ThresholdValue = value;
                break;
        }
        if (Trigger.Value == WatchdogTrigger.RamAbove) d.MemoryMetric = Metric.Value;

        if (Sustain.TryGetSeconds(out var sustain)) d.DurationSeconds = sustain;
        else Fail(Strings.Wd_BadHowLong);

        // ---- action
        bool sameAction = _original != null && _original.ActionType == Response.Value;
        d.ActionType = Response.Value;
        switch (Response.Value)
        {
            case WatchdogResponse.SetPriority:
                if (PriorityArg?.Value is { } priority) d.ActionArgument = priority.ToString();
                else Fail(Strings.Wd_NeedPriority);
                break;
            case WatchdogResponse.SetIoPriority:
                if (IoArg?.Value is { } io) d.ActionArgument = io.ToString(CultureInfo.InvariantCulture);
                else Fail(Strings.Wd_NeedIoPriority);
                break;
            case WatchdogResponse.SetAffinity:
                if (CorePicker.AnyOn) d.ActionArgument = $"0x{CorePicker.MaskKeeping(OriginalMask()):X}";
                else Fail(Strings.Wd_NeedCore);
                break;
            case WatchdogResponse.Launch:
                if (string.IsNullOrWhiteSpace(CommandLine)) Fail(Strings.Wd_NeedCommandLine);
                d.ActionArgument = CommandLine.Trim().Length == 0 ? null : CommandLine.Trim();
                break;
            case WatchdogResponse.Notify:
                d.ActionArgument = string.IsNullOrWhiteSpace(Message) ? null : Message.Trim();
                break;
            case WatchdogResponse.TrimWorkingSet:
                d.ActionArgument = Trim.Value ? WatchdogArgs.ContinuousTrim : null;
                break;
            default:
                // Nothing to say about an argument when the action takes none; drop one left over from a different action.
                if (!sameAction) d.ActionArgument = null;
                break;
        }

        if (TakesRestore)
        {
            if (Restore.TryGetSeconds(out var restore)) d.ActionDurationSeconds = restore;
            else Fail(Strings.Wd_BadRestore);
        }
        else if (!sameAction) d.ActionDurationSeconds = 0;

        if (Cooldown.TryGetSeconds(out var cooldown)) d.CooldownSeconds = cooldown;
        else Fail(Strings.Wd_BadCooldown);

        dto = d;
        error = problem;
        return problem == null;
    }

    private long? OriginalMask() =>
        _original is { ActionType: WatchdogResponse.SetAffinity } && WatchdogArgs.TryParseAffinity(_original.ActionArgument, out var m) ? m : null;
}

/// <summary>"If this happens, do that" rules the engine checks continuously.</summary>
public sealed partial class WatchdogViewModel : ObservableObject, IPage
{
    private readonly EngineHub _hub;
    private readonly ShellViewModel _shell;

    public ObservableCollection<WatchdogRow> Rules { get; } = new();

    [ObservableProperty] private WatchdogEditor? editor;
    [ObservableProperty] private bool isLoaded;

    /// <summary>True when the engine says this UI isn't an administrator: editing stays open, but some contents are refused.</summary>
    [ObservableProperty] private bool isRestricted;

    public WatchdogViewModel(EngineHub hub, ShellViewModel shell)
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

    internal void UpdateAccess() => IsRestricted = _hub.Snapshot?.CallerIsAdministrator == false;

    partial void OnIsRestrictedChanged(bool value)
    {
        if (Editor != null) Editor.IsRestricted = value;
    }

    partial void OnEditorChanged(WatchdogEditor? value)
    {
        if (value != null) value.IsRestricted = IsRestricted;
    }

    public void OnShown() => _ = LoadAsync();
    public void OnHidden() { }

    private async Task LoadAsync()
    {
        var rules = await _hub.QueryAsync<List<WatchdogRuleDto>>(CommandTypes.GetWatchdogRules, quiet: true);
        if (rules == null) return;
        Rules.Clear();
        foreach (var rule in rules.OrderBy(r => r.TargetProcessName, StringComparer.OrdinalIgnoreCase))
        {
            var row = new WatchdogRow(rule);
            row.PropertyChanged += async (_, e) =>
            {
                if (e.PropertyName == nameof(WatchdogRow.Enabled) && !row.Silent) await ToggleAsync(row);
            };
            Rules.Add(row);
        }
        IsLoaded = true;
    }

    private async Task ToggleAsync(WatchdogRow row)
    {
        var wanted = row.Enabled;
        void Revert()
        {
            row.Silent = true;
            row.Enabled = !wanted;
            row.Silent = false;
        }

        // Save a copy, so the row's own rule only changes once the engine has accepted it.
        var dto = DtoCopy.Clone(row.Dto);
        dto.IsEnabled = wanted;
        var saved = await _hub.QueryAsync<WatchdogRuleDto>(CommandTypes.SaveWatchdogRule, argument: dto);
        if (saved == null)
        {
            Revert();
            return;
        }
        row.Dto.IsEnabled = wanted;
        _shell.Toaster.Show(string.Format(wanted ? Strings.Wd_WatchArmed : Strings.Wd_WatchPaused, row.Name));
    }

    [RelayCommand]
    private void New() => Editor = new WatchdogEditor(null, Logical);

    [RelayCommand]
    private void Edit(WatchdogRow row) => Editor = new WatchdogEditor(row.Dto, Logical);

    [RelayCommand]
    private void Cancel() => Editor = null;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editor is not { } editor) return;
        if (!editor.TryBuild(out var dto, out var error))
        {
            _shell.Toaster.Show(error!, isError: true);
            return;
        }

        var saved = await _hub.QueryAsync<WatchdogRuleDto>(CommandTypes.SaveWatchdogRule, argument: dto);
        if (saved == null) return;

        _shell.Toaster.Show(string.Format(Strings.Wd_Saved, RuleText.Display(saved.TargetProcessName)));
        Editor = null;
        await LoadAsync();
    }

    [RelayCommand]
    private void Delete(WatchdogRow row)
    {
        _shell.Confirm(Strings.Wd_DeleteTitle, string.Format(Strings.Wd_DeleteWarning, row.Sentence), Strings.Delete,
            async () =>
            {
                if (await _hub.RunAsync(CommandTypes.DeleteWatchdogRule, argument: row.Dto.Id, success: string.Format(Strings.Wd_Deleted, row.Name)))
                {
                    if (Editor?.Id == row.Dto.Id) Editor = null;
                    await LoadAsync();
                }
            });
    }
}
