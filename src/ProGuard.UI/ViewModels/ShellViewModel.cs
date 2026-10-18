using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
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
    public AdvancedViewModel Advanced { get; }

    public IReadOnlyList<PageItem> Pages { get; }

    [ObservableProperty] private PageItem currentPage;

    /// <summary>A dialog view model shown over everything, or null.</summary>
    [ObservableProperty] private object? dialog;

    [ObservableProperty] private string statusLine = "CONNECTING";
    [ObservableProperty] private string clock = string.Empty;

    /// <summary>
    /// The MinimizeToTray setting: closing or minimising the window hides it to the tray instead. Defaults to the
    /// engine's default until the settings have been fetched (see <see cref="OnSnapshot"/>) or the System page changes it.
    /// </summary>
    [ObservableProperty] private bool minimizeToTray = true;

    private bool _windowSettingsRequested;
    private readonly IElevationLauncher _elevation;

    /// <summary>True while this window runs with standard rights: "Restart as administrator" is offered.</summary>
    public bool CanRestartElevated => !_elevation.IsElevated;

    /// <summary>
    /// "Always start as administrator": each start with standard rights asks for administrator rights at once (a UAC
    /// prompt), including the start at sign-in. Saying no there opens ProGuard with standard rights as before.
    /// </summary>
    public bool AlwaysStartElevated
    {
        get => _elevation.AlwaysElevate;
        set
        {
            if (value == _elevation.AlwaysElevate) return;
            try
            {
                _elevation.AlwaysElevate = value;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or global::System.Security.SecurityException or global::System.IO.IOException)
            {
                Toaster.Show($"Windows wouldn't save that: {ex.Message}", isError: true);
            }
            OnPropertyChanged();
            if (value && _elevation.AlwaysElevate && CanRestartElevated)
                Toaster.Show("From now on ProGuard starts as administrator, after a UAC prompt. Restart as administrator now to use it straight away.");
        }
    }

    /// <summary>Closes the app for good (not just to the tray). Set by the app; tests leave it, or record it.</summary>
    public Action? Quit { get; set; }

    // Ticks on its own, so the header clock keeps time while the engine is offline and no snapshots arrive.
    private readonly DispatcherTimer _clockTimer;

    public ShellViewModel(EngineHub hub, Toaster toaster, IconCache icons, IStartupRegistration? startup = null, IElevationLauncher? elevation = null)
    {
        Hub = hub;
        Toaster = toaster;
        _elevation = elevation ?? new ElevationLauncher();
        if (CanRestartElevated) toaster.Elevate = () => RestartElevatedCommand.Execute(null);

        Overview = new OverviewViewModel(hub, this);
        Processes = new ProcessesViewModel(hub, icons, this);
        Rules = new RulesViewModel(hub, this);
        Watchdog = new WatchdogViewModel(hub, this);
        Activity = new ActivityViewModel(hub, this);
        System = new SystemViewModel(hub, this, startup);
        Advanced = new AdvancedViewModel(hub, this);

        Pages = new[]
        {
            new PageItem("01", "OVERVIEW", Overview),
            new PageItem("02", "PROCESSES", Processes),
            new PageItem("03", "RULES", Rules),
            new PageItem("04", "WATCHDOG", Watchdog),
            new PageItem("05", "ACTIVITY", Activity),
            new PageItem("06", "SYSTEM", System),
            new PageItem("07", "ADVANCED", Advanced),
        };
        currentPage = Pages[0];

        clock = DateTime.Now.ToString("HH:mm:ss");
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => Clock = DateTime.Now.ToString("HH:mm:ss"), Dispatcher.CurrentDispatcher);

        hub.SnapshotApplied += OnSnapshot;
        hub.PropertyChanged += (_, e) =>
        {
            // IsOffline, not !IsOnline: it is set once a connection attempt has actually failed, which covers starting
            // with no engine (IsOnline never changes then, and the line would say CONNECTING for ever).
            if (e.PropertyName == nameof(EngineHub.IsOffline) && hub.IsOffline) StatusLine = "ENGINE OFFLINE";
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

    /// <summary>Opens the page with this title (as on the rail, any case); an unknown title is ignored.</summary>
    public void GoTo(string title)
    {
        var item = Pages.FirstOrDefault(p => string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));
        if (item != null) CurrentPage = item;
    }

    /// <summary>
    /// Starts this window again with administrator rights, on the same page, and quits this copy once the new one is on
    /// its way. Declining the UAC prompt leaves everything as it was.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestartElevated))]
    private void RestartElevated()
    {
        if (!CanRestartElevated) return; // the tray menu and toasts call Execute directly
        switch (_elevation.Restart(ElevationLauncher.PageArguments(CurrentPage.Title), out var error))
        {
            case ElevationOutcome.Started:
                Quit?.Invoke();
                break;
            case ElevationOutcome.Failed:
                Toaster.Show(error ?? "Windows couldn't start an administrator copy of ProGuard.", isError: true);
                break;
            // Cancelled: the user said no at the prompt, which needs no comment.
        }
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

        // The tray behaviour needs MinimizeToTray before the System page has ever been opened.
        if (!_windowSettingsRequested)
        {
            _windowSettingsRequested = true;
            _ = LoadWindowSettingsAsync();
        }
    }

    private async Task LoadWindowSettingsAsync()
    {
        if (await Hub.QueryAsync<EngineSettingsDto>(CommandTypes.GetSettings, quiet: true) is { } settings)
        {
            MinimizeToTray = settings.MinimizeToTray;
        }
        else
        {
            _windowSettingsRequested = false; // try again on the next snapshot
        }
    }

    /// <summary>
    /// SmartTrim's "Purge now" (administrators only; the engine refuses anyone else and says why). Shows the engine's
    /// summary as a toast. Returns null when it failed.
    /// </summary>
    public async Task<MemoryPurgeResultDto?> PurgeNowAsync()
    {
        var result = await Hub.QueryAsync<MemoryPurgeResultDto>(CommandTypes.PurgeMemory);
        if (result == null) return null;

        Toaster.Show(Fmt.PurgeSummary(result), isError: result.Errors.Count > 0);
        Hub.RefreshNow();
        return result;
    }

    /// <summary>Turns Performance Mode off if the engine reports it on, on otherwise. Returns the new state, or null on failure.</summary>
    public async Task<bool?> TogglePerformanceModeAsync()
    {
        bool enable = Hub.Snapshot?.PerformanceModeActive != true;
        bool ok = await Hub.RunAsync(enable ? CommandTypes.EnablePerformanceMode : CommandTypes.DisablePerformanceMode,
            success: enable ? "PERFORMANCE MODE ON" : "PERFORMANCE MODE OFF");
        return ok ? enable : null;
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
