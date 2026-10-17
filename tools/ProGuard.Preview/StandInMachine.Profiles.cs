using System.Globalization;
using System.Text.Json;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.Shared.Profiles;
using ActivityKind = ProGuard.Shared.Contracts.ActivityKind;

namespace ProGuard.Preview;

/// <summary>
/// Config profiles on the stand-in machine: the same commands, documents and checks as the engine's ConfigProfileService
/// (the format, the differences and the machine-specific values come from the shared <see cref="ConfigDocuments"/>),
/// with the stand-in's simplified rule checks for a standard user.
/// </summary>
public sealed partial class StandInMachine
{
    private const string BeforeSwitch = "Before switch";
    private const string BeforeImport = "Before import";
    private const string NoSuchProfile = "That profile no longer exists. Refresh the list and try again.";

    private sealed class StandInProfile
    {
        public int Id { get; init; }
        public string Name { get; set; } = string.Empty;
        public DateTime SavedUtc { get; set; }
        public ConfigDocument Document { get; set; } = new();
    }

    private List<StandInProfile>? _profiles;
    private List<StandInProfile> Profiles => _profiles ??= SeedProfiles();

    private bool TryProfiles(CommandMessage cmd, out CommandResult result)
    {
        switch (cmd.CommandType)
        {
            case CommandTypes.ListProfiles:
                result = Data(Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(ToDto).ToList());
                return true;
            case CommandTypes.SaveProfile:
                result = SaveProfile(cmd.Argument?.Trim() ?? string.Empty);
                return true;
            case CommandTypes.RenameProfile:
                result = RenameProfile(Argument<RenameProfileArgument>(cmd));
                return true;
            case CommandTypes.DeleteProfile:
            {
                var profile = FindProfile(cmd.Argument);
                if (profile != null) Mutate(() => Profiles.Remove(profile));
                result = profile == null ? CommandResult.Fail(NoSuchProfile) : CommandResult.Ok();
                return true;
            }
            case CommandTypes.ExportConfig:
            {
                if (string.IsNullOrWhiteSpace(cmd.Argument)) { result = Data(Capture(ConfigDocuments.CurrentName)); return true; }
                var profile = FindProfile(cmd.Argument);
                result = profile == null ? CommandResult.Fail(NoSuchProfile) : Data(profile.Document);
                return true;
            }
            case CommandTypes.SwitchProfile:
            {
                var arg = Argument<SwitchProfileArgument>(cmd);
                var profile = Profiles.FirstOrDefault(p => p.Id == arg.Id);
                if (profile == null) { result = CommandResult.Fail(NoSuchProfile); return true; }
                var document = ConfigDocuments.Clone(profile.Document);
                document.Name = profile.Name;
                result = PreviewOrApply(document, arg.Preview, BeforeSwitch, $"Switched to the {profile.Name} profile.");
                return true;
            }
            case CommandTypes.ImportConfig:
            {
                var arg = Argument<ImportConfigArgument>(cmd);
                var document = ConfigDocuments.Parse(arg.Document, out var error);
                result = document == null ? CommandResult.Fail(error!) : PreviewOrApply(document, arg.Preview, BeforeImport, $"Imported the {document.Name} config.");
                return true;
            }
            default:
                result = null!;
                return false;
        }
    }

    private StandInProfile? FindProfile(string? id) =>
        int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Profiles.FirstOrDefault(p => p.Id == n) : null;

    private static string? NameProblem(string name) =>
        name.Length == 0 ? "A profile needs a name."
        : IsAutomatic(name) ? $"\"{name}\" is kept for the copy ProGuard saves before a switch. Pick another name."
        : null;

    private CommandResult SaveProfile(string name) =>
        NameProblem(name) is { } problem ? CommandResult.Fail(problem) : Data(ToDto(Keep(name)));

