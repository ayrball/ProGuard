using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

internal static class WatchdogText
{
    public static string Trigger(WatchdogTrigger t) => t switch
    {
        WatchdogTrigger.CpuAbove => "CPU ABOVE",
        WatchdogTrigger.RamAbove => "MEMORY ABOVE",
        WatchdogTrigger.RunningLongerThan => "RUNNING LONGER THAN",
        _ => "INSTANCES ABOVE"
    };

    public static string Unit(WatchdogTrigger t) => t switch
    {
        WatchdogTrigger.CpuAbove => "%",
        WatchdogTrigger.RamAbove => " MB",
        WatchdogTrigger.RunningLongerThan => " s",
        _ => ""
    };

    public static string Response(WatchdogResponse r) => r switch
    {
        WatchdogResponse.Terminate => "END IT",
        WatchdogResponse.Restart => "RESTART IT",
        WatchdogResponse.SetPriority => "SET PRIORITY",
        WatchdogResponse.SetAffinity => "SET CORES",
        WatchdogResponse.SetIoPriority => "SET I/O PRIORITY",
        WatchdogResponse.TrimWorkingSet => "TRIM MEMORY",
        WatchdogResponse.Launch => "LAUNCH A PROGRAM",
        WatchdogResponse.Notify => "NOTIFY ME",
        _ => "JUST LOG IT"
    };

    public static string Sustain(int seconds) => seconds switch
    {
        0 => "AT ONCE",
        < 60 => $"FOR {seconds}s",
        _ => $"FOR {seconds / 60}m"
    };
}

/// <summary>One watchdog rule shown as a sentence: WHEN x · CPU ABOVE 80% · FOR 30s → DROP TO IDLE.</summary>
public sealed partial class WatchdogRow : ObservableObject
{
    public WatchdogRuleDto Dto { get; }
    public string Name => Fmt.Exe(Dto.TargetProcessName).ToUpperInvariant();
    public string Condition => $"{WatchdogText.Trigger(Dto.ConditionType)} {Dto.ThresholdValue:0.#}{WatchdogText.Unit(Dto.ConditionType)}";
    public string Sustain => WatchdogText.Sustain(Dto.DurationSeconds);
    public string Response => WatchdogText.Response(Dto.ActionType);

    [ObservableProperty] private bool enabled;

    public WatchdogRow(WatchdogRuleDto dto)
    {
        Dto = dto;
        enabled = dto.IsEnabled;
    }
}

public sealed partial class WatchdogEditor : ObservableObject
{
    public int Id { get; init; }
    public string Heading => Id == 0 ? "NEW WATCH" : "EDIT WATCH";

    public IReadOnlyList<Choice<WatchdogTrigger>> Triggers { get; } = Enum.GetValues<WatchdogTrigger>()
        .Select(t => new Choice<WatchdogTrigger>(WatchdogText.Trigger(t), t)).ToList();

    public IReadOnlyList<Choice<WatchdogResponse>> Responses { get; } = Enum.GetValues<WatchdogResponse>()
        .Select(r => new Choice<WatchdogResponse>(WatchdogText.Response(r), r)).ToList();

    public IReadOnlyList<Choice<int>> Sustains { get; } = new[] { 0, 10, 30, 60, 300 }
        .Select(s => new Choice<int>(WatchdogText.Sustain(s), s)).ToList();

    [ObservableProperty] private string processName = string.Empty;
    [ObservableProperty] private Choice<WatchdogTrigger> trigger;
    [ObservableProperty] private string threshold = "80";
    [ObservableProperty] private Choice<int> sustain;
    [ObservableProperty] private Choice<WatchdogResponse> response;
    [ObservableProperty] private bool enabled = true;

    public string Unit => WatchdogText.Unit(Trigger.Value).Trim();

    partial void OnTriggerChanged(Choice<WatchdogTrigger> value)
    {
        OnPropertyChanged(nameof(Unit));
        // Offer a sensible starting number for the new kind of condition.
        Threshold = value.Value switch
        {
            WatchdogTrigger.CpuAbove => "80",
            WatchdogTrigger.RamAbove => "2048",
            WatchdogTrigger.RunningLongerThan => "3600",
            _ => "1"
        };
    }

