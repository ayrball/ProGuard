using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.UI.Services;

namespace ProGuard.UI.ViewModels;

/// <summary>A page that wants to know when it is on screen (to start or stop its own refreshing).</summary>
public interface IPage
{
    void OnShown();
    void OnHidden();
}

public sealed record PageItem(string Number, string Title, object Page);

/// <summary>The window: navigation rail, the current page, the modal layer and the engine status strip.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public EngineHub Hub { get; }
    public Toaster Toaster { get; }

    public OverviewViewModel Overview { get; }
    public ProcessesViewModel Processes { get; }
    public RulesViewModel Rules { get; }
    public WatchdogViewModel Watchdog { get; }
    public ActivityViewModel Activity { get; }
    public SystemViewModel System { get; }

    public IReadOnlyList<PageItem> Pages { get; }

    [ObservableProperty] private PageItem currentPage;

    /// <summary>A dialog view model shown over everything, or null.</summary>
    [ObservableProperty] private object? dialog;

    [ObservableProperty] private string statusLine = "CONNECTING";
    [ObservableProperty] private string clock = string.Empty;

    public ShellViewModel(EngineHub hub, Toaster toaster, IconCache icons)
    {
        Hub = hub;
        Toaster = toaster;

        Overview = new OverviewViewModel(hub, this);
        Processes = new ProcessesViewModel(hub, icons, this);
        Rules = new RulesViewModel(hub, this);
        Watchdog = new WatchdogViewModel(hub, this);
        Activity = new ActivityViewModel(hub);
        System = new SystemViewModel(hub, this);

        Pages = new[]
        {
            new PageItem("01", "OVERVIEW", Overview),
            new PageItem("02", "PROCESSES", Processes),
            new PageItem("03", "RULES", Rules),
            new PageItem("04", "WATCHDOG", Watchdog),
            new PageItem("05", "ACTIVITY", Activity),
            new PageItem("06", "SYSTEM", System),
        };
        currentPage = Pages[0];

        hub.SnapshotApplied += OnSnapshot;
        hub.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EngineHub.IsOnline) && !hub.IsOnline) StatusLine = "ENGINE OFFLINE";
        };
    }

    partial void OnCurrentPageChanged(PageItem? oldValue, PageItem newValue)
    {
        (oldValue?.Page as IPage)?.OnHidden();
        (newValue.Page as IPage)?.OnShown();
    }

    [RelayCommand]
    private void Navigate(PageItem page) => CurrentPage = page;

    public void GoTo(object page)
    {
        var item = Pages.FirstOrDefault(p => ReferenceEquals(p.Page, page));
        if (item != null) CurrentPage = item;
    }

    public void ShowDialog(object dialogViewModel) => Dialog = dialogViewModel;

    [RelayCommand]
    public void CloseDialog() => Dialog = null;

    /// <summary>Shows a confirmation and runs <paramref name="action"/> only if the user agrees.</summary>
    public void Confirm(string title, string message, string confirmText, Func<Task> action) =>
        ShowDialog(new ConfirmDialogViewModel(this, title, message, confirmText, action));

    private void OnSnapshot(EngineSnapshot snapshot)
    {
        var mode = snapshot.CallerIsAdministrator ? "ADMIN" : "STANDARD RIGHTS";
        StatusLine = $"ENGINE ONLINE · {mode} · UP {Fmt.Uptime(snapshot.EngineUptime)}";
        Clock = DateTime.Now.ToString("HH:mm:ss");
    }
}

public sealed partial class ConfirmDialogViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly Func<Task> _action;

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }

    public ConfirmDialogViewModel(ShellViewModel shell, string title, string message, string confirmText, Func<Task> action)
    {
        _shell = shell;
        _action = action;
        Title = title;
        Message = message;
        ConfirmText = confirmText;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        _shell.CloseDialog();
        await _action();
    }
}
