using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Shared.Contracts;
using ProGuard.UI.IPC;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

public partial class WatchdogViewModel : ViewModelBase
{
    private const int RecentEventCount = 5;

    /// <summary>Condition choices on the add-rule panel, in display order.</summary>
    public static IReadOnlyList<string> ConditionOptions { get; } = new[]
    {
        "CPU Above (%)", "Memory Above (MB)", "Running Longer Than (s)", "Instance Count Above"
    };

    private static readonly WatchdogCondition[] ConditionValues =
    {
        WatchdogCondition.CpuAbove, WatchdogCondition.RamAbove, WatchdogCondition.RunningLongerThan, WatchdogCondition.InstanceCountAbove
    };

    public static IReadOnlyList<string> MemoryMetricOptions { get; } = new[] { "Working set", "Private bytes" };

    /// <summary>Action choices on the add-rule panel, in display order.</summary>
    public static IReadOnlyList<string> ActionOptions { get; } = new[]
    {
        "Terminate", "Restart", "Set Priority", "Set I/O Priority", "Set Affinity", "Trim Working Set", "Launch Program", "Notify", "Log Only"
    };

    private static readonly WatchdogAction[] ActionValues =
    {
        WatchdogAction.Terminate, WatchdogAction.Restart, WatchdogAction.SetPriority, WatchdogAction.SetIoPriority,
        WatchdogAction.SetAffinity, WatchdogAction.TrimWorkingSet, WatchdogAction.Launch, WatchdogAction.Notify, WatchdogAction.LogAlert
    };

    [ObservableProperty]
    private bool isAddPanelOpen;

