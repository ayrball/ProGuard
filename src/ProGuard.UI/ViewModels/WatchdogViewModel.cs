using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Core.Data;
using ProGuard.Core.Models;

namespace ProGuard.UI.ViewModels;

public partial class WatchdogViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool isAddPanelOpen;

    [ObservableProperty]
    private string newProcessName = string.Empty;

    [ObservableProperty]
    private string newCondition = "CPU Above";

    [ObservableProperty]
    private double newThreshold = 85.0;

    [ObservableProperty]
    private int newDuration = 30;

    [ObservableProperty]
    private string newAction = "Terminate";

    [ObservableProperty]
    private string recentTriggerText = "No watchdog events recorded yet.";

    public ObservableCollection<WatchdogRuleDisplayItem> WatchdogRules { get; set; } = new();

    public WatchdogViewModel()
    {
        LoadRules();
        LoadRecentTriggers();
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

    private void LoadRecentTriggers()
    {
        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();
            var lastAction = db.ProBalanceActions.OrderByDescending(a => a.Timestamp).FirstOrDefault();
            if (lastAction != null)
            {
                RecentTriggerText = $"[{lastAction.Timestamp:HH:mm:ss}] ProBalance {lastAction.Reason}: '{lastAction.ProcessName}' (PID {lastAction.Pid})";
            }
            else
            {
                RecentTriggerText = "Engine initialized. Ready to execute watchdog triggers.";
            }
        }
        catch
        {
            RecentTriggerText = "Engine standing by. All rules active.";
        }
    }

    private WatchdogRuleDisplayItem CreateDisplayItem(WatchdogRule r)
    {
        string desc = $"IF {r.TargetProcessName} {r.ConditionType} {r.ThresholdValue} THEN {r.ActionType}";
        string details = $"Condition: {r.ConditionType} > {r.ThresholdValue} for {r.DurationSeconds}s | Action: {r.ActionType}";

        var item = new WatchdogRuleDisplayItem
        {
            Id = r.Id,
            Description = desc,
            Details = details,
            IsEnabled = r.IsEnabled
        };

        item.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(WatchdogRuleDisplayItem.IsEnabled))
            {
                try
                {
                    using var db = new ProGuardDbContext();
                    var entity = db.WatchdogRules.FirstOrDefault(w => w.Id == item.Id);
                    if (entity != null)
                    {
                        entity.IsEnabled = item.IsEnabled;
                        db.SaveChanges();
                    }
                }
                catch { }
            }
        };

        return item;
    }

    [RelayCommand]
    private void AddRule()
    {
        IsAddPanelOpen = !IsAddPanelOpen;
    }

    [RelayCommand]
    private void SaveNewRule()
    {
        if (string.IsNullOrWhiteSpace(NewProcessName))
        {
            MessageBox.Show("Please enter a target process name.", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string cleanName = NewProcessName.Trim();
        if (!cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !cleanName.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            cleanName += ".exe";
        }

        var cond = WatchdogCondition.CpuAbove;
        if (NewCondition.Contains("RAM", StringComparison.OrdinalIgnoreCase)) cond = WatchdogCondition.RamAbove;
        else if (NewCondition.Contains("Longer", StringComparison.OrdinalIgnoreCase)) cond = WatchdogCondition.RunningLongerThan;
        else if (NewCondition.Contains("Instance", StringComparison.OrdinalIgnoreCase)) cond = WatchdogCondition.InstanceCountAbove;

        var act = WatchdogAction.Terminate;
        if (NewAction.Contains("Restart", StringComparison.OrdinalIgnoreCase)) act = WatchdogAction.Restart;
        else if (NewAction.Contains("Priority", StringComparison.OrdinalIgnoreCase)) act = WatchdogAction.SetPriority;
        else if (NewAction.Contains("Affinity", StringComparison.OrdinalIgnoreCase)) act = WatchdogAction.SetAffinity;
        else if (NewAction.Contains("Log", StringComparison.OrdinalIgnoreCase)) act = WatchdogAction.LogAlert;

        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();

            var entity = new WatchdogRule
            {
                TargetProcessName = cleanName,
                ConditionType = cond,
                ThresholdValue = NewThreshold,
                DurationSeconds = NewDuration,
                ActionType = act,
                IsEnabled = true
            };

            db.WatchdogRules.Add(entity);
            db.SaveChanges();

            WatchdogRules.Add(CreateDisplayItem(entity));

            NewProcessName = string.Empty;
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
    private void DeleteRule(WatchdogRuleDisplayItem item)
    {
        if (item == null) return;
        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();

            var entity = db.WatchdogRules.FirstOrDefault(r => r.Id == item.Id);
            if (entity != null)
            {
                db.WatchdogRules.Remove(entity);
                db.SaveChanges();
            }

            WatchdogRules.Remove(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error deleting watchdog rule: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
