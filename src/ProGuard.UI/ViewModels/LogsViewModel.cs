using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

/// <summary>One line of the log table, with the text it shows ready-made.</summary>
public sealed class LogRow
{
    public LogEntryDto Entry { get; }
    public string Id => Entry.Id;
    public string Time { get; }
    public string FullTime { get; }
    public string Level { get; }
    public string LevelName { get; }
    public string Source { get; }
    public string Category { get; }
    public string Process { get; }
    /// <summary>The message on one line, for the table.</summary>
    public string Message { get; }
    public string FullMessage => Entry.Message;
    public string? Detail => Entry.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(Entry.Detail);
    /// <summary>Trace and Debug: shown dimmed.</summary>
    public bool IsQuiet => Entry.Level <= LogLevelDto.Debug;
    public bool IsWarning => Entry.Level == LogLevelDto.Warning;
    /// <summary>Error and Critical: shown as a solid block.</summary>
    public bool IsError => Entry.Level >= LogLevelDto.Error;

    public LogRow(LogEntryDto entry, DateTime? today = null)
    {
        Entry = entry;
        var local = entry.TimestampUtc.ToLocalTime();
        Time = local.Date == (today ?? DateTime.Today)
            ? local.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
            : local.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        FullTime = local.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Level = ShortLevel(entry.Level);
        LevelName = entry.Level.ToString().ToUpperInvariant();
        Source = entry.Source == LogSource.Engine ? "ENGINE" : "ACTIONS";
        Category = string.IsNullOrEmpty(entry.Category) ? "—" : entry.Category;
        Process = (entry.ProcessName, entry.Pid) switch
        {
            (null or "", null) => "—",
            (null or "", int pid) => pid.ToString(CultureInfo.InvariantCulture),
            (string name, null) => name,
            (string name, int pid) => $"{name} · {pid}",
        };
        Message = entry.Message.ReplaceLineEndings(" ");
    }

    public static string ShortLevel(LogLevelDto level) => level switch
    {
        LogLevelDto.Trace => "TRC",
        LogLevelDto.Debug => "DBG",
        LogLevelDto.Information => "INF",
        LogLevelDto.Warning => "WRN",
        LogLevelDto.Error => "ERR",
        _ => "CRT",
    };

    /// <summary>What "copy row" puts on the clipboard: the fields tab-separated, then the detail, if any.</summary>
    public string ClipboardText
    {
        get
        {
            var text = string.Join('\t', FullTime, LevelName, Source, Entry.Category, Entry.ProcessName ?? "",
                Entry.Pid?.ToString(CultureInfo.InvariantCulture) ?? "", Entry.Message);
            return HasDetail ? text + Environment.NewLine + Entry.Detail : text;
        }
    }
}

/// <summary>A level chip: on means entries of that level are shown.</summary>
public sealed partial class LevelFilter : ObservableObject
{
    private readonly Action _changed;

    public string Label { get; }
    public LogLevelDto Level { get; }
    [ObservableProperty] private bool isOn = true;

    public LevelFilter(string label, LogLevelDto level, Action changed)
    {
        Label = label;
        Level = level;
        _changed = changed;
    }

    partial void OnIsOnChanged(bool value) => _changed();
}

/// <summary>
/// ADVANCED / LOGS: the engine log and the action log in one table, newest first. Pages older entries in as the table is
/// scrolled, and while live, polls for new ones every <see cref="TailInterval"/> (only while on screen). Every filter is
/// applied by the engine, so paging and the tail stay consistent with what is shown.
/// </summary>
public sealed partial class LogsViewModel : ObservableObject, IPage
{
    public const int PageSize = 200;

    private readonly EngineHub _hub;
    private readonly ShellViewModel? _shell;
    private readonly DispatcherTimer _tailTimer;
    private readonly DispatcherTimer _filterTimer;

    private string? _olderCursor;
    private string? _headCursor;
    private int _generation;
    private bool _loaded;
    private bool _loadingOlder;
    private bool _tailing;

    public ObservableCollection<LogRow> Entries { get; } = new();

    public IReadOnlyList<Choice<LogSource?>> Sources { get; } = new Choice<LogSource?>[]
    {
        new("ALL SOURCES", null),
        new("ENGINE LOG", LogSource.Engine, AdminOnly: true),
        new("ACTION LOG", LogSource.Actions),
    };

    public IReadOnlyList<LevelFilter> Levels { get; }

    public IReadOnlyList<Choice<TimeSpan?>> Ranges { get; } = new Choice<TimeSpan?>[]
    {
        new("ANY TIME", null),
        new("15 MIN", TimeSpan.FromMinutes(15)),
        new("1 HOUR", TimeSpan.FromHours(1)),
        new("24 HOURS", TimeSpan.FromHours(24)),
        new("7 DAYS", TimeSpan.FromDays(7)),
    };

