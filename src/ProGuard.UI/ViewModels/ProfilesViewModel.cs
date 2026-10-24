using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.Shared.Profiles;
using ProGuard.UI.Services;
using ProGuard.UI.Resources;

namespace ProGuard.UI.ViewModels;

public sealed class ProfileRow
{
    public ConfigProfileDto Profile { get; }
    public int Id => Profile.Id;
    public string Name => Profile.Name.ToUpperInvariant();
    public bool Automatic => Profile.Automatic;

    /// <summary>"3 RULES · 1 WATCHDOG · SAVED 14 OCT 21:07".</summary>
    public string Detail { get; }

    public ProfileRow(ConfigProfileDto profile)
    {
        Profile = profile;
        var saved = profile.SavedUtc == default ? "" : string.Format(Strings.Prof_Saved, profile.SavedUtc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture).ToUpperInvariant());
        var rules = string.Format(profile.ProcessRuleCount == 1 ? Strings.Prof_OneRule : Strings.Prof_Rules, profile.ProcessRuleCount);
        Detail = string.Format(Strings.Prof_Detail, rules, profile.WatchdogRuleCount, saved)
                 + (profile.Automatic ? Strings.Prof_KeptAutomatically : "");
    }
}

/// <summary>
/// The System page's PROFILES &amp; BACKUP section: named copies of the rules, Watchdog rules and settings to switch
/// between, and export to / import from a file. Switching and importing show what will change first, and the engine
/// keeps what was in force as "Before switch" so a switch can be undone.
/// </summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly EngineHub _hub;
    private readonly ShellViewModel _shell;
    private readonly IConfigFiles _files;
    private readonly Func<Task> _applied;

    public ObservableCollection<ProfileRow> Profiles { get; } = new();

    [ObservableProperty] private bool hasProfiles;

    /// <param name="applied">Runs after a switch or import replaced the configuration, so the page can show the new settings.</param>
    public ProfilesViewModel(EngineHub hub, ShellViewModel shell, Func<Task> applied, IConfigFiles? files = null)
    {
        _hub = hub;
        _shell = shell;
        _applied = applied;
        _files = files ?? new ConfigFileDialogs();
    }

    public async Task LoadAsync()
    {
        if (await _hub.QueryAsync<List<ConfigProfileDto>>(CommandTypes.ListProfiles, quiet: true) is { } list) Show(list);
    }

    internal void Show(IEnumerable<ConfigProfileDto> profiles)
    {
        Profiles.Clear();
        // Named profiles first, the automatic "Before switch" copies after them.
        foreach (var p in profiles.OrderBy(p => p.Automatic).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) Profiles.Add(new ProfileRow(p));
        HasProfiles = Profiles.Count > 0;
    }

    // ------------------------------------------------------------------ saving, renaming, deleting

    /// <summary>"Save current as…".</summary>
    [RelayCommand]
    private void NewProfile() =>
        _shell.ShowDialog(new ProfileNameDialogViewModel(_shell, Strings.SaveCurrentAs, Strings.Prof_NewProfile, Strings.Prof_Save, string.Empty,
            Profiles.Where(p => !p.Automatic).Select(p => p.Profile.Name).ToList(), SaveAsync));

    private async Task SaveAsync(string name)
    {
        if (await _hub.QueryAsync<ConfigProfileDto>(CommandTypes.SaveProfile, argument: name) is not { } saved) return;
        _shell.Toaster.Show(string.Format(Strings.Prof_SavedAs, saved.Name.ToUpperInvariant()));
        await LoadAsync();
    }

    [RelayCommand]
    private void RenameProfile(ProfileRow row) =>
        _shell.ShowDialog(new ProfileNameDialogViewModel(_shell, Strings.Prof_RenameProfile, row.Name, Strings.Rename, row.Profile.Name,
            Profiles.Where(p => p.Id != row.Id).Select(p => p.Profile.Name).ToList(), async name =>
            {
                if (await _hub.QueryAsync<ConfigProfileDto>(CommandTypes.RenameProfile, argument: new RenameProfileArgument { Id = row.Id, Name = name }) == null) return;
                _shell.Toaster.Show(string.Format(Strings.Prof_RenamedTo, name.ToUpperInvariant()));
                await LoadAsync();
            }) { ReplacesExisting = false });

    [RelayCommand]
    private void DeleteProfile(ProfileRow row) =>
        _shell.Confirm(string.Format(Strings.Prof_DeleteTitle, row.Name), Strings.Prof_DeleteWarning, Strings.Delete,
            async () =>
            {
                if (await _hub.RunAsync(CommandTypes.DeleteProfile, argument: row.Id, success: string.Format(Strings.Prof_Deleted, row.Name))) await LoadAsync();
            });

    // ------------------------------------------------------------------ switching

    [RelayCommand]
    private async Task SwitchProfileAsync(ProfileRow row)
    {
        var preview = await _hub.QueryAsync<ConfigPreviewDto>(CommandTypes.SwitchProfile, argument: new SwitchProfileArgument { Id = row.Id, Preview = true });
        if (preview == null) return;
        _shell.ShowDialog(new ConfigPreviewDialogViewModel(_shell, preview, Strings.Prof_SwitchProfile, string.Format(Strings.Prof_SwitchToTitle, row.Name), Strings.Switch,
            Strings.Prof_SwitchNote,
            async () =>
            {
                var done = await _hub.QueryAsync<ConfigPreviewDto>(CommandTypes.SwitchProfile, argument: new SwitchProfileArgument { Id = row.Id });
                if (done != null) await AppliedAsync(done, string.Format(Strings.Prof_SwitchedTo, row.Name));
            }));
    }

    private async Task AppliedAsync(ConfigPreviewDto result, string message)
    {
        var flagged = result.MachineSpecific.Count;
        _shell.Toaster.Show(message + (flagged == 0 ? "" : string.Format(flagged == 1 ? Strings.Prof_OneMachineValueToast : Strings.Prof_MachineValuesToast, flagged)));
        _hub.RefreshNow();
        await LoadAsync();
        await _applied();
    }

    // ------------------------------------------------------------------ files

    /// <summary>Exports a profile, or with no row the configuration in force.</summary>
    [RelayCommand]
    private async Task ExportAsync(ProfileRow? row)
    {
        var path = _files.PickSavePath(ConfigFileDialogs.SuggestName(row?.Profile.Name ?? "config " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        if (path == null) return;
        var document = await _hub.QueryAsync<ConfigDocument>(CommandTypes.ExportConfig, argument: row?.Id.ToString(CultureInfo.InvariantCulture));
        if (document == null) return;
        try
        {
            await File.WriteAllTextAsync(path, ConfigDocuments.Serialize(document));
            _shell.Toaster.Show(string.Format(Strings.Prof_ExportedTo, Path.GetFileName(path).ToUpperInvariant()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _shell.Toaster.Show(string.Format(Strings.Prof_CouldntWrite, path, ex.Message), isError: true);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = _files.PickOpenPath();
        if (path == null) return;

        string text;
        try
        {
            // Bytes, not characters, but a file this far over the limit is never a config.
            if (new FileInfo(path).Length > ConfigDocuments.MaxDocumentLength * 4L)
            {
                _shell.Toaster.Show(string.Format(Strings.Prof_TooLarge, Path.GetFileName(path)), isError: true);
                return;
            }
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _shell.Toaster.Show(string.Format(Strings.Prof_CouldntRead, path, ex.Message), isError: true);
            return;
        }

        // The engine reads and checks the file, so what is previewed is what it will apply.
        var preview = await _hub.QueryAsync<ConfigPreviewDto>(CommandTypes.ImportConfig, argument: new ImportConfigArgument { Document = text, Preview = true });
        if (preview == null) return;
        var name = string.IsNullOrWhiteSpace(preview.Name) ? Path.GetFileName(path) : preview.Name;
        _shell.ShowDialog(new ConfigPreviewDialogViewModel(_shell, preview, Strings.Prof_ImportConfig, string.Format(Strings.Prof_ImportTitle, name.ToUpperInvariant()), Strings.Prof_Import,
            Strings.Prof_ImportNote,
            async () =>
            {
                var done = await _hub.QueryAsync<ConfigPreviewDto>(CommandTypes.ImportConfig, argument: new ImportConfigArgument { Document = text });
                if (done != null) await AppliedAsync(done, Strings.Prof_ConfigImported);
            }));
    }
}

/// <summary>Asks for a profile name (save as, rename).</summary>
public sealed partial class ProfileNameDialogViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly IReadOnlyCollection<string> _taken;
    private readonly Func<string, Task> _action;

    public string Kicker { get; }
    public string Title { get; }
    public string ConfirmText { get; }

    /// <summary>Saving under a name in use replaces that profile (say so); renaming onto one is refused.</summary>
    public bool ReplacesExisting { get; init; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string name;

    public ProfileNameDialogViewModel(ShellViewModel shell, string kicker, string title, string confirmText, string name,
        IReadOnlyCollection<string> taken, Func<string, Task> action)
    {
        _shell = shell;
        _taken = taken;
        _action = action;
        Kicker = kicker;
        Title = title;
        ConfirmText = confirmText;
        this.name = name;
    }

    private string Trimmed => Name?.Trim() ?? string.Empty;
    private bool Taken => _taken.Any(t => string.Equals(t, Trimmed, StringComparison.OrdinalIgnoreCase));
    private static bool Reserved(string n) => string.Equals(n, "Before switch", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "Before import", StringComparison.OrdinalIgnoreCase);

    public string Hint =>
        Trimmed.Length == 0 ? Strings.Prof_GiveItAName
        : Trimmed.Length > 60 ? Strings.Prof_AtMost60
        : Reserved(Trimmed) ? Strings.Prof_NameReserved
        : Taken && ReplacesExisting ? Strings.Prof_Replaces
        : Taken ? Strings.Prof_NameTaken
        : string.Empty;

    private bool CanConfirm() => Trimmed.Length is > 0 and <= 60 && !Reserved(Trimmed) && (ReplacesExisting || !Taken);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        _shell.CloseDialog();
        await _action(Trimmed);
    }
}

public sealed record ConfigChangeRow(string Sign, string Area, string Name, string Detail);

/// <summary>What a switch or import will change, the machine-specific values it leaves out, and anything that stops it.</summary>
public sealed partial class ConfigPreviewDialogViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly Func<Task> _action;

    public string Kicker { get; }
    public string Title { get; }
    public string ConfirmText { get; }
    public string BackupNote { get; }
    public string Summary { get; }
    public IReadOnlyList<ConfigChangeRow> Changes { get; }
    public IReadOnlyList<MachineSpecificValueDto> MachineSpecific { get; }
    public IReadOnlyList<string> Blockers { get; }
    public bool HasChanges => Changes.Count > 0;
    public bool HasMachineSpecific => MachineSpecific.Count > 0;
    public bool HasBlockers => Blockers.Count > 0;
    public bool CanApply { get; }

    public ConfigPreviewDialogViewModel(ShellViewModel shell, ConfigPreviewDto preview, string kicker, string title, string confirmText, string backupNote, Func<Task> action)
    {
        _shell = shell;
        _action = action;
        Kicker = kicker;
        Title = title;
        ConfirmText = confirmText;
        BackupNote = backupNote;
        Changes = preview.Changes.Select(c => new ConfigChangeRow(
            c.Kind switch { ConfigChangeKind.Added => "+", ConfigChangeKind.Removed => "−", _ => "~" },
            c.Area.ToUpperInvariant(), c.Name, c.Detail)).ToList();
        MachineSpecific = preview.MachineSpecific;
        Blockers = preview.Refusals.Concat(preview.Problems).ToList();
        CanApply = preview.CanApply;

        var parts = new List<string> { Changes.Count == 0 ? Strings.Prof_NothingChanges : string.Format(Changes.Count == 1 ? Strings.Prof_OneChange : Strings.Prof_Changes, Changes.Count) };
        if (MachineSpecific.Count > 0) parts.Add(string.Format(MachineSpecific.Count == 1 ? Strings.Prof_OneMachineValue : Strings.Prof_MachineValues, MachineSpecific.Count));
        if (Blockers.Count > 0) parts.Add(Strings.CantBeApplied);
        Summary = string.Join(" · ", parts);
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ConfirmAsync()
    {
        _shell.CloseDialog();
        await _action();
    }
}
