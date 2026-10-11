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
            db.Database.EnsureCreated();

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
            db.Database.EnsureCreated();

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

            db.ProcessRules.Add(entity);
            db.SaveChanges();

            Rules.Add(new RuleDisplayItem
            {
                Id = entity.Id,
                ProcessName = entity.ProcessName,
                Priority = NewPriority,
                Affinity = "All",
                CpuLimit = cpuLimitVal.HasValue ? $"{cpuLimitVal}%" : "None",
                MaxInstances = "None",
                IsDisallowed = entity.DisallowExecution
            });

            NewProcessName = string.Empty;
            IsAddPanelOpen = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save rule: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            db.Database.EnsureCreated();

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
    public string MaxInstances { get; set; } = string.Empty;
    public bool IsDisallowed { get; set; }
}
