using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

public sealed partial class CoreToggle : ObservableObject
{
    public int Index { get; }
    public string Label => Index.ToString("00");

    [ObservableProperty] private bool isOn;

    public CoreToggle(int index, bool isOn)
    {
        Index = index;
        this.isOn = isOn;
    }
}

/// <summary>Pick which logical processors a process may run on.</summary>
public sealed partial class AffinityDialogViewModel : ObservableObject
{
    private readonly EngineHub _hub;
    private readonly ShellViewModel _shell;
    private readonly ProcessRow _row;
    private readonly int _logical;

    public string Title => _row.Name.ToUpperInvariant();
    public ObservableCollection<CoreToggle> Cores { get; } = new();

    [ObservableProperty] private string summary = string.Empty;

    public AffinityDialogViewModel(EngineHub hub, ShellViewModel shell, ProcessRow row, int logical)
    {
        _hub = hub;
        _shell = shell;
        _row = row;
        _logical = logical;

        int count = Math.Clamp(logical, 1, 64);
        long mask = row.AffinityMask == 0 ? -1 : row.AffinityMask;
        for (int i = 0; i < count; i++)
        {
            var core = new CoreToggle(i, (mask & (1L << i)) != 0);
            core.PropertyChanged += (_, _) => Refresh();
            Cores.Add(core);
        }
        Refresh();
    }

    private long Mask => Cores.Where(c => c.IsOn).Aggregate(0L, (m, c) => m | (1L << c.Index));

    private void Refresh()
    {
        int on = Cores.Count(c => c.IsOn);
        Summary = on == 0 ? "PICK AT LEAST ONE" : $"{on} OF {Cores.Count} THREADS";
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void Set(Func<int, bool> on)
    {
        foreach (var core in Cores) core.IsOn = on(core.Index);
    }

    [RelayCommand] private void All() => Set(_ => true);
    [RelayCommand] private void FirstHalf() => Set(i => i < Cores.Count / 2);
    [RelayCommand] private void SecondHalf() => Set(i => i >= Cores.Count / 2);
    /// <summary>Every other thread: on SMT machines that is one thread per physical core.</summary>
    [RelayCommand] private void Even() => Set(i => i % 2 == 0);

    private bool CanApply() => Mask != 0;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        _shell.CloseDialog();
        long mask = Mask;
        // Every core again: the rule stops naming cores rather than naming all of them.
        bool all = Cores.All(c => c.IsOn) && Cores.Count >= Math.Min(_logical, 64);
        await _shell.Processes.SetAsync(_row, CommandTypes.SetAffinity, mask, $"{_row.Name} · {Summary}", $"CORES {Summary}",
            r => r.AffinityMask = all ? null : mask);
    }
}

/// <summary>Cap a process at a share of the whole machine's CPU.</summary>
public sealed partial class CpuLimitDialogViewModel : ObservableObject
{
    private readonly EngineHub _hub;
    private readonly ShellViewModel _shell;
    private readonly ProcessRow _row;

    public string Title => _row.Name.ToUpperInvariant();
    public bool HasLimit => _row.CpuLimitPercent.HasValue;
    public IReadOnlyList<int> Presets { get; } = new[] { 5, 10, 25, 50, 75 };

    [ObservableProperty] private double percent;

    public CpuLimitDialogViewModel(EngineHub hub, ShellViewModel shell, ProcessRow row)
    {
        _hub = hub;
        _shell = shell;
        _row = row;
        percent = row.CpuLimitPercent ?? 25;
    }

    public int Whole => (int)Math.Round(Percent);

    partial void OnPercentChanged(double value) => OnPropertyChanged(nameof(Whole));

    [RelayCommand]
    private void Preset(int value) => Percent = value;

    [RelayCommand]
    private async Task ApplyAsync()
    {
        _shell.CloseDialog();
        int cap = Math.Clamp(Whole, 1, 99);
        await _shell.Processes.SetAsync(_row, CommandTypes.SetCpuLimit, cap, $"{_row.Name} capped at {cap}%", $"CAP {cap}%",
            r => r.CpuLimitPercent = cap);
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        _shell.CloseDialog();
        await _shell.Processes.SetAsync(_row, CommandTypes.RemoveCpuLimit, null, $"{_row.Name} uncapped", "UNCAPPED",
            r => r.CpuLimitPercent = null);
    }
}
