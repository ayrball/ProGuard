using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;
using ProGuard.UI.Resources;

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
            LimitText = s.CpuLimitPercent is double l ? string.Format(Strings.Proc_Limit, l) : string.Empty;
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

        Task Run() => SetAsync(row, CommandTypes.SetPriority, priority, string.Format(Strings.Proc_SetTo, row.Name, Fmt.Priority(priority)),
            string.Format(Strings.Proc_Priority, Fmt.Priority(priority)), r => r.PriorityClass = (int)priority);

        if (priority == ProcessPriorityClass.RealTime)
        {
            _shell.Confirm(Strings.Proc_RealtimeTitle, string.Format(Strings.Proc_RealtimeWarning, row.Name),
                Strings.Proc_SetRealtime, Run);
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
            _shell.Toaster.Show(Strings.Proc_IoHighNeedsAdmin, isError: true, offerElevation: true);
            return Task.CompletedTask;
        }
        return SetAsync(row, CommandTypes.SetIoPriority, hint, string.Format(Strings.Proc_IoSet, row.Name, Fmt.IoPriority(hint)),
            string.Format(Strings.Proc_Io, Fmt.IoPriority(hint)), r => r.IoPriority = hint);
    }

    /// <summary>Memory (page) priority 1-5, very low .. normal.</summary>
    [RelayCommand]
    private Task SetMemoryPriorityAsync(string level)
    {
        if (Selected is not { } row || !int.TryParse(level, out var value) || value is < 1 or > 5) return Task.CompletedTask;
        return SetAsync(row, CommandTypes.SetMemoryPriority, value, string.Format(Strings.Proc_MemorySet, row.Name, Fmt.MemoryPriority(value)),
            string.Format(Strings.Proc_Memory, Fmt.MemoryPriority(value)), r => r.MemoryPriority = value);
    }

    /// <summary>
    /// Changes the running process (priority, I/O or memory priority, efficiency mode, cores, CPU cap) and, while
    /// "Remember changes" is on, keeps it in the program's rule too (<see cref="RememberAsync"/>). Windows' own
    /// processes are left one-off: a rule names a program, and one for svchost.exe or the like would reach every copy.
    /// <paramref name="change"/> writes the new value into the rule, or clears it when the change undoes a setting
    /// (all cores again, the cap removed).
    /// </summary>
    internal async Task SetAsync(ProcessRow row, string command, object? argument, string done, string what, Action<ProcessRuleDto> change)
    {
        if (!_shell.RememberPriorityChanges || row.IsSystem)
        {
            await _hub.RunAsync(command, row.Pid, argument, done);
            return;
        }
        if (!await _hub.RunAsync(command, row.Pid, argument)) return;
        await RememberAsync(row, what, _ => true, change, kept: string.Format(Strings.Proc_Kept, done, row.Name));
    }

    [RelayCommand]
    private Task ToggleEfficiencyAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        bool enable = !row.IsEfficiencyMode;
        return SetAsync(row, CommandTypes.SetEfficiencyMode, enable, string.Format(Strings.Proc_EcoSet, row.Name, enable ? Strings.On : Strings.Off),
            string.Format(Strings.Proc_Eco, enable ? Strings.On : Strings.Off), r => r.EfficiencyMode = enable);
    }

    [RelayCommand]
    private Task ToggleSuspendAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        return row.IsSuspended
            ? _hub.RunAsync(CommandTypes.ResumeProcess, row.Pid, success: string.Format(Strings.Proc_Resumed, row.Name))
            : _hub.RunAsync(CommandTypes.SuspendProcess, row.Pid, success: string.Format(Strings.Proc_Paused, row.Name));
    }

    [RelayCommand]
    private async Task TrimAsync()
    {
        if (Selected is not { } row) return;
        // Null when the engine refused or failed: QueryAsync has already said why.
        if (await _hub.QueryAsync<long?>(CommandTypes.TrimWorkingSet, row.Pid) is not { } freed) return;
        _shell.Toaster.Show(string.Format(Strings.Proc_Freed, row.Name, Fmt.Bytes(freed)));
        _hub.RefreshNow();
    }

    [RelayCommand]
    private Task EndAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        Task Run() => _hub.RunAsync(CommandTypes.TerminateProcess, row.Pid, success: string.Format(Strings.Proc_Ended, row.Name));

        if (row.IsSystem)
        {
            _shell.Confirm(Strings.Proc_EndSystemTitle, string.Format(Strings.Proc_EndSystemWarning, row.Name), Strings.Proc_EndIt, Run);
            return Task.CompletedTask;
        }
        return Run();
    }

    [RelayCommand]
    private void EndTree()
    {
        if (Selected is not { } row) return;
        int children = CountDescendants(row.Pid);
        _shell.Confirm(Strings.Proc_EndTreeTitle,
            children == 0 ? string.Format(Strings.Proc_EndTreeNoChildren, row.Name)
                : string.Format(children == 1 ? Strings.Proc_EndTreeOneChild : Strings.Proc_EndTreeChildren, row.Name, children),
            Strings.EndTree,
            () => _hub.RunAsync(CommandTypes.TerminateProcessTree, row.Pid, success: string.Format(Strings.Proc_TreeEnded, row.Name)));
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
    private async Task MakeRuleAsync()
    {
        if (Selected is not { } row) return;
        await _shell.Rules.StartNewAsync(SeedRule(row, _logical));
        _shell.GoTo(_shell.Rules);
    }

    /// <summary>
    /// A new rule holding what <paramref name="row"/> has now. Left out: a priority ProBalance lowered and cores the CPU
    /// Limiter took away (both temporary), and I/O priority, memory priority and cores at Windows' defaults, which a rule
    /// would only repeat.
    /// </summary>
    internal static ProcessRuleDto SeedRule(ProcessRow row, int logical)
    {
        long allCores = logical >= 64 ? -1L : (1L << logical) - 1;
        return new ProcessRuleDto
        {
            ProcessName = row.Name,
            PriorityClass = row.IsRestrained ? null : (int?)row.Priority,
            CpuLimitPercent = row.CpuLimitPercent,
            IoPriority = row.IoPriority is int io && io != DefaultIoPriority ? io : null,
            MemoryPriority = row.MemoryPriority is int memory && memory != DefaultMemoryPriority ? memory : null,
            AffinityMask = !row.IsAffinityLimited && row.AffinityMask != 0 && (row.AffinityMask & allCores) != allCores ? row.AffinityMask : null,
        };
    }

    private const int DefaultIoPriority = 2, DefaultMemoryPriority = 5;

    // ------------------------------------------------------------------ "always": kept as the program's rule

    /// <summary>The menus' "Forget" entry: drops that setting from the program's rule.</summary>
    public const string Forget = "Forget";

    /// <summary>The selected program's rule as last read (when the menu opened, or after an "always" change); null when it has none.</summary>
    [ObservableProperty] private ProcessRuleDto? selectedRule;

    partial void OnSelectedChanged(ProcessRow? value) => SelectedRule = null;

    /// <summary>Reads the selected program's rule, so the "Always" menus can tick what it keeps.</summary>
    public async Task LoadSelectedRuleAsync()
    {
        if (Selected is not { } row) return;
        var rules = await _hub.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules, quiet: true);
        if (rules != null && Selected == row) SelectedRule = RuleText.RuleFor(rules, row.Name);
    }

    /// <summary>Priority by name, or <see cref="Forget"/>: set on every running copy now, and on each one that starts later.</summary>
    [RelayCommand]
    private Task AlwaysPriorityAsync(string level)
    {
        if (Selected is not { } row) return Task.CompletedTask;
        if (level == Forget) return RememberAsync(row, null, r => r.PriorityClass != null, r => r.PriorityClass = null);
        if (!Enum.TryParse<ProcessPriorityClass>(level, out var priority) || !Enum.IsDefined(priority)) return Task.CompletedTask;

        Task Run() => RememberAsync(row, string.Format(Strings.Proc_Priority, Fmt.Priority(priority)), _ => true, r => r.PriorityClass = (int)priority);
        if (priority == ProcessPriorityClass.RealTime)
        {
            if (!IsAdministrator)
            {
                _shell.Toaster.Show(Strings.Proc_RealtimeNeedsAdmin, isError: true, offerElevation: true);
                return Task.CompletedTask;
            }
            _shell.Confirm(Strings.Proc_AlwaysRealtimeTitle, string.Format(Strings.Proc_AlwaysRealtimeWarning, row.Name),
                Strings.Proc_AlwaysRealtime, Run);
            return Task.CompletedTask;
        }
        return Run();
    }

    /// <summary>I/O priority hint 0-3, or <see cref="Forget"/>. High needs administrator rights, as when setting it once.</summary>
    [RelayCommand]
    private Task AlwaysIoPriorityAsync(string level)
    {
        if (Selected is not { } row) return Task.CompletedTask;
        if (level == Forget) return RememberAsync(row, null, r => r.IoPriority != null, r => r.IoPriority = null);
        if (!int.TryParse(level, out var hint) || hint is < 0 or > 3) return Task.CompletedTask;
        if (hint == 3 && !IsAdministrator)
        {
            _shell.Toaster.Show(Strings.Proc_IoHighNeedsAdmin, isError: true, offerElevation: true);
            return Task.CompletedTask;
        }
        return RememberAsync(row, string.Format(Strings.Proc_Io, Fmt.IoPriority(hint)), _ => true, r => r.IoPriority = hint);
    }

    /// <summary>Memory priority 1-5, or <see cref="Forget"/>.</summary>
    [RelayCommand]
    private Task AlwaysMemoryPriorityAsync(string level)
    {
        if (Selected is not { } row) return Task.CompletedTask;
        if (level == Forget) return RememberAsync(row, null, r => r.MemoryPriority != null, r => r.MemoryPriority = null);
        if (!int.TryParse(level, out var value) || value is < 1 or > 5) return Task.CompletedTask;
        return RememberAsync(row, string.Format(Strings.Proc_Memory, Fmt.MemoryPriority(value)), _ => true, r => r.MemoryPriority = value);
    }

    /// <summary>
    /// Writes one setting into the program's rule, creating the rule if there is none and leaving the rest of it alone.
    /// Saving a rule puts it into effect on the running copies straight away, and the engine applies it again to every
    /// copy that starts, including the ones running when the engine itself starts. <paramref name="what"/> is null when
    /// forgetting: <paramref name="has"/> then says whether the rule keeps that setting, and a rule left with nothing to
    /// do is deleted.
    /// </summary>
    private async Task RememberAsync(ProcessRow row, string? what, Func<ProcessRuleDto, bool> has, Action<ProcessRuleDto> change, string? kept = null)
    {
        var rules = await _hub.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules);
        if (rules == null) return;

        var existing = RuleText.RuleFor(rules, row.Name);
        bool forgetting = what == null;
        if (forgetting && (existing == null || !has(existing)))
        {
            _shell.Toaster.Show(string.Format(Strings.Proc_NothingKept, row.Name));
            return;
        }

        var rule = existing != null ? DtoCopy.Clone(existing) : new ProcessRuleDto { ProcessName = RuleText.Display(row.Name) };
        change(rule);
        // Kept means kept: if the program (a game resetting its own priority, say) changes it later, the engine puts it
        // back. ProBalance and the boosts still lower or raise it for a while; the engine waits until they let go.
        if (!forgetting) rule.ReapplyIfChanged = true;

        // A change that undid the last thing the rule kept (all cores again, the cap removed): no rule is left to keep.
        if (!forgetting && !HasEffect(rule))
        {
            if (existing == null)
            {
                _shell.Toaster.Show(kept ?? string.Format(Strings.Proc_NameWhat, row.Name, what));
                return;
            }
            forgetting = true;
        }

        if (forgetting && !HasEffect(rule))
        {
            if (await _hub.RunAsync(CommandTypes.DeleteProcessRule, argument: rule.Id,
                    success: string.Format(Strings.Proc_Forgotten, row.Name)))
            {
                if (Selected == row) SelectedRule = null;
                _hub.RefreshNow();
            }
            return;
        }

        var saved = await _hub.QueryAsync<ProcessRuleDto>(CommandTypes.SaveProcessRule, argument: rule);
        if (saved == null) return;
        if (Selected == row) SelectedRule = saved;
        _shell.Toaster.Show(forgetting
            ? string.Format(Strings.Proc_Forgotten, row.Name)
            : kept ?? string.Format(Strings.Proc_Always, row.Name, what));
        _hub.RefreshNow();
    }

    /// <summary>Whether the rule does anything besides re-applying (which alone does nothing).</summary>
    private bool HasEffect(ProcessRuleDto rule)
    {
        var check = DtoCopy.Clone(rule);
        check.ReapplyIfChanged = false;
        return RuleEffects.All(check, _logical).Count > 0;
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
            _shell.Toaster.Show(Strings.Proc_NoLocation, isError: true);
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
