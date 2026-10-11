using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

public enum ProcessFilter { All, Apps, System, Active, Heavy }
public enum ProcessSort { Cpu, Memory, Name, Pid }

/// <summary>One process as the table shows it. Updated in place every refresh; only changed values notify.</summary>
public sealed partial class ProcessRow : ObservableObject
{
    public int Pid { get; }
    public string Name { get; }
    public string Monogram { get; }

    [ObservableProperty] private string? path;
    [ObservableProperty] private ImageSource? icon;
    [ObservableProperty] private double cpuPercent;
    [ObservableProperty] private string cpuText = "0.0";
    [ObservableProperty] private long memoryBytes;
    [ObservableProperty] private string memoryText = string.Empty;
    [ObservableProperty] private ProcessPriorityClass? priority;
    [ObservableProperty] private string priorityText = "—";
    [ObservableProperty] private long affinityMask;
    [ObservableProperty] private string affinityText = "—";
    [ObservableProperty] private bool isSystem;
    [ObservableProperty] private bool isForeground;
    [ObservableProperty] private bool isSuspended;
    [ObservableProperty] private bool isEfficiencyMode;
    [ObservableProperty] private bool isRestrained;
    [ObservableProperty] private bool hasRule;
    [ObservableProperty] private double? cpuLimitPercent;
    [ObservableProperty] private string limitText = string.Empty;
    [ObservableProperty] private int parentPid;
    [ObservableProperty] private string startedText = "—";
    [ObservableProperty] private bool isAffinityLimited;
    [ObservableProperty] private int? ioPriority;
    [ObservableProperty] private string ioPriorityText = "—";
    [ObservableProperty] private int? memoryPriority;
    [ObservableProperty] private string memoryPriorityText = "—";
    [ObservableProperty] private string userText = "—";
    [ObservableProperty] private int sessionId;
    [ObservableProperty] private long privateBytes;
    [ObservableProperty] private string privateText = "—";
    [ObservableProperty] private int threadCount;

    public ProcessRow(int pid, string name)
    {
        Pid = pid;
        Name = Fmt.Exe(name);
        Monogram = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
    }

    public void Update(ProcessSnapshot s, int logical)
    {
        if (Path != s.FilePath) Path = s.FilePath;
        if (CpuPercent != s.CpuPercent)
        {
            CpuPercent = s.CpuPercent;
            var text = s.CpuPercent.ToString("0.0");
            if (CpuText != text) CpuText = text;
        }
        if (MemoryBytes != s.WorkingSetBytes)
        {
            MemoryBytes = s.WorkingSetBytes;
            var text = Fmt.Bytes(s.WorkingSetBytes);
            if (MemoryText != text) MemoryText = text;
        }
        if (Priority != s.Priority)
        {
            Priority = s.Priority;
            PriorityText = Fmt.Priority(s.Priority);
        }
        if (AffinityMask != s.AffinityMask)
        {
            AffinityMask = s.AffinityMask;
            AffinityText = Fmt.Affinity(s.AffinityMask, logical);
        }
        if (IsSystem != s.IsSystem) IsSystem = s.IsSystem;
        if (IsForeground != s.IsForeground) IsForeground = s.IsForeground;
        if (IsSuspended != s.IsSuspended) IsSuspended = s.IsSuspended;
        if (IsEfficiencyMode != s.IsEfficiencyMode) IsEfficiencyMode = s.IsEfficiencyMode;
        if (IsRestrained != s.IsRestrained) IsRestrained = s.IsRestrained;
        if (HasRule != s.HasRule) HasRule = s.HasRule;
        if (CpuLimitPercent != s.CpuLimitPercent)
        {
            CpuLimitPercent = s.CpuLimitPercent;
            LimitText = s.CpuLimitPercent is double l ? $"LIM {l:0.#}%" : string.Empty;
        }
        if (IsAffinityLimited != s.IsAffinityLimited) IsAffinityLimited = s.IsAffinityLimited;
        if (IoPriority != s.IoPriority)
        {
            IoPriority = s.IoPriority;
            IoPriorityText = Fmt.IoPriority(s.IoPriority);
        }
        if (MemoryPriority != s.MemoryPriority)
        {
            MemoryPriority = s.MemoryPriority;
            MemoryPriorityText = Fmt.MemoryPriority(s.MemoryPriority);
        }
        var user = string.IsNullOrEmpty(s.UserName) ? "—" : s.UserName;
        if (UserText != user) UserText = user;
        if (SessionId != s.SessionId) SessionId = s.SessionId;
        if (PrivateBytes != s.PrivateBytes)
        {
            PrivateBytes = s.PrivateBytes;
            PrivateText = Fmt.Bytes(s.PrivateBytes);
        }
        if (ThreadCount != s.ThreadCount) ThreadCount = s.ThreadCount;
        if (ParentPid != s.ParentPid) ParentPid = s.ParentPid;
        if (StartedText == "—" && s.StartTimeUtc != DateTime.MinValue) StartedText = s.StartTimeUtc.ToLocalTime().ToString("MMM d · HH:mm");
    }
}

