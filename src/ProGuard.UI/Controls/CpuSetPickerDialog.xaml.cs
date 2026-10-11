using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ProGuard.Core.Helpers;
using Wpf.Ui.Controls;

namespace ProGuard.UI.Controls;

/// <summary>
/// Picks CPU Set IDs for a rule from GetSystemCpuSetInformation, showing each set's logical processor, core and
/// efficiency class (on hybrid CPUs the highest class is the P-cores).
/// </summary>
public partial class CpuSetPickerDialog : FluentWindow
{
    public ObservableCollection<CpuSetItem> Items { get; } = new();

    /// <summary>The chosen IDs as stored on a rule ("256,257"), or "" when none are ticked.</summary>
    public string SelectedIdsText => CpuSets.FormatIds(Items.Where(i => i.IsSelected).Select(i => i.Info.Id));

    public CpuSetPickerDialog() : this(null, null) { }

    public CpuSetPickerDialog(string? initialIds, string? processName)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(processName)) Title = $"Choose CPU Sets for {processName}";

        CpuSets.TryParseIds(initialIds ?? "", out var initial);
        var sets = CpuSets.GetSystemCpuSets();
        var classes = sets.Select(s => s.EfficiencyClass).Distinct().Count();
        var maxClass = sets.Count == 0 ? 0 : sets.Max(s => s.EfficiencyClass);

        foreach (var set in sets)
            Items.Add(new CpuSetItem(set, isHybrid: classes > 1, isFastest: set.EfficiencyClass == maxClass, UpdateSummary) { IsSelected = initial.Contains(set.Id) });

        FastestButton.IsEnabled = SlowestButton.IsEnabled = classes > 1;
        CpuSetList.ItemsSource = Items;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var count = Items.Count(i => i.IsSelected);
        SummaryText.Text = count == 0
            ? $"None selected: the rule will leave CPU Sets alone. {Items.Count} CPU Sets on this machine."
            : $"Selected {count}/{Items.Count}: IDs {SelectedIdsText}";
    }

    private void Select(Func<CpuSetItem, bool> predicate)
    {
        foreach (var item in Items) item.IsSelected = predicate(item);
        UpdateSummary();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Select(_ => true);
    private void SelectFastest_Click(object sender, RoutedEventArgs e) => Select(i => i.IsFastest);
    private void SelectSlowest_Click(object sender, RoutedEventArgs e) => Select(i => !i.IsFastest);
    private void ClearAll_Click(object sender, RoutedEventArgs e) => Select(_ => false);

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

public class CpuSetItem : ObservableObject
{
    private readonly Action _onChanged;
    private bool _isSelected;

    public CpuSetItem(CpuSetInfo info, bool isHybrid, bool isFastest, Action onChanged)
    {
        Info = info;
        IsHybrid = isHybrid;
        IsFastest = isFastest;
        _onChanged = onChanged;
    }

    public CpuSetInfo Info { get; }
    public bool IsHybrid { get; }
    public bool IsFastest { get; }

    public string Name => $"CPU Set {Info.Id}";
    public string Location => $"Logical CPU {Info.Group}:{Info.LogicalProcessorIndex} · Core {Info.CoreIndex}" + (Info.Parked ? " · parked" : "");
    public string ClassTag => IsHybrid
        ? $"{(IsFastest ? "P-core" : "E-core")} (class {Info.EfficiencyClass})"
        : $"Efficiency class {Info.EfficiencyClass}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) _onChanged();
        }
    }
}
