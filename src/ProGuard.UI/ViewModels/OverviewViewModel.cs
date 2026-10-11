using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

public sealed partial class ConsumerRow : ObservableObject
{
    [ObservableProperty] private int rank;
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private double cpuPercent;
    [ObservableProperty] private string cpuText = string.Empty;
    [ObservableProperty] private string memoryText = string.Empty;
}

/// <summary>The at-a-glance page: machine load in very large type, plus who is causing it.</summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private const int TopCount = 6;
    private readonly ShellViewModel _shell;

    public EngineHub Hub { get; }
    public ObservableCollection<ConsumerRow> TopConsumers { get; } = new();

    [ObservableProperty] private int cpu;
    [ObservableProperty] private int memory;
    [ObservableProperty] private string memoryDetail = "—";
    [ObservableProperty] private int processCount;
    [ObservableProperty] private int restrainedCount;
    [ObservableProperty] private int ruledCount;
    [ObservableProperty] private int limitedCount;
    [ObservableProperty] private string foreground = "—";
    [ObservableProperty] private bool proBalanceEnabled;
    [ObservableProperty] private string coresText = "—";
    [ObservableProperty] private bool isElevated = true;

    // Responsiveness (Process Lasso's measure of how long the CPU queue is) and the load of each logical processor.
    [ObservableProperty] private double responsiveness;
    [ObservableProperty] private string responsivenessText = "—";
    [ObservableProperty] private IReadOnlyList<double> coreLoads = Array.Empty<double>();

    // ProBalance in detail: armed, paused because the user is idle, or off; and how many restraints so far.
    [ObservableProperty] private bool proBalancePaused;
    [ObservableProperty] private string proBalanceState = "—";
    [ObservableProperty] private int restraintsTotal;
    [ObservableProperty] private string restraintsText = "—";

    [ObservableProperty] private bool performanceModeActive;
    [ObservableProperty] private string performanceModeText = "—";
    [ObservableProperty] private string activePlan = "—";
    [ObservableProperty] private string planOwner = "—";

    public OverviewViewModel(EngineHub hub, ShellViewModel shell)
    {
        Hub = hub;
        _shell = shell;
        hub.SnapshotApplied += Apply;
        for (int i = 0; i < TopCount; i++) TopConsumers.Add(new ConsumerRow { Rank = i + 1 });
    }

    internal void Apply(EngineSnapshot s)
    {
        Responsiveness = s.ResponsivenessPercent ?? 0;
        ResponsivenessText = s.ResponsivenessPercent is double r ? $"{r:0}%" : "—";
        CoreLoads = s.CoreCpuPercent.ToArray();

        ProBalancePaused = s.ProBalanceEnabled && s.ProBalancePaused;
        ProBalanceState = !s.ProBalanceEnabled ? "OFF" : s.ProBalancePaused ? "PAUSED · IDLE" : "ARMED";
        RestraintsTotal = s.ProBalanceRestraintsTotal;
        RestraintsText = $"{s.ProBalanceRestrained} NOW · {s.ProBalanceRestraintsTotal} TOTAL";

        PerformanceModeActive = s.PerformanceModeActive;
        PerformanceModeText = s.PerformanceModeActive ? "ON" : "OFF";
        ActivePlan = string.IsNullOrEmpty(s.ActivePowerPlanName) ? "—" : s.ActivePowerPlanName.ToUpperInvariant();
        PlanOwner = Fmt.PlanOwner(s.PowerPlanOwner);

        Cpu = (int)Math.Round(s.CpuPercent);
        Memory = (int)Math.Round(s.MemoryPercent);
        MemoryDetail = $"{Fmt.Bytes(s.TotalMemoryBytes - s.AvailableMemoryBytes)} OF {Fmt.Bytes(s.TotalMemoryBytes)}";
        ProcessCount = s.Processes.Count;
        RestrainedCount = s.ProBalanceRestrained;
        RuledCount = s.Processes.Count(p => p.HasRule);
        LimitedCount = s.Processes.Count(p => p.CpuLimitPercent.HasValue);
        ProBalanceEnabled = s.ProBalanceEnabled;
        CoresText = $"{s.LogicalProcessors} THREADS";
        IsElevated = s.CallerIsAdministrator;

        var fg = s.Processes.FirstOrDefault(p => p.Pid == s.ForegroundPid);
        Foreground = fg != null ? Fmt.Exe(fg.Name).ToUpperInvariant() : "—";

        // Update the fixed rows in place: no list churn, so the column never flickers.
        var top = s.Processes.Where(p => p.Pid > 4).OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.WorkingSetBytes).Take(TopCount).ToList();
        for (int i = 0; i < TopCount; i++)
        {
            var row = TopConsumers[i];
            if (i < top.Count)
            {
                row.Name = Fmt.Exe(top[i].Name);
                row.CpuPercent = top[i].CpuPercent;
                row.CpuText = $"{top[i].CpuPercent:0.0}";
                row.MemoryText = Fmt.Bytes(top[i].WorkingSetBytes);
            }
            else
            {
                row.Name = string.Empty;
                row.CpuPercent = 0;
                row.CpuText = string.Empty;
                row.MemoryText = string.Empty;
            }
        }
    }

    [RelayCommand]
    private void OpenProcesses() => _shell.GoTo(_shell.Processes);
}