/// <summary>
/// The process table. Rows are matched by PID and patched in place. Re-sorting is paused while the pointer
/// is over the table or a menu is open (<see cref="FreezeOrder"/>), so the row you are about to click
/// never moves out from under you.
/// </summary>
public sealed partial class ProcessesViewModel : ObservableObject, IPage
{
    private readonly EngineHub _hub;
    private readonly IconCache _icons;
    private readonly ShellViewModel _shell;
    private readonly Dictionary<int, ProcessRow> _byPid = new();
    private int _logical = Environment.ProcessorCount;


    public ObservableCollection<ProcessRow> Rows { get; } = new();

    [ObservableProperty] private ProcessRow? selected;
    [ObservableProperty] private string search = string.Empty;
    [ObservableProperty] private ProcessFilter filter = ProcessFilter.All;
    [ObservableProperty] private ProcessSort sort = ProcessSort.Cpu;
    [ObservableProperty] private bool freezeOrder;
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private string totalCpuText = "0";
    [ObservableProperty] private double cpuScale = 100;
    [ObservableProperty] private bool isAdministrator = true;

    public ProcessesViewModel(EngineHub hub, IconCache icons, ShellViewModel shell)
    {
        _hub = hub;
        _icons = icons;
        _shell = shell;
        hub.SnapshotApplied += Apply;
    }

    public int ShownCount => Rows.Count;

    public void OnShown() => _hub.RefreshNow();
    public void OnHidden() { }

    partial void OnSearchChanged(string value) => Reorder(force: true);
    partial void OnFilterChanged(ProcessFilter value) => Reorder(force: true);
    partial void OnSortChanged(ProcessSort value) => Reorder(force: true);
    partial void OnFreezeOrderChanged(bool value)
    {
        if (!value) Reorder(force: false);
    }

    [RelayCommand]
    private void SortBy(ProcessSort column) => Sort = column;

    [RelayCommand]
    private void ClearSearch() => Search = string.Empty;

    internal void Apply(EngineSnapshot snapshot)
    {
        _logical = Math.Max(1, snapshot.LogicalProcessors);
        IsAdministrator = snapshot.CallerIsAdministrator;
        // A full bar means eight logical processors flat out: per-process load is a small share of a big machine.
        CpuScale = Math.Min(100, 100.0 / _logical * 8);

        var alive = new HashSet<int>(snapshot.Processes.Count);
        double totalCpu = 0;
        foreach (var s in snapshot.Processes)
        {
            alive.Add(s.Pid);
            totalCpu += s.CpuPercent;
            if (!_byPid.TryGetValue(s.Pid, out var row))
            {
                row = new ProcessRow(s.Pid, s.Name);
                _byPid[s.Pid] = row;
            }
            row.Update(s, _logical);
            if (row.Icon == null && row.Path != null) RequestIcon(row);
        }

        foreach (var pid in _byPid.Keys.Where(pid => !alive.Contains(pid)).ToList()) _byPid.Remove(pid);

        TotalCount = _byPid.Count;
        TotalCpuText = totalCpu.ToString("0");
        Reorder(force: false);
    }