    [ObservableProperty] private Choice<LogSource?> source;
    [ObservableProperty] private Choice<TimeSpan?> range;
    [ObservableProperty] private string search = string.Empty;
    [ObservableProperty] private string process = string.Empty;
    [ObservableProperty] private string category = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedCommand))]
    private LogRow? selected;

    /// <summary>Following new entries as they are written; false while paused.</summary>
    [ObservableProperty] private bool isLive = true;
    /// <summary>Entries the live tail has added since the page was loaded or the tail resumed.</summary>
    [ObservableProperty] private int newSinceResume;
    [ObservableProperty] private bool hasOlder;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isEmpty = true;
    /// <summary>False when the engine left its own log out because the caller isn't an administrator.</summary>
    [ObservableProperty] private bool engineLogIncluded = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearActionLogCommand))]
    private bool isAdministrator = true;

    public TimeSpan TailInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Asks where to save an export (the suggested file name in, a path or null out). A save dialog unless replaced.</summary>
    public Func<string, string?> PickExportPath { get; set; } = AskForExportPath;
    /// <summary>Puts text on the clipboard. Replaced in tests.</summary>
    public Action<string> CopyText { get; set; } = text => Clipboard.SetText(text);

    public LogsViewModel(EngineHub hub, ShellViewModel? shell = null)
    {
        _hub = hub;
        _shell = shell;
        source = Sources[0];
        range = Ranges[0];
        Levels = new[]
        {
            new LevelFilter("TRACE", LogLevelDto.Trace, () => ScheduleReload(chip: true)),
            new LevelFilter("DEBUG", LogLevelDto.Debug, () => ScheduleReload(chip: true)),
            new LevelFilter("INFO", LogLevelDto.Information, () => ScheduleReload(chip: true)),
            new LevelFilter("WARN", LogLevelDto.Warning, () => ScheduleReload(chip: true)),
            new LevelFilter("ERROR", LogLevelDto.Error, () => ScheduleReload(chip: true)),
            new LevelFilter("CRITICAL", LogLevelDto.Critical, () => ScheduleReload(chip: true)),
        };

        hub.SnapshotApplied += s => IsAdministrator = s.CallerIsAdministrator;

        _tailTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TailInterval };
        _tailTimer.Tick += async (_, _) => await TailAsync();
        // Filters reload after a pause, so typing a search doesn't send a query per key.
        _filterTimer = new DispatcherTimer(DispatcherPriority.Background);
        _filterTimer.Tick += async (_, _) =>
        {
            _filterTimer.Stop();
            await ReloadAsync();
        };
    }

    // ------------------------------------------------------------------ on screen

    public void OnShown()
    {
        _ = _loaded ? TailAsync() : ReloadAsync();
        _tailTimer.Interval = TailInterval;
        if (IsLive) _tailTimer.Start();
    }

    public void OnHidden()
    {
        _tailTimer.Stop();
        _filterTimer.Stop();
    }

    partial void OnSourceChanged(Choice<LogSource?> value) => ScheduleReload(chip: true);
    partial void OnRangeChanged(Choice<TimeSpan?> value) => ScheduleReload(chip: true);
    partial void OnSearchChanged(string value) => ScheduleReload(chip: false);
    partial void OnProcessChanged(string value) => ScheduleReload(chip: false);
    partial void OnCategoryChanged(string value) => ScheduleReload(chip: false);

    private void ScheduleReload(bool chip)
    {
        _filterTimer.Stop();
        _filterTimer.Interval = chip ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromMilliseconds(350);
        _filterTimer.Start();
    }

    /// <summary>The query the filters describe, without a cursor.</summary>
    public LogQuery BuildQuery(DateTime? nowUtc = null)
    {
        var query = new LogQuery { Limit = PageSize };
        if (Source.Value is LogSource s) query.Sources = new List<LogSource> { s };
        if (Levels.Any(l => !l.IsOn)) query.Levels = Levels.Where(l => l.IsOn).Select(l => l.Level).ToList();
        if (Range.Value is TimeSpan span) query.FromUtc = (nowUtc ?? DateTime.UtcNow) - span;
        if (!string.IsNullOrWhiteSpace(Search)) query.Text = Search.Trim();
        if (!string.IsNullOrWhiteSpace(Process)) query.Process = Process.Trim();
        if (!string.IsNullOrWhiteSpace(Category)) query.Category = Category.Trim();
        return query;
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Starts again from the newest page, keeping the selection if it is still there.</summary>
    public async Task ReloadAsync()
    {
        var generation = ++_generation;
        var query = BuildQuery();
        if (Levels.All(l => !l.IsOn))
        {
            // Nothing can match; the engine would take an empty list to mean every level.
            Show(Array.Empty<LogEntryDto>(), null, _headCursor, EngineLogIncluded);
            return;
        }

        IsLoading = true;
        try
        {
            var page = await _hub.QueryAsync<LogPage>(CommandTypes.GetLogEntries, argument: query, quiet: _loaded);
            if (generation != _generation || page == null) return;
            Show(page.Entries, page.OlderCursor, page.HeadCursor, page.EngineLogIncluded);
            _loaded = true;
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }

    private void Show(IReadOnlyList<LogEntryDto> entries, string? older, string? head, bool engineLogIncluded)
    {
        var keep = Selected?.Id;
        Entries.Clear();
        foreach (var entry in entries) Entries.Add(new LogRow(entry));
        Selected = keep == null ? null : Entries.FirstOrDefault(r => r.Id == keep);
        _olderCursor = older;
        _headCursor = head;
        HasOlder = older != null;
        EngineLogIncluded = engineLogIncluded;
        NewSinceResume = 0;
        IsEmpty = Entries.Count == 0;
    }

    /// <summary>Appends the next older page. The table calls it when scrolled near the bottom.</summary>
    [RelayCommand]
    public async Task LoadOlderAsync()
    {
        if (_olderCursor == null || _loadingOlder) return;
        var generation = _generation;
        var query = BuildQuery();
        query.Before = _olderCursor;

        _loadingOlder = true;
        try
        {
            var page = await _hub.QueryAsync<LogPage>(CommandTypes.GetLogEntries, argument: query, quiet: true);
            if (generation != _generation || page == null) return;
            var known = Entries.Select(r => r.Id).ToHashSet();
            foreach (var entry in page.Entries)
                if (known.Add(entry.Id)) Entries.Add(new LogRow(entry));
            _olderCursor = page.OlderCursor;
            HasOlder = page.OlderCursor != null;
            IsEmpty = Entries.Count == 0;
        }
        finally
        {
            _loadingOlder = false;
        }
    }

    /// <summary>One poll of the live tail: puts anything new at the top.</summary>
    public async Task TailAsync()
    {
        if (!IsLive || _tailing) return;
        if (_headCursor == null)
        {
            if (!_loaded) await ReloadAsync();
            return;
        }

        var generation = _generation;
        var query = BuildQuery();
        query.After = _headCursor;
        if (Levels.All(l => !l.IsOn)) return;

        _tailing = true;
        try
        {
            var page = await _hub.QueryAsync<LogPage>(CommandTypes.GetLogEntries, argument: query, quiet: true);
            if (generation != _generation || page == null) return;
            if (page.Reset || page.Truncated)
            {
                // Too much arrived, or the log started over (the action log was cleared): start from the top.
                await ReloadAsync();
                return;
            }

            var known = Entries.Select(r => r.Id).ToHashSet();
            int at = 0;
            foreach (var entry in page.Entries)
            {
                if (!known.Add(entry.Id)) continue;
                Entries.Insert(at++, new LogRow(entry));
            }
            NewSinceResume += at;
            _headCursor = page.HeadCursor;
            EngineLogIncluded = page.EngineLogIncluded;
            IsEmpty = Entries.Count == 0;
        }
        finally
        {
            _tailing = false;
        }
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task ToggleLive()
    {
        IsLive = !IsLive;
        if (!IsLive)
        {
            _tailTimer.Stop();
            return;
        }
        NewSinceResume = 0;
        _tailTimer.Interval = TailInterval;
        _tailTimer.Start();
        await TailAsync(); // catch up at once
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Source = Sources[0];
        Range = Ranges[0];
        foreach (var level in Levels) level.IsOn = true;
        Search = string.Empty;
        Process = string.Empty;
        Category = string.Empty;
    }

    [RelayCommand]
    private void ClearSearch() => Search = string.Empty;

    [RelayCommand(CanExecute = nameof(CanCopySelected))]
    private void CopySelected()
    {
        if (Selected == null) return;
        try
        {
            CopyText(Selected.ClipboardText);
            _shell?.Toaster.Show("ROW COPIED");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program has the clipboard open.
            _shell?.Toaster.Show("The clipboard is busy. Try again.", isError: true);
        }
    }

    private bool CanCopySelected() => Selected != null;

    /// <summary>Asks the engine for every entry the filters match as CSV, then saves it where the user picks.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var query = BuildQuery();
        var export = await _hub.QueryAsync<LogExportDto>(CommandTypes.ExportLogs, argument: query);
        if (export == null) return;

        var path = PickExportPath(export.FileName);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // With a byte order mark, so Excel reads it as UTF-8.
            await File.WriteAllTextAsync(path, export.Csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var what = export.Count == 1 ? "1 ENTRY" : $"{export.Count:N0} ENTRIES";
            _shell?.Toaster.Show(export.Truncated
                ? $"EXPORTED THE NEWEST {what} · MORE MATCHED THAN ONE EXPORT HOLDS"
                : $"EXPORTED {what} · {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell?.Toaster.Show($"Couldn't save the export: {ex.Message}", isError: true);
        }
    }

    [RelayCommand(CanExecute = nameof(IsAdministrator))]
    private void ClearActionLog()
    {
        if (_shell == null) return;
        _shell.Confirm("CLEAR THE ACTION LOG?",
            "Deletes the logged ProBalance, CPU Limiter and power-plan actions for everyone. The engine log is kept. This can't be undone.",
            "CLEAR ACTION LOG",
            async () =>
            {
                if (await _hub.RunAsync(CommandTypes.ClearActionLog, success: "ACTION LOG CLEARED")) await ReloadAsync();
            });
    }

    private static string? AskForExportPath(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export log",
            FileName = suggestedName,
            DefaultExt = ".csv",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true,
        };
        var owner = Application.Current?.MainWindow;
        return (owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }
}
