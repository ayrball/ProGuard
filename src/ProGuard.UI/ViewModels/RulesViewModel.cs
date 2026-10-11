using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.UI.Controls;
using ProGuard.UI.IPC;
using ProGuard.UI.Services;

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

    // Instance limit
    [ObservableProperty]
    private string newMaxInstances = "None";

    [ObservableProperty]
    private bool newMaxInstancesAllSessions;

    // Instance Balancer
    public string[] BalancerModes { get; } = { BalancerOff, BalancerEqual, BalancerFixed };
    private const string BalancerOff = "Off";
    private const string BalancerEqual = "Equal cores per instance";
    private const string BalancerFixed = "Fixed cores per instance";

    [ObservableProperty]
    private string newBalancerMode = BalancerOff;

    [ObservableProperty]
    private string newBalancerCoresPerInstance = "1";

    [ObservableProperty]
    private bool newBalancerAvoidSmt;

    [ObservableProperty]
    private string newBalancerReservedCores = "0";

    [ObservableProperty]
    private string newBalancerMinCpu = "0";

    // Startup boost
    [ObservableProperty]
    private string newStartupBoostPriority = "None";

    [ObservableProperty]
    private string newStartupBoostSeconds = "10";

    // Persistent per-process defaults. Index 0 of every option list is "(unchanged)", which stores null.
    private const string Unchanged = "(unchanged)";
    public IReadOnlyList<string> IoPriorityOptions { get; } = WithUnchanged(ProcessRuleOptions.IoPriorities);
    /// <summary>The index is the stored MEMORY_PRIORITY value (1..5).</summary>
    public IReadOnlyList<string> MemoryPriorityOptions { get; } = WithUnchanged(ProcessRuleOptions.MemoryPriorities.Skip(ProcessRuleOptions.MinMemoryPriority));
    public IReadOnlyList<string> EfficiencyModeOptions { get; } = new[] { Unchanged, "On (EcoQoS)", "Off" };
    public IReadOnlyList<string> PriorityBoostOptions { get; } = new[] { Unchanged, "Disable boost", "Allow boost" };
    public IReadOnlyList<string> GpuPriorityOptions { get; } = WithUnchanged(ProcessRuleOptions.GpuPriorities);
    public IReadOnlyList<string> GpuPreferenceOptions { get; } = WithUnchanged(ProcessRuleOptions.GpuPreferences);

    [ObservableProperty] private int newIoPriorityIndex;
    [ObservableProperty] private int newMemoryPriorityIndex;
    [ObservableProperty] private int newEfficiencyModeIndex;
    [ObservableProperty] private int newPriorityBoostIndex;
    [ObservableProperty] private int newGpuPriorityIndex;
    [ObservableProperty] private int newGpuPreferenceIndex;
    [ObservableProperty] private string newCpuSetIds = string.Empty;
    [ObservableProperty] private bool newReapplyIfChanged;

    public ObservableCollection<RuleDisplayItem> Rules { get; set; } = new();

    private readonly ConfigWriter _config;

    public RulesViewModel() : this(new ConfigWriter(new EngineProxy())) { }

    public RulesViewModel(ConfigWriter config)
    {
        _config = config;
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
                string maxInst = FormatMaxInstances(r);

                Rules.Add(new RuleDisplayItem
                {
                    Id = r.Id,
                    ProcessName = r.ProcessName,
                    Priority = prioText,
                    Affinity = affText,
                    CpuLimit = cpuText,
                    CpuAffinityLimit = FormatCpuAffinityLimit(r),
                    MaxInstances = maxInst,
                    InstanceBalancer = FormatBalancer(r),
                    StartupBoost = FormatStartupBoost(r),
                    IsDisallowed = r.DisallowExecution,
                    Defaults = DescribeDefaults(r)
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
    private async Task SaveNewRuleAsync()
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

            var cpuSetIds = string.IsNullOrWhiteSpace(NewCpuSetIds) ? null : NewCpuSetIds.Trim();
            if (cpuSetIds != null && !CpuSets.TryParseIds(cpuSetIds, out _))
            {
                MessageBox.Show("CPU Sets must be a comma-separated list of CPU Set IDs (use Pick...).", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var entity = new ProcessRule
            {
                ProcessName = cleanName,
                PriorityClass = prioVal,
                CpuLimitPercent = cpuLimitVal,
                DisallowExecution = NewIsDisallowed,
                IoPriority = FromOptionIndex(NewIoPriorityIndex),
                MemoryPriority = NewMemoryPriorityIndex > 0 ? NewMemoryPriorityIndex : null,
                EfficiencyMode = FromTriState(NewEfficiencyModeIndex),
                DisablePriorityBoost = FromTriState(NewPriorityBoostIndex),
                CpuSetIds = cpuSetIds,
                GpuPriority = FromOptionIndex(NewGpuPriorityIndex),
                GpuPreference = FromOptionIndex(NewGpuPreferenceIndex),
                ReapplyIfChanged = NewReapplyIfChanged
            };
            ApplyInstanceAndBoostFields(entity);

            var affinityLimitError = ApplyCpuAffinityLimit(entity, NewCpuLimitThreshold, NewCpuLimitOverSeconds, NewCpuLimitCores, NewCpuLimitDuration);
            if (affinityLimitError != null)
            {
                MessageBox.Show(affinityLimitError, "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = await _config.SaveProcessRuleAsync(entity);
            if (!result.Success)
            {
                MessageBox.Show($"Rule not saved: {result.Error}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Saving replaces any rule for the same process, so reload rather than append.
            LoadRules();

            NewProcessName = string.Empty;
            NewCpuLimitThreshold = string.Empty;
            ResetPersistentDefaults();
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

    /// <summary>Copies the instance limit, Instance Balancer and startup boost inputs onto <paramref name="rule"/>.</summary>
    public void ApplyInstanceAndBoostFields(ProcessRule rule)
    {
        rule.MaxInstances = int.TryParse(NewMaxInstances?.Trim(), out var max) && max >= 1 ? max : null;
        rule.MaxInstancesAllSessions = NewMaxInstancesAllSessions;

        rule.InstanceBalancerMode = NewBalancerMode switch
        {
            BalancerEqual => InstanceBalancerMode.EqualCoresPerInstance,
            BalancerFixed => InstanceBalancerMode.FixedCoresPerInstance,
            _ => null
        };
        rule.InstanceBalancerCoresPerInstance = int.TryParse(NewBalancerCoresPerInstance?.Trim(), out var cores) && cores >= 1 ? cores : null;
        rule.InstanceBalancerAvoidSmt = NewBalancerAvoidSmt;
        rule.InstanceBalancerReservedCores = int.TryParse(NewBalancerReservedCores?.Trim(), out var reserved) && reserved > 0 ? reserved : null;
        rule.InstanceBalancerMinCpuPercent =
            double.TryParse(NewBalancerMinCpu?.Replace("%", "").Trim(), out var minCpu) && minCpu > 0 ? Math.Min(minCpu, 100) : null;

        rule.StartupBoostPriorityClass = Enum.TryParse<ProcessPriorityClass>(NewStartupBoostPriority, out var boost) ? (int)boost : null;
        rule.StartupBoostSeconds = int.TryParse(NewStartupBoostSeconds?.Trim(), out var seconds) ? Math.Clamp(seconds, 1, 3600) : null;
    }

    private static string FormatMaxInstances(ProcessRule r) =>
        r.MaxInstances is int max ? (r.MaxInstancesAllSessions ? $"{max} (all sessions)" : max.ToString()) : "None";

    private static string FormatBalancer(ProcessRule r) => r.InstanceBalancerMode switch
    {
        InstanceBalancerMode.EqualCoresPerInstance => "Equal",
        InstanceBalancerMode.FixedCoresPerInstance => $"{r.InstanceBalancerCoresPerInstance ?? 1} per instance",
        _ => "Off"
    };

    private static string FormatStartupBoost(ProcessRule r) =>
        r.StartupBoostPriorityClass is int p ? $"{(ProcessPriorityClass)p} for {r.StartupBoostSeconds ?? 10}s" : "None";

    [RelayCommand]
    private void CancelAddRule()
    {
        IsAddPanelOpen = false;
        NewProcessName = string.Empty;
        ResetPersistentDefaults();
    }

    [RelayCommand]
    private void PickCpuSets()
    {
        try
        {
            var dialog = new CpuSetPickerDialog(NewCpuSetIds, string.IsNullOrWhiteSpace(NewProcessName) ? null : NewProcessName.Trim())
            {
                Owner = Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() == true) NewCpuSetIds = dialog.SelectedIdsText;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to read the CPU Sets: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetPersistentDefaults()
    {
        NewIoPriorityIndex = NewMemoryPriorityIndex = NewEfficiencyModeIndex = NewPriorityBoostIndex = 0;
        NewGpuPriorityIndex = NewGpuPreferenceIndex = 0;
        NewCpuSetIds = string.Empty;
        NewReapplyIfChanged = false;
    }

    private static IReadOnlyList<string> WithUnchanged(IEnumerable<string> options) => new[] { Unchanged }.Concat(options).ToArray();
    private static int? FromOptionIndex(int index) => index > 0 ? index - 1 : null;
    private static bool? FromTriState(int index) => index switch { 1 => true, 2 => false, _ => null };

    /// <summary>One-line summary of a rule's persistent per-process defaults for the rules grid.</summary>
    internal static string DescribeDefaults(ProcessRule r)
    {
        var parts = new List<string>();
        if (r.IoPriority is int io) parts.Add("I/O " + ProcessRuleOptions.Label(ProcessRuleOptions.IoPriorities, io));
        if (r.MemoryPriority is int mem) parts.Add("Memory " + ProcessRuleOptions.Label(ProcessRuleOptions.MemoryPriorities, mem));
        if (r.EfficiencyMode is bool eco) parts.Add(eco ? "Eco on" : "Eco off");
        if (r.DisablePriorityBoost is bool boost) parts.Add(boost ? "No boost" : "Boost");
        if (r.CpuSetIds != null) parts.Add(r.CpuSetIds.Length == 0 ? "CPU Sets cleared" : "CPU Sets " + r.CpuSetIds);
        if (r.GpuPriority is int gpu) parts.Add("GPU " + ProcessRuleOptions.Label(ProcessRuleOptions.GpuPriorities, gpu));
        if (r.GpuPreference is int pref) parts.Add(ProcessRuleOptions.Label(ProcessRuleOptions.GpuPreferences, pref));
        if (parts.Count > 0 && r.ReapplyIfChanged) parts.Add("re-apply");
        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule == null)
        {
            MessageBox.Show("Please select a rule to delete.", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var selected = SelectedRule;
            var result = await _config.DeleteProcessRuleAsync(selected.Id);
            if (!result.Success)
            {
                MessageBox.Show($"Rule not deleted: {result.Error}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Rules.Remove(selected);
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
    public string InstanceBalancer { get; set; } = string.Empty;
    public string StartupBoost { get; set; } = string.Empty;
    public bool IsDisallowed { get; set; }
    /// <summary>Summary of the persistent per-process defaults (I/O, memory, EcoQoS, boost, CPU Sets, GPU).</summary>
    public string Defaults { get; set; } = string.Empty;
}
