using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;

namespace ProGuard.UI.ViewModels;

/// <summary>One CPU Set (a logical processor as the CPU Sets API names it) in the picker.</summary>
public sealed partial class CpuSetItem : ObservableObject
{
    public int Id { get; }
    public int Group { get; }
    public int LogicalProcessor { get; }
    public int Core { get; }
    public int EfficiencyClass { get; }
    public bool IsParked { get; }

    /// <summary>True on a CPU whose cores differ in speed (P-cores and E-cores).</summary>
    public bool IsHybrid { get; }

    /// <summary>The fastest efficiency class on this CPU: the P-cores on a hybrid one.</summary>
    public bool IsFast { get; }

    public string Label { get; }

    /// <summary>"CORE 3 · P-CORE", or just the core on a CPU whose cores are all alike.</summary>
    public string Detail => IsHybrid ? $"CORE {Core} · {(IsFast ? "P-CORE" : "E-CORE")}" : $"CORE {Core}";

    /// <summary>"ID 259", plus "PARKED" when Windows has the processor asleep.</summary>
    public string Footer => IsParked ? $"ID {Id} · PARKED" : $"ID {Id}";

    [ObservableProperty] private bool isOn;

    public CpuSetItem(CpuSetDto dto, bool isHybrid, bool isFast, bool showGroup, bool isOn)
    {
        Id = dto.Id;
        Group = dto.Group;
        LogicalProcessor = dto.LogicalProcessorIndex;
        Core = dto.CoreIndex;
        EfficiencyClass = dto.EfficiencyClass;
        IsParked = dto.IsParked;
        IsHybrid = isHybrid;
        IsFast = isFast;
        Label = showGroup ? $"{dto.Group}:{dto.LogicalProcessorIndex:00}" : dto.LogicalProcessorIndex.ToString("00", CultureInfo.InvariantCulture);
        this.isOn = isOn;
    }
}

/// <summary>
/// Pick the CPU Sets a rule asks Windows to prefer for a program. The result is the comma-separated ID list stored on
/// the rule: an empty list clears the process's CPU Sets, and "leave alone" hands back null.
/// </summary>
public sealed partial class CpuSetDialogViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly Action<string?> _picked;
    private readonly int _missing;

    public string Title { get; }
    public ObservableCollection<CpuSetItem> Sets { get; } = new();

    /// <summary>True when the cores differ in speed, so the P-core / E-core buttons mean something.</summary>
    public bool IsHybrid { get; }

    /// <summary>True when the rule currently has CPU Sets of its own, so "leave alone" would change something.</summary>
    public bool CanLeaveAlone { get; }

    [ObservableProperty] private string summary = string.Empty;

    /// <param name="sets">The machine's CPU Sets, from the engine.</param>
    /// <param name="currentIds">The rule's stored list: null (leave alone), "" (clear) or comma-separated IDs.</param>
    /// <param name="picked">Called with the chosen list: "" for none (clear), IDs, or null for "leave alone".</param>
    public CpuSetDialogViewModel(ShellViewModel shell, string title, IEnumerable<CpuSetDto> sets, string? currentIds, Action<string?> picked)
    {
        _shell = shell;
        _picked = picked;
        Title = title;
        CanLeaveAlone = currentIds != null;

        TryParseIds(currentIds, out var wanted);
        var ordered = sets.OrderBy(s => s.Group).ThenBy(s => s.LogicalProcessorIndex).ThenBy(s => s.Id).ToList();
        int max = ordered.Count == 0 ? 0 : ordered.Max(s => s.EfficiencyClass);
        IsHybrid = ordered.Select(s => s.EfficiencyClass).Distinct().Count() > 1;
        bool showGroup = ordered.Any(s => s.Group != 0);

        foreach (var set in ordered)
        {
            var item = new CpuSetItem(set, IsHybrid, isFast: set.EfficiencyClass == max, showGroup, isOn: wanted.Contains(set.Id));
            item.PropertyChanged += (_, _) => Refresh();
            Sets.Add(item);
        }

        _missing = wanted.Count(id => ordered.All(s => s.Id != id));
        Refresh();
    }

    /// <summary>The chosen IDs as stored on a rule ("256,257"), or "" when none are lit.</summary>
    public string Ids => string.Join(",", Sets.Where(s => s.IsOn).Select(s => s.Id).OrderBy(i => i).Select(i => i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Reads a stored ID list (commas, semicolons or spaces between IDs). Empty text is a valid, empty list. Returns
    /// false for null or anything that isn't a list of whole, non-negative numbers.
    /// </summary>
    public static bool TryParseIds(string? text, out IReadOnlyList<int> ids)
    {
        var list = new List<int>();
        ids = list;
        if (text == null) return false;
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return false;
            if (!list.Contains(id)) list.Add(id);
        }
        return true;
    }

    private void Refresh()
    {
        int on = Sets.Count(s => s.IsOn);
        var text = on == 0
            ? "NONE PICKED · APPLYING CLEARS THE PROGRAM'S CPU SETS"
            : $"{on} OF {Sets.Count} · IDS {Ids}";
        if (_missing > 0) text += $" · {_missing} SAVED ID{(_missing == 1 ? "" : "S")} NOT ON THIS MACHINE";
        Summary = text;
    }

    private void Set(Func<CpuSetItem, bool> on)
    {
        foreach (var item in Sets) item.IsOn = on(item);
    }

    [RelayCommand] private void All() => Set(_ => true);
    [RelayCommand] private void None() => Set(_ => false);
    [RelayCommand] private void Fast() => Set(s => s.IsFast);
    [RelayCommand] private void Slow() => Set(s => !s.IsFast);

    [RelayCommand]
    private void Apply()
    {
        _shell.CloseDialog();
        _picked(Ids);
    }

    [RelayCommand]
    private void LeaveAlone()
    {
        _shell.CloseDialog();
        _picked(null);
    }
}