    [ObservableProperty]
    private string newProcessName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMemoryCondition))]
    private string newCondition = ConditionOptions[0];

    [ObservableProperty]
    private string newMemoryMetric = MemoryMetricOptions[0];

    [ObservableProperty]
    private double newThreshold = 85.0;

    [ObservableProperty]
    private int newDuration = 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionArgumentHint), nameof(ActionTakesArgument), nameof(ActionTakesDuration), nameof(IsTrimAction))]
    private string newAction = ActionOptions[0];

    [ObservableProperty]
    private string newActionArgument = string.Empty;

    [ObservableProperty]
    private int newActionDuration;

    [ObservableProperty]
    private bool newTrimContinuously;

    [ObservableProperty]
    private int newCooldown = 60;

    [ObservableProperty]
    private string newKeepRunningName = string.Empty;

    [ObservableProperty]
    private string recentTriggerText = "No watchdog events recorded yet.";

    public ObservableCollection<WatchdogRuleDisplayItem> WatchdogRules { get; set; } = new();

    /// <summary>Processes whose rule has Keep Running set.</summary>
    public ObservableCollection<string> KeepRunningProcesses { get; } = new();

    public bool IsMemoryCondition => SelectedCondition == WatchdogCondition.RamAbove;
    public bool ActionTakesArgument => SelectedAction is WatchdogAction.SetPriority or WatchdogAction.SetIoPriority
        or WatchdogAction.SetAffinity or WatchdogAction.Launch or WatchdogAction.Notify;
    public bool ActionTakesDuration => SelectedAction is WatchdogAction.SetPriority or WatchdogAction.SetIoPriority or WatchdogAction.SetAffinity;
    public bool IsTrimAction => SelectedAction == WatchdogAction.TrimWorkingSet;

    public string ActionArgumentHint => SelectedAction switch
    {
        WatchdogAction.SetPriority => "Idle, BelowNormal, Normal, AboveNormal, High",
        WatchdogAction.SetIoPriority => "VeryLow, Low, Normal, High",
        WatchdogAction.SetAffinity => "CPU mask, e.g. 0x3",
        WatchdogAction.Launch => @"""C:\path\app.exe"" arguments",
        WatchdogAction.Notify => "Message (optional)",
        _ => ""
    };

    private WatchdogCondition SelectedCondition => Lookup(ConditionOptions, ConditionValues, NewCondition, WatchdogCondition.CpuAbove);
    private WatchdogAction SelectedAction => Lookup(ActionOptions, ActionValues, NewAction, WatchdogAction.Terminate);

    private readonly ConfigWriter _config;

    public WatchdogViewModel() : this(new ConfigWriter(new EngineProxy())) { }

    public WatchdogViewModel(ConfigWriter config)
    {
        _config = config;
        LoadRules();
        LoadKeepRunning();
        LoadRecentTriggers();
    }

    public WatchdogViewModel(WatchdogNotificationListener listener, ConfigWriter config) : this(config)
    {
        if (listener.LatestEvent != null) ShowRecent(new[] { listener.LatestEvent });
        listener.EventsReceived += (_, events) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) ShowRecent(events);
            else dispatcher.BeginInvoke(() => ShowRecent(events));
        };
    }

    private readonly List<string> _recent = new();

    private void ShowRecent(IEnumerable<WatchdogEventDto> events)
    {
        foreach (var e in events)
            _recent.Insert(0, $"[{e.TimestampUtc.ToLocalTime():HH:mm:ss}] {e.Message}");
        if (_recent.Count > RecentEventCount) _recent.RemoveRange(RecentEventCount, _recent.Count - RecentEventCount);
        if (_recent.Count > 0) RecentTriggerText = string.Join(Environment.NewLine, _recent);
    }

    public void LoadRules()
    {
        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();

            var rules = db.WatchdogRules.ToList();

            WatchdogRules.Clear();
            foreach (var r in rules)
            {
                WatchdogRules.Add(CreateDisplayItem(r));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load watchdog rules: {ex}");
        }
    }

    public void LoadKeepRunning()
    {
        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();
            KeepRunningProcesses.Clear();
            foreach (var name in db.ProcessRules.Where(r => r.KeepRunning).Select(r => r.ProcessName).ToList().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                KeepRunningProcesses.Add(name);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load Keep Running processes: {ex}");
        }
    }

    private void LoadRecentTriggers()
    {
        RecentTriggerText = "No watchdog events yet. Rules are checked every second by the engine service.";
    }

    private WatchdogRuleDisplayItem CreateDisplayItem(WatchdogRule r)
    {
        var item = new WatchdogRuleDisplayItem
        {
            Id = r.Id,
            Description = $"IF {r.TargetProcessName} {DescribeCondition(r)} THEN {DescribeAction(r)}",
            Details = DescribeDetails(r),
            IsEnabled = r.IsEnabled
        };

        item.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName != nameof(WatchdogRuleDisplayItem.IsEnabled)) return;
            r.IsEnabled = item.IsEnabled;
            var result = await _config.SaveWatchdogRuleAsync(r);
            if (!result.Success)
                MessageBox.Show($"Watchdog rule not saved: {result.Error}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        return item;
    }

    internal static string DescribeCondition(WatchdogRule r)
    {
        var value = r.ThresholdValue.ToString("0.##", CultureInfo.InvariantCulture);
        return r.ConditionType switch
        {
            WatchdogCondition.CpuAbove => $"CPU > {value}%",
            WatchdogCondition.RamAbove => $"{(r.MemoryMetric == WatchdogMemoryMetric.PrivateBytes ? "private bytes" : "working set")} > {value} MB",
            WatchdogCondition.RunningLongerThan => $"runs longer than {value} s",
            WatchdogCondition.InstanceCountAbove => $"instances > {value}",
            _ => r.ConditionType.ToString()
        };
    }

    internal static string DescribeAction(WatchdogRule r)
    {
        var argument = r.ActionArgument?.Trim();
        var forDuration = r.ActionDurationSeconds > 0 ? $" for {r.ActionDurationSeconds} s" : "";
        return r.ActionType switch
        {
            WatchdogAction.Terminate => "terminate",
            WatchdogAction.Restart => "restart",
            WatchdogAction.SetPriority => $"set priority {(string.IsNullOrEmpty(argument) ? WatchdogArguments.DefaultPriority.ToString() : argument)}{forDuration}",
            WatchdogAction.SetIoPriority => $"set I/O priority {(string.IsNullOrEmpty(argument) ? "Low" : argument)}{forDuration}",
            WatchdogAction.SetAffinity => $"set affinity {argument}{forDuration}",
            WatchdogAction.TrimWorkingSet => WatchdogArguments.IsContinuousTrim(argument) ? "trim working set continuously" : "trim working set",
            WatchdogAction.Launch => $"launch {argument}",
            WatchdogAction.Notify => "notify",
            WatchdogAction.LogAlert => "log",
            _ => r.ActionType.ToString()
        };
    }

    private static string DescribeDetails(WatchdogRule r)
    {
        var held = r.ConditionType == WatchdogCondition.RunningLongerThan || r.DurationSeconds <= 0 ? "" : $" for {r.DurationSeconds}s";
        var again = r.CooldownSeconds > 0 ? $"again after {r.CooldownSeconds}s if still true" : "again once it clears";
        return $"Condition: {DescribeCondition(r)}{held} | Action: {DescribeAction(r)} | Fires {again}";
    }

    [RelayCommand]
    private void AddRule()
    {
        IsAddPanelOpen = !IsAddPanelOpen;
    }

    /// <summary>Builds a rule from the add-rule panel, or returns why the input is invalid.</summary>
    internal string? TryBuildRule(out WatchdogRule rule)
    {
        rule = new WatchdogRule();
        if (string.IsNullOrWhiteSpace(NewProcessName)) return "Please enter a target process name.";
        if (NewThreshold < 0) return "The threshold can't be negative.";
        if (NewDuration < 0 || NewActionDuration < 0 || NewCooldown < 0) return "Durations can't be negative.";

        var action = SelectedAction;
        var argument = NewActionArgument?.Trim() ?? "";
        switch (action)
        {
            case WatchdogAction.SetPriority when argument.Length > 0 && !WatchdogArguments.TryParsePriority(argument, out _):
                return $"Unknown priority '{argument}'. Use Idle, BelowNormal, Normal, AboveNormal, High or RealTime.";
            case WatchdogAction.SetIoPriority when argument.Length > 0 && !WatchdogArguments.TryParseIoPriority(argument, out _):
                return $"Unknown I/O priority '{argument}'. Use VeryLow, Low, Normal or High.";
            case WatchdogAction.SetAffinity when !WatchdogArguments.TryParseAffinity(argument, out _):
                return "Set Affinity needs a CPU mask, such as 0x3 for the first two CPUs.";
            case WatchdogAction.Launch when !WatchdogArguments.TrySplitCommandLine(argument, out _, out _):
                return "Launch Program needs the program to start.";
        }
        if (action == WatchdogAction.TrimWorkingSet) argument = NewTrimContinuously ? WatchdogArguments.ContinuousTrim : "";
        else if (!ActionTakesArgument) argument = "";

        rule = new WatchdogRule
        {
            TargetProcessName = NormalizeExeName(NewProcessName),
            ConditionType = SelectedCondition,
            ThresholdValue = NewThreshold,
            DurationSeconds = NewDuration,
            MemoryMetric = NewMemoryMetric == MemoryMetricOptions[1] ? WatchdogMemoryMetric.PrivateBytes : WatchdogMemoryMetric.WorkingSet,
            ActionType = action,
            ActionArgument = argument.Length > 0 ? argument : null,
            ActionDurationSeconds = ActionTakesDuration ? NewActionDuration : 0,
            CooldownSeconds = NewCooldown,
            IsEnabled = true
        };
        return null;
    }

    [RelayCommand]
    private async Task SaveNewRuleAsync()
    {
        var error = TryBuildRule(out var entity);
        if (error != null)
        {
            MessageBox.Show(error, "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var result = await _config.SaveWatchdogRuleAsync(entity);
            if (!result.Success)
            {
                MessageBox.Show($"Watchdog rule not saved: {result.Error}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (result.Id is int id) entity.Id = id;

            WatchdogRules.Add(CreateDisplayItem(entity));

            NewProcessName = string.Empty;
            NewActionArgument = string.Empty;
            IsAddPanelOpen = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save watchdog rule: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void CancelAddRule()
    {
        IsAddPanelOpen = false;
        NewProcessName = string.Empty;
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(WatchdogRuleDisplayItem item)
    {
        if (item == null) return;
        try
        {
            var result = await _config.DeleteWatchdogRuleAsync(item.Id);
            if (!result.Success)
            {
                MessageBox.Show($"Watchdog rule not deleted: {result.Error}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            WatchdogRules.Remove(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error deleting watchdog rule: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Sets Keep Running on the process's rule, creating the rule if it has none.</summary>
    [RelayCommand]
    private async Task AddKeepRunningAsync()
    {
        if (string.IsNullOrWhiteSpace(NewKeepRunningName)) return;
        var name = NormalizeExeName(NewKeepRunningName);
        try
        {
            var result = await SetKeepRunningAsync(name, true);
            if (!result.Success)
            {
                MessageBox.Show($"Keep Running not saved: {result.Error}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            NewKeepRunningName = string.Empty;
            LoadKeepRunning();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save Keep Running: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Clears Keep Running. The process rule stays, since it may carry other settings.</summary>
    [RelayCommand]
    private async Task RemoveKeepRunningAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var result = await SetKeepRunningAsync(name, false);
            if (!result.Success)
            {
                MessageBox.Show($"Keep Running not removed: {result.Error}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LoadKeepRunning();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to remove Keep Running: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Sets Keep Running on the process's rule, creating the rule if it has none (and Keep Running is wanted).</summary>
    internal Task<SaveResult> SetKeepRunningAsync(string processName, bool keepRunning) =>
        _config.UpdateProcessRuleAsync(processName, rule =>
        {
            if (rule.Id == 0 && !keepRunning) return false; // no rule, nothing to clear
            rule.KeepRunning = keepRunning;
            return true;
        });

    private static string NormalizeExeName(string name)
    {
        var clean = name.Trim();
        if (!clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !clean.Equals("System", StringComparison.OrdinalIgnoreCase))
            clean += ".exe";
        return clean;
    }

    private static T Lookup<T>(IReadOnlyList<string> labels, T[] values, string label, T fallback)
    {
        for (var i = 0; i < labels.Count; i++)
            if (labels[i] == label) return values[i];
        return fallback;
    }
}

public class WatchdogRuleDisplayItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public int Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
}
