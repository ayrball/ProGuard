using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.UI.Controls;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

public partial class ProcessListViewModel : ViewModelBase, IDisposable
{
    /// <summary>One row of the filtered (and, in tree view, ordered) list, before it reaches the UI collection.</summary>
    private readonly record struct ViewRow(ProcessDisplayItem Item, string DisplayName, int TreeLevel);

    /// <summary>The filtered list plus the filter version it was computed for, so a stale one can be discarded.</summary>
    private sealed record ViewSnapshot(List<ViewRow> Rows, int Version);

    /// <summary>Lets the whole list be swapped with a single change notification instead of one per row.</summary>
    private sealed class BulkCollection : ObservableCollection<ProcessDisplayItem>
    {
        public void ReplaceAll(IEnumerable<ProcessDisplayItem> items)
        {
            CheckReentrancy();
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    private readonly List<ProcessDisplayItem> _allProcesses = new();
    private readonly CpuUsageTracker _cpuTracker = new();
    private readonly object _pollLock = new();
    private HashSet<int> _lastPids = new();
    private readonly System.Timers.Timer _timer;
    private int _isPolling;
    private bool _disposed;
    private bool _isMockMode;

    // Filtering happens off the UI thread; only the newest finished list is handed over, and at most one
    // hand-over is ever queued, so a slow UI cannot build up a backlog of updates.
    private readonly object _applyLock = new();
    private readonly object _viewLock = new();
    private ViewSnapshot? _pendingView;
    private bool _applyQueued;
    private int _filterVersion;
    private int _rebuildRequested;

    [ObservableProperty]
    private bool isActive = true;

    partial void OnIsActiveChanged(bool value)
    {
        if (value && !_isMockMode)
        {
            _ = Task.Run(PollProcessesAsync);
        }
    }

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool hasSearchText;

    [ObservableProperty]
    private int selectedFilterIndex = 0;

    [ObservableProperty]
    private bool isTreeView;

    [ObservableProperty]
    private string statusText = "Processes: 0 | Selected: 0";

    [ObservableProperty]
    private ProcessDisplayItem? selectedProcess;

    [ObservableProperty]
    private bool hasSelectedProcess;

    public ObservableCollection<ProcessDisplayItem> Processes { get; set; } = new BulkCollection();

    public ProcessListViewModel()
    {
        _ = Task.Run(PollProcessesAsync);

        _timer = new System.Timers.Timer(1500);
        _timer.Elapsed += (s, e) =>
        {
            if (!_isMockMode && IsActive)
            {
                _ = Task.Run(PollProcessesAsync);
            }
        };
        _timer.Start();
    }

    partial void OnSearchTextChanged(string value)
    {
        HasSearchText = !string.IsNullOrWhiteSpace(value);
        RequestRebuild();
    }

    partial void OnSelectedFilterIndexChanged(int value) => RequestRebuild();
    partial void OnIsTreeViewChanged(bool value) => RequestRebuild();
    partial void OnSelectedProcessChanged(ProcessDisplayItem? value)
    {
        HasSelectedProcess = value != null;
        if (value != null && string.IsNullOrEmpty(value.FilePath))
        {
            // The poll normally supplies the path; this covers a row selected before its first refresh.
            value.FilePath = ProcessProbe.TryGetImagePath(value.Pid);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText = $"Processes: {Processes.Count} | Selected: {(SelectedProcess != null ? SelectedProcess.Name : "None")}";
    }

    // The filter, search text or view mode changed: the whole list has to be re-ordered, not just patched.
    private void RequestRebuild()
    {
        Interlocked.Increment(ref _filterVersion);
        Interlocked.Exchange(ref _rebuildRequested, 1);
        ApplyFilter();
    }

    public void ApplyFilter()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            // Already on the UI thread (or there is no UI at all, as in unit tests): do it right here.
            lock (_applyLock)
            {
                _pendingView = null;
            }
            ApplyView(BuildView());
            return;
        }

        var view = BuildView();
        lock (_applyLock)
        {
            _pendingView = view;
            if (_applyQueued) return;
            _applyQueued = true;
        }

        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyPendingView));
        }
        catch
        {
            lock (_applyLock)
            {
                _applyQueued = false;
            }
        }
    }

    private void ApplyPendingView()
    {
        ViewSnapshot? view;
        lock (_applyLock)
        {
            view = _pendingView;
            _pendingView = null;
            _applyQueued = false;
        }

        if (view == null) return;

        // The filter changed while this list was being built: it is out of date, so build it again.
        if (view.Version != Volatile.Read(ref _filterVersion))
        {
            view = BuildView();
        }

        ApplyView(view);
    }

    // Applies the current filter, search text and tree/flat mode to the latest poll results. Safe on any
    // thread; it does not touch anything the UI is bound to.
    private ViewSnapshot BuildView()
    {
        int version = Volatile.Read(ref _filterVersion);
        int filterIndex = SelectedFilterIndex;
        string search = SearchText;
        bool tree = IsTreeView;

        List<ProcessDisplayItem> source;
        lock (_allProcesses)
        {
            source = _allProcesses.ToList();
        }

        var query = source.AsEnumerable();

        if (filterIndex == 1)
        {
            query = query.Where(p => !p.IsSystem);
        }
        else if (filterIndex == 2)
        {
            query = query.Where(p => p.IsSystem);
        }
        else if (filterIndex == 3)
        {
            query = query.Where(p => p.CpuPercent != "0.0%");
        }
        else if (filterIndex == 4)
        {
            query = query.Where(p => p.WorkingSetBytes > 500L * 1024L * 1024L);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Pid.ToString().Contains(search));
        }

        var filtered = query.ToList();
        var rows = new List<ViewRow>(filtered.Count);

        if (tree)
        {
            var pids = filtered.Select(p => p.Pid).ToHashSet();
            var childrenMap = filtered.GroupBy(p => p.ParentPid).ToDictionary(g => g.Key, g => g.ToList());
            var rootItems = filtered.Where(p => !pids.Contains(p.ParentPid)).ToList();

            void Traverse(ProcessDisplayItem node, int depth)
            {
                string displayName = depth > 0 ? $"{new string(' ', depth * 4)}↳ {node.Name}" : node.Name;
                rows.Add(new ViewRow(node, displayName, depth));

                if (childrenMap.TryGetValue(node.Pid, out var children))
                {
                    foreach (var child in children)
                    {
                        Traverse(child, depth + 1);
                    }
                }
            }

            foreach (var root in rootItems)
            {
                Traverse(root, 0);
            }
        }
        else
        {
            foreach (var p in filtered)
            {
                rows.Add(new ViewRow(p, p.Name, 0));
            }
        }

        return new ViewSnapshot(rows, version);
    }

    // UI thread only. Brings the bound collection in line with the list, touching as little as possible.
    private void ApplyView(ViewSnapshot view)
    {
        lock (_viewLock)
        {
            var rows = view.Rows;
            var currentSelectedPid = SelectedProcess?.Pid;
            bool rebuild = Interlocked.Exchange(ref _rebuildRequested, 0) == 1 || _isMockMode || Processes.Count == 0;

            Dictionary<int, ProcessDisplayItem>? existingByPid = null;
            if (!rebuild)
            {
                existingByPid = new Dictionary<int, ProcessDisplayItem>(Processes.Count);
                foreach (var p in Processes)
                {
                    existingByPid[p.Pid] = p;
                }

                // Rows are patched in place, which keeps them where they are. A tree whose shape changed
                // has to be laid out again.
                if (IsTreeView && (rows.Count != Processes.Count || rows.Any(r => !existingByPid.ContainsKey(r.Item.Pid))))
                {
                    rebuild = true;
                }
            }

            if (rebuild)
            {
                foreach (var row in rows)
                {
                    row.Item.DisplayName = row.DisplayName;
                    row.Item.TreeLevel = row.TreeLevel;
                }

                if (Processes is BulkCollection bulk)
                {
                    bulk.ReplaceAll(rows.Select(r => r.Item));
                }
                else
                {
                    Processes.Clear();
                    foreach (var row in rows)
                    {
                        Processes.Add(row.Item);
                    }
                }
            }
            else
            {
                var filteredPids = new HashSet<int>();
                foreach (var row in rows)
                {
                    filteredPids.Add(row.Item.Pid);
                }

                for (int i = Processes.Count - 1; i >= 0; i--)
                {
                    if (!filteredPids.Contains(Processes[i].Pid))
                    {
                        Processes.RemoveAt(i);
                    }
                }

                int insertIndex = 0;
                foreach (var row in rows)
                {
                    var p = row.Item;
                    if (existingByPid!.TryGetValue(p.Pid, out var existing))
                    {
                        if (existing.DisplayName != row.DisplayName) existing.DisplayName = row.DisplayName;
                        if (existing.TreeLevel != row.TreeLevel) existing.TreeLevel = row.TreeLevel;

                        if (!ReferenceEquals(existing, p))
                        {
                            if (existing.ParentPid != p.ParentPid) existing.ParentPid = p.ParentPid;
                            if (existing.CpuPercent != p.CpuPercent) existing.CpuPercent = p.CpuPercent;
                            if (existing.MemoryText != p.MemoryText) existing.MemoryText = p.MemoryText;
                            if (existing.WorkingSetBytes != p.WorkingSetBytes) existing.WorkingSetBytes = p.WorkingSetBytes;
                            if (existing.Priority != p.Priority) existing.Priority = p.Priority;
                            if (existing.AffinityText != p.AffinityText) existing.AffinityText = p.AffinityText;
                            if (existing.RawAffinityMask != p.RawAffinityMask) existing.RawAffinityMask = p.RawAffinityMask;
                            if (existing.Status != p.Status) existing.Status = p.Status;
                            if (existing.ThreadCount != p.ThreadCount) existing.ThreadCount = p.ThreadCount;
                            if (existing.HandleCount != p.HandleCount) existing.HandleCount = p.HandleCount;
                            if (existing.StartTimeText != p.StartTimeText) existing.StartTimeText = p.StartTimeText;
                            if (existing.FilePath != p.FilePath) existing.FilePath = p.FilePath;
                            if (existing.Icon != p.Icon) existing.Icon = p.Icon;
                        }
                    }
                    else
                    {
                        p.DisplayName = row.DisplayName;
                        p.TreeLevel = row.TreeLevel;
                        Processes.Insert(Math.Min(insertIndex, Processes.Count), p);
                    }
                    insertIndex++;
                }
            }

            if (currentSelectedPid.HasValue)
            {
                var current = Processes.FirstOrDefault(p => p.Pid == currentSelectedPid.Value);
                if (!ReferenceEquals(SelectedProcess, current))
                {
                    SelectedProcess = current;
                }
            }

            UpdateStatus();
        }
    }

    public void PollProcesses() => PollProcessesInternal();

    private Task PollProcessesAsync()
    {
        if (_isMockMode) return Task.CompletedTask;
        if (Interlocked.Exchange(ref _isPolling, 1) == 1) return Task.CompletedTask;

        try
        {
            PollProcessesInternal();
        }
        finally
        {
            Interlocked.Exchange(ref _isPolling, 0);
        }

        return Task.CompletedTask;
    }

    private void PollProcessesInternal()
    {
        lock (_pollLock)
        {
            if (_isMockMode) return;
            try
            {
                var rawProcesses = Process.GetProcesses();
                var updatedList = new List<ProcessDisplayItem>(rawProcesses.Length);
                var alivePids = new HashSet<int>(rawProcesses.Length);
                long allCoresMask = (1L << Math.Min(Environment.ProcessorCount, 64)) - 1;

                foreach (var p in rawProcesses)
                {
                    try
                    {
                        string name = p.ProcessName + ".exe";
                        int pid = p.Id;
                        alivePids.Add(pid);

                        // One limited handle yields times, priority, affinity, parent and path without the
                        // access-denied exceptions Process raises for most system and service processes.
                        double cpu = 0;
                        string priority = "Normal";
                        long affinityMask = allCoresMask;
                        int parentPid = 0;
                        string startText = "--";
                        string? imagePath = null;
                        if (ProcessProbe.TryQuery(pid, includeImagePath: true, out var probe))
                        {
                            cpu = _cpuTracker.GetCpuUsage(pid, probe.CpuTime);
                            if (probe.Priority.HasValue) priority = probe.Priority.Value.ToString();
                            if (probe.AffinityMask.HasValue) affinityMask = probe.AffinityMask.Value;
                            parentPid = probe.ParentPid;
                            imagePath = probe.ImagePath;
                            if (probe.StartTimeUtc != DateTime.MinValue) startText = probe.StartTimeUtc.ToLocalTime().ToString("HH:mm:ss");
                        }

                        long ws = 0;
                        try { ws = p.WorkingSet64; } catch { }

                        updatedList.Add(new ProcessDisplayItem
                        {
                            Name = name,
                            DisplayName = name,
                            Pid = pid,
                            ParentPid = parentPid,
                            CpuPercent = $"{cpu:0.0}%",
                            MemoryText = ProcessExtensions.ToReadableSize(ws),
                            WorkingSetBytes = ws,
                            Priority = priority,
                            AffinityText = FormatAffinityText(affinityMask),
                            RawAffinityMask = affinityMask,
                            Status = "Running",
                            IsSystem = ProcessExtensions.IsSystemProcess(p) || p.SessionId == 0,
                            ThreadCount = p.SafeGetThreadCount(),
                            HandleCount = p.SafeGetHandleCount(),
                            StartTimeText = startText,
                            FilePath = imagePath,
                            Icon = ProcessIconCache.GetIconDeferred(imagePath)
                        });
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }

                foreach (var gone in _lastPids.Where(pid => !alivePids.Contains(pid)))
                {
                    _cpuTracker.RemoveProcess(gone);
                }
                _lastPids = alivePids;

                lock (_allProcesses)
                {
                    // Mock data may have been loaded while this poll was running; it must not be overwritten.
                    if (_isMockMode) return;

                    _allProcesses.Clear();
                    _allProcesses.AddRange(updatedList.OrderByDescending(p => p.CpuPercent != "0.0%").ThenBy(p => p.Name));
                }

                ApplyFilter();
            }
            catch { }
        }
    }

    public void AddProcess(ProcessDisplayItem item)
    {
        if (string.IsNullOrEmpty(item.DisplayName)) item.DisplayName = item.Name;
        lock (_allProcesses)
        {
            _allProcesses.Add(item);
        }
        ApplyFilter();
    }

    public void ClearProcesses()
    {
        _isMockMode = true;
        lock (_allProcesses)
        {
            _allProcesses.Clear();
        }
        Processes.Clear();
        UpdateStatus();
    }

    [RelayCommand]
    private void Refresh()
    {
        _isMockMode = false;
        _ = Task.Run(PollProcessesAsync);
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    private void TerminateProcess()
    {
        if (SelectedProcess != null)
        {
            try
            {
                using var p = Process.GetProcessById(SelectedProcess.Pid);
                p.Kill();
            }
            catch { }
            lock (_allProcesses)
            {
                _allProcesses.RemoveAll(p => p.Pid == SelectedProcess.Pid);
            }
            ApplyFilter();
        }
    }

    [RelayCommand]
    private void KillProcessTree()
    {
        if (SelectedProcess == null) return;
        var result = MessageBox.Show($"Are you sure you want to terminate '{SelectedProcess.Name}' (PID {SelectedProcess.Pid}) and all its child processes?", "Kill Process Tree", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe", $"/PID {SelectedProcess.Pid} /T /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi)?.WaitForExit(3000);

                lock (_allProcesses)
                {
                    _allProcesses.RemoveAll(p => p.Pid == SelectedProcess.Pid || p.ParentPid == SelectedProcess.Pid);
                }
                ApplyFilter();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error terminating process tree: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private void ToggleEfficiencyMode()
    {
        if (SelectedProcess == null) return;
        try
        {
            var h = Kernel32.OpenProcess(ProcessAccessFlags.SetInformation | ProcessAccessFlags.QueryInformation, false, SelectedProcess.Pid);
            if (h != IntPtr.Zero)
            {
                bool enable = !SelectedProcess.IsEfficiencyMode;
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = enable ? PROCESS_POWER_THROTTLING_STATE.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
                };

                int sz = Marshal.SizeOf(state);
                IntPtr ptr = Marshal.AllocHGlobal(sz);
                Marshal.StructureToPtr(state, ptr, false);

                bool success = Kernel32.SetProcessInformation(h, Kernel32.ProcessPowerThrottling, ptr, (uint)sz);
                Marshal.FreeHGlobal(ptr);
                Kernel32.CloseHandle(h);

                if (success)
                {
                    SelectedProcess.IsEfficiencyMode = enable;
                    SelectedProcess.Status = enable ? "🍃 Eco" : "Running";
                    ApplyFilter();
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Efficiency Mode not supported or access denied: {ex.Message}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private void SetIoPriorityVeryLow() => SetSelectedIoPriority("VeryLow");
    [RelayCommand]
    private void SetIoPriorityLow() => SetSelectedIoPriority("Low");
    [RelayCommand]
    private void SetIoPriorityNormal() => SetSelectedIoPriority("Normal");
    [RelayCommand]
    private void SetIoPriorityHigh() => SetSelectedIoPriority("High");

    private void SetSelectedIoPriority(string level)
    {
        if (SelectedProcess == null) return;
        try
        {
            int prioVal = level switch
            {
                "VeryLow" => 0,
                "Low" => 1,
                "Normal" => 2,
                "High" => 3,
                _ => 2
            };

            var h = Kernel32.OpenProcess(ProcessAccessFlags.SetInformation, false, SelectedProcess.Pid);
            if (h != IntPtr.Zero)
            {
                int status = Ntdll.NtSetInformationProcess(h, Ntdll.ProcessIoPriority, ref prioVal, sizeof(int));
                Kernel32.CloseHandle(h);
                if (status == 0)
                {
                    SelectedProcess.IoPriority = level;
                    ApplyFilter();
                }
            }
        }
        catch { }
    }

    [RelayCommand]
    private void SetMemoryPriority1() => SetSelectedMemoryPriority(1);
    [RelayCommand]
    private void SetMemoryPriority2() => SetSelectedMemoryPriority(2);
    [RelayCommand]
    private void SetMemoryPriority3() => SetSelectedMemoryPriority(3);
    [RelayCommand]
    private void SetMemoryPriority4() => SetSelectedMemoryPriority(4);
    [RelayCommand]
    private void SetMemoryPriority5() => SetSelectedMemoryPriority(5);

    private void SetSelectedMemoryPriority(int level)
    {
        if (SelectedProcess == null) return;
        try
        {
            var h = Kernel32.OpenProcess(ProcessAccessFlags.SetInformation, false, SelectedProcess.Pid);
            if (h != IntPtr.Zero)
            {
                var memPrio = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = (uint)Math.Clamp(level, 1, 5) };
                int sz = Marshal.SizeOf(memPrio);
                IntPtr ptr = Marshal.AllocHGlobal(sz);
                Marshal.StructureToPtr(memPrio, ptr, false);

                Kernel32.SetProcessInformation(h, Kernel32.ProcessMemoryPriority, ptr, (uint)sz);
                Marshal.FreeHGlobal(ptr);
                Kernel32.CloseHandle(h);

                SelectedProcess.MemoryPriority = level;
                ApplyFilter();
            }
        }
        catch { }
    }

    [RelayCommand]
    private void SetCpuLimit()
    {
        if (SelectedProcess == null) return;
        var dialog = new CpuLimitDialog(SelectedProcess.Name, SelectedProcess.Pid, SelectedProcess.CpuLimitPercent)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            if (dialog.RemoveLimitRequested)
            {
                SelectedProcess.CpuLimitPercent = null;
                try
                {
                    App.GetService<ICpuLimiter>()?.RemoveLimit(SelectedProcess.Pid);
                }
                catch { }
            }
            else
            {
                int limit = dialog.SelectedLimitPercent;
                SelectedProcess.CpuLimitPercent = limit;
                try
                {
                    App.GetService<ICpuLimiter>()?.AddLimit(SelectedProcess.Pid, limit);
                }
                catch { }
            }
            ApplyFilter();
        }
    }

    [RelayCommand]
    private void TrimWorkingSet()
    {
        if (SelectedProcess == null) return;
        try
        {
            using var p = Process.GetProcessById(SelectedProcess.Pid);
            long before = p.WorkingSet64;
            Kernel32.EmptyWorkingSet(p.Handle);
            p.Refresh();
            long after = p.WorkingSet64;
            long reclaimed = Math.Max(0, before - after);

            SelectedProcess.MemoryText = ProcessExtensions.ToReadableSize(after);
            SelectedProcess.WorkingSetBytes = after;
            MessageBox.Show($"SmartTrim trimmed {ProcessExtensions.ToReadableSize(reclaimed)} from '{SelectedProcess.Name}'!", "SmartTrim", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not trim working set: {ex.Message}", "SmartTrim", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void TrimAllWorkingSets()
    {
        try
        {
            long reclaimedTotal = 0;
            var rawProcesses = Process.GetProcesses();
            foreach (var p in rawProcesses)
            {
                try
                {
                    long before = p.WorkingSet64;
                    if (Kernel32.EmptyWorkingSet(p.Handle))
                    {
                        p.Refresh();
                        long after = p.WorkingSet64;
                        reclaimedTotal += Math.Max(0, before - after);
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            MessageBox.Show($"SmartTrim successfully flushed system working sets!\nReclaimed approximately {ProcessExtensions.ToReadableSize(reclaimedTotal)} RAM.", "SmartTrim Global Flush", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = Task.Run(PollProcessesAsync);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error running SmartTrim: {ex.Message}", "SmartTrim", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void SetPriorityRealtime() => SetSelectedPriority(ProcessPriorityClass.RealTime);
    [RelayCommand]
    private void SetPriorityHigh() => SetSelectedPriority(ProcessPriorityClass.High);
    [RelayCommand]
    private void SetPriorityAboveNormal() => SetSelectedPriority(ProcessPriorityClass.AboveNormal);
    [RelayCommand]
    private void SetPriorityNormal() => SetSelectedPriority(ProcessPriorityClass.Normal);
    [RelayCommand]
    private void SetPriorityBelowNormal() => SetSelectedPriority(ProcessPriorityClass.BelowNormal);
    [RelayCommand]
    private void SetPriorityIdle() => SetSelectedPriority(ProcessPriorityClass.Idle);

    private void SetSelectedPriority(ProcessPriorityClass priority)
    {
        if (SelectedProcess != null)
        {
            try
            {
                using var p = Process.GetProcessById(SelectedProcess.Pid);
                p.PriorityClass = priority;
                SelectedProcess.Priority = priority.ToString();
            }
            catch
            {
                try
                {
                    var h = Kernel32.OpenProcess(ProcessAccessFlags.SetInformation, false, SelectedProcess.Pid);
                    if (h != IntPtr.Zero)
                    {
                        Kernel32.SetPriorityClass(h, (uint)priority);
                        Kernel32.CloseHandle(h);
                        SelectedProcess.Priority = priority.ToString();
                    }
                }
                catch { }
            }
            ApplyFilter();
        }
    }

    [RelayCommand]
    private void SetAffinity()
    {
        if (SelectedProcess == null) return;

        try
        {
            long currentMask = SelectedProcess.RawAffinityMask;
            var dialog = new AffinityPickerDialog(currentMask, SelectedProcess.Name)
            {
                Owner = Application.Current?.MainWindow
            };

            if (dialog.ShowDialog() == true)
            {
                ApplyAffinityMask(dialog.SelectedMask);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to configure CPU affinity: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void DisableSmt()
    {
        if (SelectedProcess == null) return;
        var topology = SystemInfo.GetCpuTopology();
        ApplyAffinityMask(topology.PhysicalCoresMask);
    }

    [RelayCommand]
    private void EnableSmt()
    {
        if (SelectedProcess == null) return;
        var topology = SystemInfo.GetCpuTopology();
        ApplyAffinityMask(topology.AllCoresMask);
    }

    public void ApplyAffinityMask(long newMask)
    {
        if (SelectedProcess == null) return;

        try
        {
            var h = Kernel32.OpenProcess(ProcessAccessFlags.SetInformation | ProcessAccessFlags.QueryInformation, false, SelectedProcess.Pid);
            if (h != IntPtr.Zero)
            {
                Kernel32.SetProcessAffinityMask(h, new IntPtr(newMask));
                Kernel32.CloseHandle(h);
                SelectedProcess.RawAffinityMask = newMask;
                SelectedProcess.AffinityText = FormatAffinityText(newMask);
                ApplyFilter();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to set CPU affinity: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public static string FormatAffinityText(long affinityMask)
    {
        var topology = SystemInfo.GetCpuTopology();
        if (affinityMask == topology.AllCoresMask || affinityMask == -1L || affinityMask == 0)
        {
            return "All Cores";
        }
        if (affinityMask == topology.PhysicalCoresMask)
        {
            return "Physical (No SMT)";
        }
        if (affinityMask == topology.SmtSiblingsMask)
        {
            return "SMT Siblings";
        }
        return $"0x{affinityMask:X}";
    }

    [RelayCommand]
    private void SuspendProcess()
    {
        if (SelectedProcess == null) return;
        try
        {
            var h = Kernel32.OpenProcess(ProcessAccessFlags.SuspendResume, false, SelectedProcess.Pid);
            if (h != IntPtr.Zero)
            {
                Ntdll.NtSuspendProcess(h);
                Kernel32.CloseHandle(h);
                SelectedProcess.Status = "Suspended";
                ApplyFilter();
            }
        }
        catch { }
    }

    [RelayCommand]
    private void ResumeProcess()
    {
        if (SelectedProcess == null) return;
        try
        {
            var h = Kernel32.OpenProcess(ProcessAccessFlags.SuspendResume, false, SelectedProcess.Pid);
            if (h != IntPtr.Zero)
            {
                Ntdll.NtResumeProcess(h);
                Kernel32.CloseHandle(h);
                SelectedProcess.Status = "Running";
                ApplyFilter();
            }
        }
        catch { }
    }

    [RelayCommand]
    private void OpenFileLocation()
    {
        if (SelectedProcess == null) return;
        try
        {
            string? path = SelectedProcess.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                path = ProcessProbe.TryGetImagePath(SelectedProcess.Pid);
                SelectedProcess.FilePath = path;
            }

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show("Could not determine process executable path.", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error opening file location: {ex.Message}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void CreateRule()
    {
        if (SelectedProcess == null) return;
        try
        {
            using var db = new ProGuardDbContext();
            db.Database.EnsureCreated();

            int? priorityVal = null;
            if (Enum.TryParse<ProcessPriorityClass>(SelectedProcess.Priority, out var pc))
            {
                priorityVal = (int)pc;
            }

            var rule = new ProcessRule
            {
                ProcessName = SelectedProcess.Name,
                PriorityClass = priorityVal,
                AffinityMask = SelectedProcess.RawAffinityMask > 0 ? SelectedProcess.RawAffinityMask : null,
                CpuLimitPercent = SelectedProcess.CpuLimitPercent,
                DisallowExecution = false
            };

            db.ProcessRules.Add(rule);
            db.SaveChanges();

            MessageBox.Show($"Persistent rule created for '{SelectedProcess.Name}'!", "ProGuard Rule Created", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save rule: {ex.Message}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void DisallowProcess()
    {
        if (SelectedProcess == null) return;
        try
        {
            using var db = new ProGuardDbContext();
            db.Database.EnsureCreated();

            var rule = new ProcessRule
            {
                ProcessName = SelectedProcess.Name,
                DisallowExecution = true
            };

            db.ProcessRules.Add(rule);
            db.SaveChanges();

            TerminateProcess();
            MessageBox.Show($"Process '{SelectedProcess.Name}' disallowed and terminated!", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to disallow process: {ex.Message}", "ProGuard", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void LoadMockData()
    {
        _isMockMode = true;
        ClearProcesses();
        AddProcess(new ProcessDisplayItem { Name = "svchost.exe", Pid = 1204, CpuPercent = "0.1%", MemoryText = "12 MB", Priority = "Normal", AffinityText = "All Cores", Status = "Running", IsSystem = true });
        AddProcess(new ProcessDisplayItem { Name = "chrome.exe", Pid = 8452, CpuPercent = "5.4%", MemoryText = "512 MB", Priority = "Normal", AffinityText = "All Cores", Status = "Running" });
        AddProcess(new ProcessDisplayItem { Name = "devenv.exe", Pid = 9124, CpuPercent = "12.0%", MemoryText = "1.2 GB", Priority = "Normal", AffinityText = "All Cores", Status = "Running" });
        AddProcess(new ProcessDisplayItem { Name = "explorer.exe", Pid = 3456, CpuPercent = "0.5%", MemoryText = "128 MB", Priority = "Normal", AffinityText = "All Cores", Status = "Running" });
        AddProcess(new ProcessDisplayItem { Name = "Discord.exe", Pid = 1120, CpuPercent = "1.1%", MemoryText = "340 MB", Priority = "Normal", AffinityText = "All Cores", Status = "Running" });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _timer?.Stop();
            _timer?.Dispose();
        }
    }
}

public partial class ProcessDisplayItem : ObservableObject
{
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string displayName = string.Empty;
    [ObservableProperty] private int pid;
    [ObservableProperty] private int parentPid;
    [ObservableProperty] private string cpuPercent = string.Empty;
    [ObservableProperty] private string memoryText = string.Empty;
    [ObservableProperty] private long workingSetBytes;
    [ObservableProperty] private string priority = string.Empty;
    [ObservableProperty] private string affinityText = string.Empty;
    [ObservableProperty] private long rawAffinityMask = -1L;
    [ObservableProperty] private string status = "Running";
    [ObservableProperty] private bool isSystem;
    [ObservableProperty] private string? filePath;
    [ObservableProperty] private ImageSource? icon;
    [ObservableProperty] private bool isEfficiencyMode;
    [ObservableProperty] private string ioPriority = "Normal";
    [ObservableProperty] private int memoryPriority = 5;
    [ObservableProperty] private int threadCount = 1;
    [ObservableProperty] private int handleCount;
    [ObservableProperty] private string startTimeText = string.Empty;
    [ObservableProperty] private int treeLevel;
    [ObservableProperty] private int? cpuLimitPercent;
}
