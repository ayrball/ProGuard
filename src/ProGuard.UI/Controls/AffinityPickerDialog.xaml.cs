using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ProGuard.Core.Helpers;
using Wpf.Ui.Controls;

namespace ProGuard.UI.Controls;

public partial class AffinityPickerDialog : FluentWindow
{
    public CpuTopology Topology { get; }
    public ObservableCollection<CoreItem> Cores { get; set; } = new();
    private bool _isUpdatingToggle;

    public long SelectedMask
    {
        get
        {
            long mask = 0;
            for (int i = 0; i < Cores.Count && i < 64; i++)
            {
                if (Cores[i].IsSelected)
                {
                    mask |= (1L << i);
                }
            }
            return mask == 0 ? -1L : mask;
        }
    }

    public AffinityPickerDialog() : this(-1L, null) { }

    public AffinityPickerDialog(long initialMask) : this(initialMask, null) { }

    public AffinityPickerDialog(long initialMask, string? processName)
    {
        InitializeComponent();

        Topology = SystemInfo.GetCpuTopology();
        ProcessNameText.Text = !string.IsNullOrEmpty(processName)
            ? $"Target Process: {processName}"
            : "Select cores allowed to execute this process:";

        CpuNameText.Text = $"Processor: {SystemInfo.CpuName} ({Topology.PhysicalCoreCount} Physical Cores / {Topology.LogicalProcessorCount} Threads)";

        long effectiveMask = initialMask <= 0 ? Topology.AllCoresMask : initialMask;

        foreach (var core in Topology.Cores)
        {
            bool isSelected = (effectiveMask & (1L << core.LogicalIndex)) != 0;
            var item = new CoreItem
            {
                CoreIndex = core.LogicalIndex,
                PhysicalCoreId = core.PhysicalCoreId,
                IsSmtSibling = core.IsSmtSibling,
                IsSelected = isSelected,
                OnSelectionChanged = UpdateSummary
            };
            Cores.Add(item);
        }

        CoresList.ItemsSource = Cores;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        long mask = SelectedMask;
        int selectedCount = Cores.Count(c => c.IsSelected);
        int total = Cores.Count;

        string smtStatus;
        if (mask == Topology.PhysicalCoresMask)
        {
            smtStatus = "SMT: Disabled (Physical Cores Only)";
        }
        else if (mask == Topology.AllCoresMask || mask == -1L)
        {
            smtStatus = "SMT: Enabled (All Cores)";
        }
        else if (mask == Topology.SmtSiblingsMask)
        {
            smtStatus = "SMT: Siblings Only";
        }
        else
        {
            smtStatus = "SMT: Custom Affinity Mask";
        }

        SummaryText.Text = $"Selected: {selectedCount}/{total} Threads | Mask: 0x{mask:X} | {smtStatus}";

        _isUpdatingToggle = true;
        SmtToggle.IsChecked = (mask == Topology.PhysicalCoresMask);
        _isUpdatingToggle = false;
    }

    private void SmtToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingToggle) return;
        PhysicalOnly_Click(sender, e);
    }

    private void SmtToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingToggle) return;
        SelectAll_Click(sender, e);
    }

    private void PhysicalOnly_Click(object sender, RoutedEventArgs e)
    {
        foreach (var core in Cores)
        {
            core.IsSelected = !core.IsSmtSibling;
        }
        UpdateSummary();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var core in Cores)
        {
            core.IsSelected = true;
        }
        UpdateSummary();
    }

    private void SmtSiblingsOnly_Click(object sender, RoutedEventArgs e)
    {
        foreach (var core in Cores)
        {
            core.IsSelected = core.IsSmtSibling;
        }
        UpdateSummary();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var core in Cores)
        {
            core.IsSelected = false;
        }
        UpdateSummary();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

public class CoreItem : ObservableObject
{
    public int CoreIndex { get; set; }
    public int PhysicalCoreId { get; set; }
    public bool IsSmtSibling { get; set; }
    public string Name => $"CPU {CoreIndex}";
    public string CoreTypeTag => IsSmtSibling ? "SMT Sibling" : $"Physical #{PhysicalCoreId}";
    public Action? OnSelectionChanged { get; set; }

    public Brush BadgeBackground => IsSmtSibling
        ? new SolidColorBrush(Color.FromArgb(40, 150, 150, 150))
        : new SolidColorBrush(Color.FromArgb(50, 0, 150, 255));

    public Brush BadgeForeground => IsSmtSibling
        ? new SolidColorBrush(Color.FromArgb(220, 190, 190, 190))
        : new SolidColorBrush(Color.FromArgb(255, 100, 210, 255));

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnSelectionChanged?.Invoke();
            }
        }
    }
}
