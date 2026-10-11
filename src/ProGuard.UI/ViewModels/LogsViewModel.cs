using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Core.Data;
using ProGuard.Core.Models;

namespace ProGuard.UI.ViewModels;

public partial class LogsViewModel : ViewModelBase
{
    private readonly List<LogEntryItem> _allLogs = new();

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private int selectedTypeIndex = 0;

    public ObservableCollection<LogEntryItem> LogEntries { get; set; } = new();

    public LogsViewModel()
    {
        LoadLogs();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedTypeIndexChanged(int value) => ApplyFilter();

    public void LoadLogs()
    {
        _allLogs.Clear();
        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();

            var actions = db.ProBalanceActions.OrderByDescending(a => a.Timestamp).Take(200).ToList();
            foreach (var a in actions)
            {
                if (a.Pid == 0 && a.ProcessName == ProBalanceAction.PowerPlanEntryName)
                {
                    _allLogs.Add(new LogEntryItem
                    {
                        Timestamp = a.Timestamp.ToString("g"),
                        Type = "System",
                        ProcessName = a.ProcessName,
                        Message = a.Reason
                    });
                    continue;
                }

                // CPU Limiter (affinity reduction) entries leave both priorities at 0 and put the masks in Reason.
                bool isCpuLimiter = a.OriginalPriority == 0 && a.NewPriority == 0;
                _allLogs.Add(new LogEntryItem
                {
                    Timestamp = a.Timestamp.ToString("g"),
                    Type = "ProBalance",
                    ProcessName = a.ProcessName,
                    Message = isCpuLimiter
                        ? $"{a.Reason} (PID: {a.Pid})"
                        : $"{a.Reason} (PID: {a.Pid}, Priority: {a.OriginalPriority} -> {a.NewPriority})"
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load logs: {ex}");
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = _allLogs.AsEnumerable();

        if (SelectedTypeIndex == 1)
            query = query.Where(l => l.Type == "ProBalance");
        else if (SelectedTypeIndex == 2)
            query = query.Where(l => l.Type == "Watchdog");
        else if (SelectedTypeIndex == 3)
            query = query.Where(l => l.Type == "System");

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(l => l.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                                     l.ProcessName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        LogEntries.Clear();
        foreach (var item in query)
        {
            LogEntries.Add(item);
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        try
        {
            using var db = new ProGuardDbContext();
            db.EnsureSchema();
            db.ProBalanceActions.RemoveRange(db.ProBalanceActions);
            db.SaveChanges();
        }
        catch { }

        _allLogs.Clear();
        LogEntries.Clear();
    }

    [RelayCommand]
    private void ExportLogs()
    {
        try
        {
            string exportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ProGuard_Logs.txt");
            var lines = _allLogs.Select(l => $"[{l.Timestamp}] [{l.Type}] [{l.ProcessName}] {l.Message}");
            File.WriteAllLines(exportPath, lines);
            MessageBox.Show($"Logs successfully exported to:\n{exportPath}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export logs: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public class LogEntryItem
{
    public string Timestamp { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