    private CommandResult RenameProfile(RenameProfileArgument arg)
    {
        var name = arg.Name?.Trim() ?? string.Empty;
        var profile = Profiles.FirstOrDefault(p => p.Id == arg.Id);
        if (profile == null) return CommandResult.Fail(NoSuchProfile);
        if (NameProblem(name) is { } problem) return CommandResult.Fail(problem);
        if (Profiles.Any(p => p.Id != arg.Id && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail($"There is already a profile called {name}.");
        Mutate(() => profile.Name = name);
        var dto = ToDto(profile);
        dto.Name = name;
        return Data(dto);
    }

    private CommandResult PreviewOrApply(ConfigDocument document, bool previewOnly, string backup, string activity)
    {
        var current = Capture(ConfigDocuments.CurrentName);
        var fitted = ConfigDocuments.FitToMachine(document, current.Machine!, out var flags);
        var preview = new ConfigPreviewDto
        {
            Name = document.Name, SchemaVersion = document.SchemaVersion, SavedUtc = document.SavedUtc,
            Changes = ConfigDocuments.Diff(current, fitted), MachineSpecific = flags,
        };
        foreach (var rule in fitted.ProcessRules)
            if (ValidateRule(rule) is { } problem) preview.Problems.Add($"Process rule {rule.ProcessName}: {problem}");

        // The rules a standard user would add, change or remove, checked as if saved one by one.
        var changedRules = fitted.ProcessRules.Where(r => !current.ProcessRules.Any(c => ConfigDocuments.SameRule(c, r)))
            .Concat(current.ProcessRules.Where(c => !fitted.ProcessRules.Any(r => ConfigDocuments.SameRule(c, r))));
        foreach (var rule in changedRules)
            if (RuleRefusal(rule) is { } refused) preview.Refusals.Add(Deny(refused).Error!);
        var unmatched = current.WatchdogRules.Select(ConfigDocuments.WatchdogKey).ToList();
        foreach (var rule in fitted.WatchdogRules.Where(r => !unmatched.Remove(ConfigDocuments.WatchdogKey(r))).ToList())
            if (WatchRefusal(rule) is { } refused) preview.Refusals.Add(Deny(refused).Error!);
        foreach (var rule in current.WatchdogRules.Where(r => unmatched.Contains(ConfigDocuments.WatchdogKey(r))))
            if (WatchRefusal(rule) is { } refused) preview.Refusals.Add(Deny(refused).Error!);
        if (!_admin && fitted.Settings.SetTimerResolutionAtStartup != _settings.SetTimerResolutionAtStartup)
            preview.Refusals.Add(Deny("change the system timer resolution").Error!);
        if (!_admin && fitted.Settings.ProBalanceDisableCoreParking != _settings.ProBalanceDisableCoreParking)
            preview.Refusals.Add(Deny("change whether ProBalance unparks cores (it edits the power plan)").Error!);
        preview.Refusals = preview.Refusals.Distinct().ToList();

        if (previewOnly) return Data(preview);
        if (preview.Refusals.Count > 0) return CommandResult.Fail(preview.Refusals[0]);
        if (preview.Problems.Count > 0) return CommandResult.Fail("The configuration can't be applied: " + string.Join("; ", preview.Problems));

        Keep(backup);
        Mutate(() =>
        {
            int id = 1;
            _rules.Clear();
            foreach (var rule in fitted.ProcessRules) { rule.Id = id++; _rules.Add(rule); }
            id = 1;
            _watchdog.Clear();
            foreach (var rule in fitted.WatchdogRules) { rule.Id = id++; _watchdog.Add(rule); }
            _settings = fitted.Settings;
            MarkRuledProcesses();
        });
        Log(ActivityKind.Manual, null, activity);
        preview.Applied = true;
        preview.BackupName = backup;
        return Data(preview);
    }

    private static bool IsAutomatic(string name) =>
        string.Equals(name, BeforeSwitch, StringComparison.OrdinalIgnoreCase) || string.Equals(name, BeforeImport, StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves what is in force as <paramref name="name"/>, replacing a profile of that name.</summary>
    private StandInProfile Keep(string name)
    {
        var existing = Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var profile = new StandInProfile
        {
            Id = existing?.Id ?? (Profiles.Count == 0 ? 1 : Profiles.Max(p => p.Id) + 1),
            Name = name, SavedUtc = DateTime.UtcNow, Document = Capture(name),
        };
        Mutate(() =>
        {
            if (existing != null) Profiles.Remove(existing);
            Profiles.Add(profile);
        });
        return profile;
    }

    private ConfigDocument Capture(string name) => new()
    {
        Name = name,
        SavedUtc = DateTime.UtcNow,
        Machine = DescribeMachine(),
        ProcessRules = _rules.Select(r => { var copy = Copy(r); copy.Id = 0; return copy; }).ToList(),
        WatchdogRules = _watchdog.Select(r => { var copy = Copy(r); copy.Id = 0; return copy; }).ToList(),
        Settings = Copy(_settings),
    };

    private MachineDescription DescribeMachine() => new()
    {
        LogicalProcessors = StandInData.Logical,
        CpuSetLayout = ConfigDocuments.CpuSetLayout(_cpuSets.Select(s => ((uint)s.Id, s.CoreIndex, s.EfficiencyClass))),
        PowerPlans = _plans.Select(p => new PowerPlanDto { Id = p.Id, Name = p.Name }).ToList(),
    };

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private static ConfigProfileDto ToDto(StandInProfile p) => new()
    {
        Id = p.Id, Name = p.Name, SavedUtc = p.SavedUtc, Automatic = IsAutomatic(p.Name),
        ProcessRuleCount = p.Document.ProcessRules.Count, WatchdogRuleCount = p.Document.WatchdogRules.Count,
    };

    /// <summary>A few profiles to switch between: one for games, a quieter one, and one brought over from another PC.</summary>
    private List<StandInProfile> SeedProfiles()
    {
        var now = DateTime.UtcNow;
        var gaming = Capture("Gaming");
        gaming.SavedUtc = now.AddDays(-12);
        gaming.Settings.ProBalanceSensitivity = 8;
        gaming.Settings.PerformanceModeEngageForSteam = true;

        var quiet = Capture("Quiet work");
        quiet.SavedUtc = now.AddDays(-3);
        quiet.ProcessRules.RemoveAll(r => r.PriorityClass is 128 or 256);
        quiet.Settings.SmartTrimEnabled = true;
        quiet.Settings.ProBalanceSensitivity = 4;

        // Saved on a 32-thread desktop, with a power plan this machine hasn't got.
        var desktop = Capture("Old desktop");
        desktop.SavedUtc = now.AddDays(-90);
        var studio = new PowerPlanDto { Id = Guid.Parse("5c1f0e9a-7d2b-4c8e-9f3a-1b6d2e4a8c71"), Name = "Studio render" };
        desktop.Machine = new MachineDescription { LogicalProcessors = 32, CpuSetLayout = "256:0:0,257:0:0", PowerPlans = { studio } };
        desktop.ProcessRules.Add(new ProcessRuleDto { ProcessName = "render.exe", PriorityClass = 16384, AffinityMask = 0xFFFF0000L, CpuSetIds = "256,257" });
        desktop.Settings.PerformanceModePlanGuid = studio.Id.ToString();

        return new List<StandInProfile>
        {
            new() { Id = 1, Name = gaming.Name, SavedUtc = gaming.SavedUtc, Document = gaming },
            new() { Id = 2, Name = quiet.Name, SavedUtc = quiet.SavedUtc, Document = quiet },
            new() { Id = 3, Name = desktop.Name, SavedUtc = desktop.SavedUtc, Document = desktop },
        };
    }
}