    public WatchdogEditor(WatchdogRuleDto? from = null)
    {
        trigger = Triggers[0];
        sustain = Sustains[2];
        response = Responses[1];
        if (from == null) return;

        Id = from.Id;
        _original = from;
        processName = Fmt.Exe(from.TargetProcessName);
        trigger = Triggers.First(t => t.Value == from.ConditionType);
        threshold = from.ThresholdValue.ToString("0.#");
        sustain = Sustains.FirstOrDefault(s => s.Value == from.DurationSeconds) ?? new Choice<int>(WatchdogText.Sustain(from.DurationSeconds), from.DurationSeconds);
        response = Responses.First(r => r.Value == from.ActionType);
        enabled = from.IsEnabled;
    }

    /// <summary>The rule being edited, as loaded; fields this editor doesn't show are sent back unchanged.</summary>
    private readonly WatchdogRuleDto? _original;

    public bool TryBuild(out WatchdogRuleDto dto, out string? error)
    {
        dto = new WatchdogRuleDto();
        error = null;
        if (string.IsNullOrWhiteSpace(ProcessName))
        {
            error = "Name the program to watch, e.g. chrome.exe.";
            return false;
        }
        if (!double.TryParse(Threshold, out var value) || value <= 0)
        {
            error = "The threshold has to be a number above zero.";
            return false;
        }
        if (Trigger.Value == WatchdogTrigger.CpuAbove && value > 100)
        {
            error = "CPU can't go above 100%.";
            return false;
        }

        dto = _original == null ? new WatchdogRuleDto() : DtoCopy.Clone(_original);
        dto.Id = Id;
        dto.TargetProcessName = ProcessName.Trim();
        dto.IsEnabled = Enabled;
        dto.ConditionType = Trigger.Value;
        dto.ThresholdValue = value;
        dto.DurationSeconds = Sustain.Value;
        dto.ActionType = Response.Value;
        // "Drop to idle" was the rebuild's only priority action; keep that as the default argument.
        if (dto.ActionType == WatchdogResponse.SetPriority && string.IsNullOrWhiteSpace(dto.ActionArgument)) dto.ActionArgument = "Idle";
        return true;
    }
}

/// <summary>"If this happens, do that" rules the engine checks continuously.</summary>
public sealed partial class WatchdogViewModel : ObservableObject, IPage
{
    private readonly EngineHub _hub;
    private readonly ShellViewModel _shell;

    public ObservableCollection<WatchdogRow> Rules { get; } = new();

    [ObservableProperty] private WatchdogEditor? editor;
    [ObservableProperty] private bool isLoaded;

    public WatchdogViewModel(EngineHub hub, ShellViewModel shell)
    {
        _hub = hub;
        _shell = shell;
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
                if (e.PropertyName == nameof(WatchdogRow.Enabled)) await ToggleAsync(row);
            };
            Rules.Add(row);
        }
        IsLoaded = true;
    }

    private async Task ToggleAsync(WatchdogRow row)
    {
        row.Dto.IsEnabled = row.Enabled;
        await _hub.QueryAsync<WatchdogRuleDto>(CommandTypes.SaveWatchdogRule, argument: row.Dto);
        _shell.Toaster.Show($"{row.Name} WATCH {(row.Enabled ? "ARMED" : "PAUSED")}");
    }

    [RelayCommand] private void New() => Editor = new WatchdogEditor();
    [RelayCommand] private void Edit(WatchdogRow row) => Editor = new WatchdogEditor(row.Dto);
    [RelayCommand] private void Cancel() => Editor = null;

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

        _shell.Toaster.Show($"WATCH SAVED · {Fmt.Exe(saved.TargetProcessName)}");
        Editor = null;
        await LoadAsync();
    }

    [RelayCommand]
    private void Delete(WatchdogRow row)
    {
        _shell.Confirm("DELETE WATCH?", $"ProGuard stops watching {row.Name} for: {row.Condition} {row.Sustain}.", "DELETE",
            async () =>
            {
                if (await _hub.RunAsync(CommandTypes.DeleteWatchdogRule, argument: row.Dto.Id, success: $"WATCH DELETED · {row.Name}"))
                {
                    if (Editor?.Id == row.Dto.Id) Editor = null;
                    await LoadAsync();
                }
            });
    }
}
