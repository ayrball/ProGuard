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

public partial class RulesViewModel : ViewModelBase
{
    [ObservableProperty]
    private RuleDisplayItem? selectedRule;

    [ObservableProperty]
    private bool isAddPanelOpen;

    [ObservableProperty]
    private string newProcessName = string.Empty;

    [ObservableProperty]
    private string newPriority = "Normal";

    [ObservableProperty]
    private string newCpuLimit = "None";

    [ObservableProperty]
    private bool newIsDisallowed;

    // CPU Limiter, affinity-reduction mode. A blank threshold leaves it off.
    [ObservableProperty]
    private string newCpuLimitThreshold = string.Empty;

    [ObservableProperty]
    private string newCpuLimitOverSeconds = CpuAffinityLimitDefaults.OverThresholdSeconds.ToString();

    [ObservableProperty]
    private string newCpuLimitCores = CpuAffinityLimitDefaults.CoresToRemove.ToString();

    [ObservableProperty]
    private string newCpuLimitDuration = CpuAffinityLimitDefaults.DurationSeconds.ToString();

    public ObservableCollection<RuleDisplayItem> Rules { get; set; } = new();

    public RulesViewModel()
    {
        LoadRules();
    }

    public void LoadRules()
    {
        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();

            var dbRules = db.ProcessRules.ToList();

            Rules.Clear();
            foreach (var r in dbRules)
            {
                string prioText = r.PriorityClass.HasValue ? ((ProcessPriorityClass)r.PriorityClass.Value).ToString() : "Normal";
                string affText = r.AffinityMask.HasValue ? ProcessListViewModel.FormatAffinityText(r.AffinityMask.Value) : "All Cores";
                string cpuText = r.CpuLimitPercent.HasValue ? $"{r.CpuLimitPercent.Value}%" : "None";
                string maxInst = r.MaxInstances.HasValue ? r.MaxInstances.Value.ToString() : "None";

                Rules.Add(new RuleDisplayItem
                {
                    Id = r.Id,
                    ProcessName = r.ProcessName,
                    Priority = prioText,
                    Affinity = affText,
                    CpuLimit = cpuText,
                    CpuAffinityLimit = FormatCpuAffinityLimit(r),
                    MaxInstances = maxInst,
                    IsDisallowed = r.DisallowExecution
                });
            }
        }
        catch (Exception ex)
        {
            // Leave the list empty rather than showing invented rules the engine doesn't know about.
            Debug.WriteLine($"Failed to load process rules: {ex}");
        }
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
            MessageBox.Show("Please enter a process name (e.g., game.exe).", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string cleanName = NewProcessName.Trim();
        if (!cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            cleanName += ".exe";
        }

        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();

            int? prioVal = null;
            if (Enum.TryParse<ProcessPriorityClass>(NewPriority, out var pc))
            {
                prioVal = (int)pc;
            }

            double? cpuLimitVal = null;
            if (double.TryParse(NewCpuLimit.Replace("%", "").Trim(), out var parsedCpu))
            {
                cpuLimitVal = parsedCpu;
            }

            var entity = new ProcessRule
            {
                ProcessName = cleanName,
                PriorityClass = prioVal,
                CpuLimitPercent = cpuLimitVal,
                DisallowExecution = NewIsDisallowed
            };

            var affinityLimitError = ApplyCpuAffinityLimit(entity, NewCpuLimitThreshold, NewCpuLimitOverSeconds, NewCpuLimitCores, NewCpuLimitDuration);
            if (affinityLimitError != null)
            {
                MessageBox.Show(affinityLimitError, "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            db.ProcessRules.Add(entity);
            db.SaveChanges();

            Rules.Add(new RuleDisplayItem
            {
                Id = entity.Id,
                ProcessName = entity.ProcessName,
                Priority = NewPriority,
                Affinity = "All",
                CpuLimit = cpuLimitVal.HasValue ? $"{cpuLimitVal}%" : "None",
                CpuAffinityLimit = FormatCpuAffinityLimit(entity),
                MaxInstances = "None",
                IsDisallowed = entity.DisallowExecution
            });

            NewProcessName = string.Empty;
            NewCpuLimitThreshold = string.Empty;
            IsAddPanelOpen = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save rule: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Fills the CPU Limiter (affinity reduction) fields of <paramref name="rule"/> from the editor's text boxes.
    /// A blank threshold turns the mode off. Returns an error message, or null when the input is valid.
    /// </summary>
    public static string? ApplyCpuAffinityLimit(ProcessRule rule, string threshold, string overSeconds, string cores, string duration)
    {
        if (string.IsNullOrWhiteSpace(threshold) || threshold.Trim().Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            rule.CpuLimitThresholdPercent = null;
            rule.CpuLimitOverThresholdSeconds = null;
            rule.CpuLimitCoresToRemove = null;
            rule.CpuLimitDurationSeconds = null;
            return null;
        }

        if (!double.TryParse(threshold.Replace("%", "").Trim(), out var t) || t <= 0 || t > 100)
            return "CPU Limiter threshold must be a percentage between 1 and 100.";
        if (!int.TryParse(overSeconds.Trim(), out var o) || o < 0)
            return "CPU Limiter time over threshold must be 0 or more seconds.";
        if (!int.TryParse(cores.Trim(), out var c) || c < 1)
            return "CPU Limiter must remove at least 1 core.";
        if (!int.TryParse(duration.Trim(), out var d) || d < 1)
            return "CPU Limiter duration must be at least 1 second.";

        rule.CpuLimitThresholdPercent = t;
        rule.CpuLimitOverThresholdSeconds = o;
        rule.CpuLimitCoresToRemove = c;
        rule.CpuLimitDurationSeconds = d;
        return null;
    }

    /// <summary>One-line summary of a rule's CPU Limiter (affinity reduction) settings for the grid.</summary>
    public static string FormatCpuAffinityLimit(ProcessRule rule)
    {
        if (!rule.CpuLimitThresholdPercent.HasValue) return "None";
        var over = rule.CpuLimitOverThresholdSeconds ?? CpuAffinityLimitDefaults.OverThresholdSeconds;
        var cores = rule.CpuLimitCoresToRemove ?? CpuAffinityLimitDefaults.CoresToRemove;
        var duration = rule.CpuLimitDurationSeconds ?? CpuAffinityLimitDefaults.DurationSeconds;
        return $"≥{rule.CpuLimitThresholdPercent.Value}% for {over}s: -{cores} core{(cores == 1 ? "" : "s")} for {duration}s";
    }

    [RelayCommand]
    private void CancelAddRule()
    {
        IsAddPanelOpen = false;
        NewProcessName = string.Empty;
    }

    [RelayCommand]
    private void DeleteRule()
    {
        if (SelectedRule == null)
        {
            MessageBox.Show("Please select a rule to delete.", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();

            var entity = db.ProcessRules.FirstOrDefault(r => r.Id == SelectedRule.Id || r.ProcessName == SelectedRule.ProcessName);
            if (entity != null)
            {
                db.ProcessRules.Remove(entity);
                db.SaveChanges();
            }

            Rules.Remove(SelectedRule);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error deleting rule: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public class RuleDisplayItem
{
    public int Id { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string Affinity { get; set; } = string.Empty;
    public string CpuLimit { get; set; } = string.Empty;
    public string CpuAffinityLimit { get; set; } = "None";
    public string MaxInstances { get; set; } = string.Empty;
    public bool IsDisallowed { get; set; }
}
