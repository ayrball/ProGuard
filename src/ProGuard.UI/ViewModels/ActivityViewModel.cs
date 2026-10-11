using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

public sealed class ActivityRow
{
    public ActivityEntry Entry { get; }
    public string Time { get; }
    public string Kind { get; }
    public string Process { get; }
    public string Message => Entry.Message;

    public ActivityRow(ActivityEntry entry)
    {
        Entry = entry;
        Time = entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
        Kind = entry.Kind switch
        {
            ActivityKind.Restrained => "BALANCED",
            ActivityKind.Restored => "RESTORED",
            ActivityKind.Rule => "RULE",
            ActivityKind.Watchdog => "WATCHDOG",
            ActivityKind.Manual => "YOU",
            _ => "ENGINE"
        };
        Process = string.IsNullOrEmpty(entry.ProcessName) ? "—" : Fmt.Exe(entry.ProcessName).ToUpperInvariant();
    }
}

/// <summary>What ProGuard (and you) did, newest first. Polls only while the page is on screen.</summary>
public sealed partial class ActivityViewModel : ObservableObject, IPage
{
    private readonly EngineHub _hub;
    private readonly DispatcherTimer _timer;
    private List<ActivityEntry> _all = new();

    public IReadOnlyList<Choice<ActivityKind?>> Kinds { get; } = new Choice<ActivityKind?>[]
    {
        new("ALL", null),
        new("BALANCED", ActivityKind.Restrained),
        new("RESTORED", ActivityKind.Restored),
        new("RULES", ActivityKind.Rule),
        new("WATCHDOG", ActivityKind.Watchdog),
        new("YOU", ActivityKind.Manual),
        new("ENGINE", ActivityKind.System),
    };

    public ObservableCollection<ActivityRow> Entries { get; } = new();

    [ObservableProperty] private Choice<ActivityKind?> kind;
    [ObservableProperty] private bool isEmpty = true;

    public ActivityViewModel(EngineHub hub)
    {
        _hub = hub;
        kind = Kinds[0];
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await LoadAsync();
    }

    partial void OnKindChanged(Choice<ActivityKind?> value) => Rebuild();

    public void OnShown()
    {
        _ = LoadAsync();
        _timer.Start();
    }

    public void OnHidden() => _timer.Stop();

    private async Task LoadAsync()
    {
        var entries = await _hub.QueryAsync<List<ActivityEntry>>(CommandTypes.GetActivity, argument: 400, quiet: true);
        if (entries == null) return;

        // Skip the rebuild when nothing new arrived, so scrolling and selection stay put.
        if (entries.Count == _all.Count && (entries.Count == 0 || entries[0].TimestampUtc == _all[0].TimestampUtc)) return;
        _all = entries;
        Rebuild();
    }

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var entry in _all)
        {
            if (Kind.Value is { } k && entry.Kind != k) continue;
            Entries.Add(new ActivityRow(entry));
        }
        IsEmpty = Entries.Count == 0;
    }
}
