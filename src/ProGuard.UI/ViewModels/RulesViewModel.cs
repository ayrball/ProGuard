using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

/// <summary>A labelled value for a row of chip-style choices.</summary>
public sealed record Choice<T>(string Label, T Value);

public sealed class RuleRow
{
    public ProcessRuleDto Dto { get; }
    public string Name => Fmt.Exe(Dto.ProcessName).ToUpperInvariant();
    public IReadOnlyList<string> Effects { get; }

    public RuleRow(ProcessRuleDto dto, int logical)
    {
        Dto = dto;
        var effects = new List<string>();
        if (dto.DisallowExecution) effects.Add("BLOCKED");
        if (dto.PriorityClass is { } p) effects.Add($"PRIORITY {Fmt.Priority((ProcessPriorityClass)p)}");
        if (dto.CpuLimitPercent is { } l) effects.Add($"CAP {l:0.#}%");
        if (dto.AffinityMask is { } m) effects.Add($"CORES {Fmt.Affinity(m, logical)}");
        if (dto.ExcludeFromProBalance) effects.Add("PROBALANCE OFF");
        if (effects.Count == 0) effects.Add("NO EFFECT");
        Effects = effects;
    }
}

/// <summary>The rule being created or changed in the side panel.</summary>
public sealed partial class RuleEditor : ObservableObject
{
    public int Id { get; init; }
    public bool IsNew => Id == 0;
    public string Heading => IsNew ? "NEW RULE" : "EDIT RULE";

    public IReadOnlyList<Choice<ProcessPriorityClass?>> Priorities { get; } = new Choice<ProcessPriorityClass?>[]
    {
        new("LEAVE", null),
        new("IDLE", ProcessPriorityClass.Idle),
        new("LOW", ProcessPriorityClass.BelowNormal),
        new("NORMAL", ProcessPriorityClass.Normal),
        new("ABOVE", ProcessPriorityClass.AboveNormal),
        new("HIGH", ProcessPriorityClass.High),
    };

    public ObservableCollection<CoreToggle> Cores { get; } = new();

    [ObservableProperty] private string processName = string.Empty;
    [ObservableProperty] private Choice<ProcessPriorityClass?> priority;
    [ObservableProperty] private bool limitEnabled;
    [ObservableProperty] private double limit = 25;
    [ObservableProperty] private bool affinityEnabled;
    [ObservableProperty] private bool excludeFromProBalance;
    [ObservableProperty] private bool disallowExecution;

    public RuleEditor(int logical, ProcessRuleDto? from = null)
    {
        priority = Priorities[0];
        long mask = from?.AffinityMask ?? -1;
        for (int i = 0; i < Math.Clamp(logical, 1, 64); i++) Cores.Add(new CoreToggle(i, (mask & (1L << i)) != 0));

        if (from == null) return;
        _original = DtoCopy.Clone(from);
        Id = from.Id;
        processName = Fmt.Exe(from.ProcessName);
        priority = Priorities.FirstOrDefault(c => (int?)c.Value == from.PriorityClass) ?? Priorities[0];
        limitEnabled = from.CpuLimitPercent.HasValue;
        limit = from.CpuLimitPercent ?? 25;
        affinityEnabled = from.AffinityMask.HasValue;
        excludeFromProBalance = from.ExcludeFromProBalance;
        disallowExecution = from.DisallowExecution;
    }

    public int LimitWhole => (int)Math.Round(Limit);
    partial void OnLimitChanged(double value) => OnPropertyChanged(nameof(LimitWhole));

    /// <summary>The rule being edited, as loaded; fields this editor doesn't show are sent back unchanged.</summary>
    private readonly ProcessRuleDto _original = new();

    public ProcessRuleDto ToDto()
    {
        long mask = Cores.Where(c => c.IsOn).Aggregate(0L, (m, c) => m | (1L << c.Index));
        var dto = DtoCopy.Clone(_original);
        dto.Id = Id;
        dto.ProcessName = ProcessName.Trim();
        dto.PriorityClass = (int?)Priority.Value;
        dto.CpuLimitPercent = LimitEnabled ? Math.Clamp(LimitWhole, 1, 99) : null;
        dto.AffinityMask = AffinityEnabled && mask != 0 ? mask : null;
        dto.ExcludeFromProBalance = ExcludeFromProBalance;
        dto.DisallowExecution = DisallowExecution;
        return dto;
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

    public RulesViewModel(EngineHub hub, ShellViewModel shell)
    {
        _hub = hub;
        _shell = shell;
    }

    private int Logical => _hub.Snapshot?.LogicalProcessors ?? Environment.ProcessorCount;

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

    /// <summary>Opens the editor pre-filled from a running process (the "make a rule" action).</summary>
    public void StartNew(string processName, ProcessPriorityClass? priority, double? cpuLimit)
    {
        var existing = Rules.FirstOrDefault(r => string.Equals(Fmt.Exe(r.Dto.ProcessName), Fmt.Exe(processName), StringComparison.OrdinalIgnoreCase));
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
    private async Task SaveAsync()
    {
        if (Editor is not { } editor) return;
        if (string.IsNullOrWhiteSpace(editor.ProcessName))
        {
            _shell.Toaster.Show("Name the program the rule is for, e.g. chrome.exe.", isError: true);
            return;
        }

        var saved = await _hub.QueryAsync<ProcessRuleDto>(CommandTypes.SaveProcessRule, argument: editor.ToDto());
        if (saved == null) return;

        _shell.Toaster.Show($"RULE SAVED · {Fmt.Exe(saved.ProcessName)}");
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