    private void RequestIcon(ProcessRow row)
    {
        var path = row.Path;
        if (_icons.TryGet(path, icon => { if (row.Path == path) row.Icon = icon; }, out var cached) && cached != null)
        {
            row.Icon = cached;
        }
    }

    /// <summary>Brings <see cref="Rows"/> in line with the filter and sort, moving as few rows as possible.</summary>
    private void Reorder(bool force)
    {
        var wanted = _byPid.Values.Where(Matches);
        wanted = Sort switch
        {
            ProcessSort.Memory => wanted.OrderByDescending(r => r.MemoryBytes).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            ProcessSort.Name => wanted.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Pid),
            ProcessSort.Pid => wanted.OrderBy(r => r.Pid),
            _ => wanted.OrderByDescending(r => r.CpuPercent).ThenByDescending(r => r.MemoryBytes).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        };
        var target = wanted.ToList();

        var keep = new HashSet<ProcessRow>(target);
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);
        }

        if (FreezeOrder && !force)
        {
            // Hold every existing row where it is; newcomers go to the bottom until the pointer leaves.
            var present = new HashSet<ProcessRow>(Rows);
            foreach (var row in target)
            {
                if (!present.Contains(row)) Rows.Add(row);
            }
        }
        else
        {
            for (int i = 0; i < target.Count; i++)
            {
                var row = target[i];
                if (i < Rows.Count && ReferenceEquals(Rows[i], row)) continue;

                int at = -1;
                for (int j = i + 1; j < Rows.Count; j++)
                {
                    if (ReferenceEquals(Rows[j], row)) { at = j; break; }
                }

                if (at < 0) Rows.Insert(i, row);
                else Rows.Move(at, i);
            }
        }

        OnPropertyChanged(nameof(ShownCount));
    }

    private bool Matches(ProcessRow row)
    {
        bool passes = Filter switch
        {
            ProcessFilter.Apps => !row.IsSystem,
            ProcessFilter.System => row.IsSystem,
            ProcessFilter.Active => row.CpuPercent >= 0.1,
            ProcessFilter.Heavy => row.MemoryBytes >= 500L * 1024 * 1024,
            _ => true
        };
        if (!passes) return false;

        var query = Search.Trim();
        return query.Length == 0
            || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Pid.ToString().StartsWith(query, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ actions on the selected process

    [RelayCommand]
    private Task SetPriorityAsync(string level)
    {
        if (Selected is not { } row || !Enum.TryParse<ProcessPriorityClass>(level, out var priority)) return Task.CompletedTask;

        Task Run() => _hub.RunAsync(CommandTypes.SetPriority, row.Pid, priority, $"{row.Name} → {Fmt.Priority(priority)}");

        if (priority == ProcessPriorityClass.RealTime)
        {
            _shell.Confirm("REALTIME?", $"Realtime priority lets {row.Name} starve Windows itself, including your mouse and keyboard. Only use it for a process you trust to stay light.",
                "SET REALTIME", Run);
            return Task.CompletedTask;
        }
        return Run();
    }

    /// <summary>I/O priority hint 0-3 (very low .. high). High needs administrator rights; the engine would refuse it otherwise.</summary>
    [RelayCommand]
    private Task SetIoPriorityAsync(string level)
    {
        if (Selected is not { } row || !int.TryParse(level, out var hint) || hint is < 0 or > 3) return Task.CompletedTask;
        if (hint == 3 && !IsAdministrator)
        {
            _shell.Toaster.Show("I/O priority High needs administrator rights. Restart ProGuard as administrator to use it.", isError: true);
            return Task.CompletedTask;
        }
        return _hub.RunAsync(CommandTypes.SetIoPriority, row.Pid, hint, $"{row.Name} · I/O {Fmt.IoPriority(hint)}");
    }

    /// <summary>Memory (page) priority 1-5, very low .. normal.</summary>
    [RelayCommand]
    private Task SetMemoryPriorityAsync(string level)
    {
        if (Selected is not { } row || !int.TryParse(level, out var value) || value is < 1 or > 5) return Task.CompletedTask;
        return _hub.RunAsync(CommandTypes.SetMemoryPriority, row.Pid, value, $"{row.Name} · MEMORY {Fmt.MemoryPriority(value)}");
    }

    [RelayCommand]
    private Task ToggleEfficiencyAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        bool enable = !row.IsEfficiencyMode;
        return _hub.RunAsync(CommandTypes.SetEfficiencyMode, row.Pid, enable, $"{row.Name} · ECO {(enable ? "ON" : "OFF")}");
    }

    [RelayCommand]
    private Task ToggleSuspendAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        return row.IsSuspended
            ? _hub.RunAsync(CommandTypes.ResumeProcess, row.Pid, success: $"{row.Name} resumed")
            : _hub.RunAsync(CommandTypes.SuspendProcess, row.Pid, success: $"{row.Name} paused");
    }

    [RelayCommand]
    private async Task TrimAsync()
    {
        if (Selected is not { } row) return;
        var freed = await _hub.QueryAsync<long>(CommandTypes.TrimWorkingSet, row.Pid);
        _shell.Toaster.Show($"{row.Name} · freed {Fmt.Bytes(freed)}");
        _hub.RefreshNow();
    }

    [RelayCommand]
    private Task EndAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        Task Run() => _hub.RunAsync(CommandTypes.TerminateProcess, row.Pid, success: $"{row.Name} ended");

        if (row.IsSystem)
        {
            _shell.Confirm("END A SYSTEM PROCESS?", $"{row.Name} belongs to Windows or a service. Ending it can crash apps or the session.", "END IT", Run);
            return Task.CompletedTask;
        }
        return Run();
    }

    [RelayCommand]
    private void EndTree()
    {
        if (Selected is not { } row) return;
        int children = CountDescendants(row.Pid);
        _shell.Confirm("END THE WHOLE TREE?",
            children == 0 ? $"{row.Name} has no child processes; only it will end." : $"{row.Name} and {children} process{(children == 1 ? "" : "es")} it started will end.",
            "END TREE",
            () => _hub.RunAsync(CommandTypes.TerminateProcessTree, row.Pid, success: $"{row.Name} tree ended"));
    }

    [RelayCommand]
    private void EditAffinity()
    {
        if (Selected is { } row) _shell.ShowDialog(new AffinityDialogViewModel(_hub, _shell, row, _logical));
    }

    [RelayCommand]
    private void EditCpuLimit()
    {
        if (Selected is { } row) _shell.ShowDialog(new CpuLimitDialogViewModel(_hub, _shell, row));
    }

    [RelayCommand]
    private void MakeRule()
    {
        if (Selected is not { } row) return;
        _shell.Rules.StartNew(row.Name, row.Priority, row.CpuLimitPercent);
        _shell.GoTo(_shell.Rules);
    }

    [RelayCommand]
    private void OpenLocation()
    {
        if (Selected?.Path is { } path && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        else
        {
            _shell.Toaster.Show("Windows won't reveal where this process lives.", isError: true);
        }
    }

    private int CountDescendants(int pid)
    {
        var children = _byPid.Values.ToLookup(r => r.ParentPid);
        int count = 0;
        var stack = new Stack<int>();
        stack.Push(pid);
        var seen = new HashSet<int> { pid };
        while (stack.Count > 0)
        {
            foreach (var child in children[stack.Pop()])
            {
                if (seen.Add(child.Pid))
                {
                    count++;
                    stack.Push(child.Pid);
                }
            }
        }
        return count;
    }
}
