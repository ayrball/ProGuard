using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.Shared.Profiles;
using ActivityKind = ProGuard.Shared.Contracts.ActivityKind; // System.Diagnostics has one too

namespace ProGuard.Engine.Services;

/// <summary>
/// Config profiles: named copies of the process rules, Watchdog rules and settings that can be switched between,
/// exported as a file and imported from one.
/// <list type="bullet">
/// <item>A switch or import replaces all three in one database transaction (<see cref="IRuleStore.ReplaceConfiguration"/>),
/// after saving what was in force as "Before switch" or "Before import", so it can be undone by switching back. The new
/// rules are then put into effect on running processes the way saving a rule does (<see cref="RuleService.ApplyReplaced"/>)
/// and the settings through <see cref="SettingsApplier"/>.</item>
/// <item>Before anything is replaced, every rule the caller would add, change or remove goes through
/// <see cref="RuleAuthorization"/> as the caller, and the settings through <see cref="IpcAuthorization.CheckSettingsChange"/>,
/// exactly as if they had saved each one. Any refusal stops the whole switch: a standard user can't import a rule they
/// couldn't have created, nor drop an administrator's rule by switching to a profile without it.</item>
/// <item>Values that only work on the machine they were saved on are left out and reported
/// (<see cref="ConfigDocuments.FitToMachine"/>).</item>
/// <item>Saving, listing, exporting, renaming and deleting profiles are open to any user: a profile changes nothing
/// until it is applied, and applying it is what is checked.</item>
/// </list>
/// Switch and import change settings, so the dispatcher calls them under its settings lock.
/// </summary>
public sealed class ConfigProfileService
{
    public const string BeforeSwitch = "Before switch";
    public const string BeforeImport = "Before import";
    public const int MaxProfiles = 50;
    public const int MaxNameLength = 60;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<ConfigProfileService> _logger;
    private readonly IRuleStore _store;
    private readonly IConfigProfileStore _profiles;
    private readonly RuleService _rules;
    private readonly SettingsApplier _applier;
    private readonly IProcessMonitor _monitor;
    private readonly IPowerManager _power;
    private readonly IProcessTuningNative _native;
    private readonly ActivityLog _activity;
    private readonly Func<int> _logicalProcessors;

    public ConfigProfileService(
        ILogger<ConfigProfileService> logger,
        IRuleStore store,
        IConfigProfileStore profiles,
        RuleService rules,
        SettingsApplier applier,
        IProcessMonitor monitor,
        IPowerManager power,
        IProcessTuningNative native,
        ActivityLog activity,
        Func<int>? logicalProcessors = null)
    {
        _logger = logger;
        _store = store;
        _profiles = profiles;
        _rules = rules;
        _applier = applier;
        _monitor = monitor;
        _power = power;
        _native = native;
        _activity = activity;
        _logicalProcessors = logicalProcessors ?? (() => Environment.ProcessorCount);
    }

    public static bool IsAutomatic(string name) =>
        string.Equals(name, BeforeSwitch, StringComparison.OrdinalIgnoreCase) || string.Equals(name, BeforeImport, StringComparison.OrdinalIgnoreCase);

    // === Listing and bookkeeping ===

    public CommandResult List() => CommandResult.Ok(JsonSerializer.Serialize(_profiles.GetProfiles().Select(ToDto).ToList()));

    public CommandResult Save(string? name)
    {
        name = name?.Trim() ?? string.Empty;
        if (CheckName(name) is { } bad) return CommandResult.Fail(bad);
        if (IsAutomatic(name)) return CommandResult.Fail($"\"{name}\" is kept for the copy ProGuard saves before a switch. Pick another name.");
        var all = _profiles.GetProfiles();
        if (all.Count(p => !IsAutomatic(p.Name)) >= MaxProfiles && all.All(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail($"There are already {MaxProfiles} profiles. Delete one first.");

        var saved = _profiles.Save(ToProfile(Capture(name)));
        _activity.Add(ActivityKind.Manual, string.Empty, 0, $"Saved the configuration as the {saved.Name} profile.");
        return CommandResult.Ok(JsonSerializer.Serialize(ToDto(saved)));
    }

    public CommandResult Rename(RenameProfileArgument argument)
    {
        var name = argument.Name?.Trim() ?? string.Empty;
        if (CheckName(name) is { } bad) return CommandResult.Fail(bad);
        if (IsAutomatic(name)) return CommandResult.Fail($"\"{name}\" is kept for the copy ProGuard saves before a switch. Pick another name.");
        var profile = _profiles.GetProfile(argument.Id);
        if (profile == null) return CommandResult.Fail("That profile no longer exists. Refresh the list and try again.");
        if (_profiles.GetProfiles().Any(p => p.Id != profile.Id && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail($"There is already a profile called {name}.");
        if (!_profiles.Rename(profile.Id, name)) return CommandResult.Fail("That profile no longer exists. Refresh the list and try again.");
        profile.Name = name;
        return CommandResult.Ok(JsonSerializer.Serialize(ToDto(profile)));
    }

    public CommandResult Delete(int id)
    {
        var profile = _profiles.GetProfile(id);
        if (profile == null || !_profiles.Delete(id)) return CommandResult.Fail("That profile no longer exists. Refresh the list and try again.");
        _activity.Add(ActivityKind.Manual, string.Empty, 0, $"Deleted the {profile.Name} profile.");
        return CommandResult.Ok();
    }

    /// <summary>A profile, or the configuration in force when <paramref name="argument"/> is empty, as a document.</summary>
    public CommandResult Export(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return CommandResult.Ok(JsonSerializer.Serialize(Capture(ConfigDocuments.CurrentName)));
        if (!int.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return CommandResult.Fail($"Expected a profile id, got '{argument}'");
        if (Load(id, out var document) is { } failed) return failed;
        return CommandResult.Ok(JsonSerializer.Serialize(document));
    }

    // === Switching and importing ===

    /// <summary>Call under the dispatcher's settings lock.</summary>
    public CommandResult Switch(SwitchProfileArgument argument, IIpcCaller caller)
    {
        if (Load(argument.Id, out var document) is { } failed) return failed;
        return PreviewOrApply(document!, argument.Preview, caller, BeforeSwitch, $"Switched to the {document!.Name} profile.");
    }

    /// <summary>Call under the dispatcher's settings lock.</summary>
    public CommandResult Import(ImportConfigArgument argument, IIpcCaller caller)
    {
        var document = ConfigDocuments.Parse(argument.Document, out var error);
        if (document == null) return CommandResult.Fail(error!);
        var label = string.IsNullOrWhiteSpace(document.Name) ? "a config file" : $"the {document.Name.Trim()} config";
        return PreviewOrApply(document, argument.Preview, caller, BeforeImport, $"Imported {label}.");
    }

    private CommandResult PreviewOrApply(ConfigDocument document, bool previewOnly, IIpcCaller caller, string backupName, string activity)
    {
        var (preview, plan) = Prepare(document, caller);
        if (previewOnly) return CommandResult.Ok(JsonSerializer.Serialize(preview));

        if (preview.Refusals.Count > 0) return CommandResult.Fail(preview.Refusals[0]);
        if (preview.Problems.Count > 0) return CommandResult.Fail("The configuration can't be applied: " + string.Join("; ", preview.Problems));

        // The configuration in force, kept first so the switch can be undone. Saved before anything is replaced: if the
        // replacement fails, it is simply a copy of what is still in force.
        _profiles.Save(ToProfile(Capture(backupName)));

        var previous = _store.GetProcessRules().ToList();
        _store.ReplaceConfiguration(plan!.ProcessRules, plan.WatchdogRules, plan.Settings);
        _rules.ApplyReplaced(previous, plan.ProcessRules);
        _applier.Apply(plan.Settings);

        _logger.LogInformation("{Activity} ({Rules} process rules, {Watchdog} Watchdog rules) by {Caller}", activity, plan.ProcessRules.Count, plan.WatchdogRules.Count, caller.Name);
        _activity.Add(ActivityKind.Manual, string.Empty, 0, activity);
        preview.Applied = true;
        preview.BackupName = backupName;
        return CommandResult.Ok(JsonSerializer.Serialize(preview));
    }

    private sealed record Plan(List<ProcessRule> ProcessRules, List<WatchdogRule> WatchdogRules, AppSettings Settings);

    /// <summary>What applying <paramref name="document"/> would change, and the rules and settings it would store.</summary>
    private (ConfigPreviewDto Preview, Plan? Plan) Prepare(ConfigDocument document, IIpcCaller caller)
    {
        var current = Capture(ConfigDocuments.CurrentName);
        var fitted = ConfigDocuments.FitToMachine(document, current.Machine!, out var flags);
        var preview = new ConfigPreviewDto
        {
            Name = document.Name,
            SchemaVersion = document.SchemaVersion,
            SavedUtc = document.SavedUtc,
            Changes = ConfigDocuments.Diff(current, fitted),
            MachineSpecific = flags,
        };

        // Is it valid?
        foreach (var rule in fitted.ProcessRules)
            if (RuleService.Validate(rule) is { } problem) preview.Problems.Add($"Process rule {rule.ProcessName}: {problem}");
        foreach (var group in fitted.ProcessRules.GroupBy(r => ConfigDocuments.RuleKey(r.ProcessName)).Where(g => g.Count() > 1 && g.Key.Length > 0))
            preview.Problems.Add($"There is more than one process rule for {group.First().ProcessName}.");
        foreach (var rule in fitted.WatchdogRules)
            if (RuleService.Validate(rule) is { } problem) preview.Problems.Add($"Watchdog rule {rule.TargetProcessName}: {problem}");
        if (IpcDispatcher.ValidateSettings(fitted.Settings) is { } invalid) preview.Problems.Add("Settings: " + invalid);
        if (preview.Problems.Count > 0) return (preview, null);

        var plan = new Plan(fitted.ProcessRules.Select(ToModel).ToList(), fitted.WatchdogRules.Select(ToModel).ToList(), ToModel(fitted.Settings));

        // May the caller make every change? Rules that stay exactly as they are aren't changes.
        var refusals = new List<string>();
        var incoming = fitted.ProcessRules.ToDictionary(r => ConfigDocuments.RuleKey(r.ProcessName));
        var existing = current.ProcessRules.ToDictionary(r => ConfigDocuments.RuleKey(r.ProcessName));
        var stored = _store.GetProcessRules().ToDictionary(r => ProcessExtensions.NormalizeName(r.ProcessName));
        foreach (var (key, dto) in existing)
        {
            if (incoming.TryGetValue(key, out var same) && ConfigDocuments.SameRule(dto, same)) continue;
            if (stored.TryGetValue(key, out var rule) && RuleAuthorization.CheckProcessRule(rule, caller, _monitor) is { } denied) refusals.Add(denied);
        }
        foreach (var rule in plan.ProcessRules)
        {
            var key = ConfigDocuments.RuleKey(rule.ProcessName);
            if (existing.TryGetValue(key, out var same) && ConfigDocuments.SameRule(same, incoming[key])) continue;
            if (RuleAuthorization.CheckProcessRule(rule, caller, _monitor) is { } denied) refusals.Add(denied);
        }

        var keptWatchdog = _store.GetWatchdogRules().Select(r => (Rule: r, Key: ConfigDocuments.WatchdogKey(DtoMapper.Map<WatchdogRule, WatchdogRuleDto>(r)))).ToList();
        var newWatchdog = plan.WatchdogRules.Select(r => (Rule: r, Key: ConfigDocuments.WatchdogKey(DtoMapper.Map<WatchdogRule, WatchdogRuleDto>(r)))).ToList();
        var unmatchedOld = keptWatchdog.ToList();
        foreach (var rule in newWatchdog)
        {
            var match = unmatchedOld.FindIndex(o => o.Key == rule.Key);
            if (match >= 0) { unmatchedOld.RemoveAt(match); continue; }
            if (RuleAuthorization.CheckWatchdogRule(rule.Rule, caller, _monitor) is { } denied) refusals.Add(denied);
        }
        foreach (var (rule, _) in unmatchedOld)
            if (RuleAuthorization.CheckWatchdogRule(rule, caller, _monitor) is { } denied) refusals.Add(denied);

        if (IpcAuthorization.CheckSettingsChange(_store.GetSettings(), plan.Settings, caller) is { } settingsDenied) preview.Refusals.Add(settingsDenied.Error!);
        preview.Refusals.InsertRange(0, refusals.Distinct().Select(what => IpcAuthorization.Deny(caller, what).Error!));
        return (preview, plan);
    }

    // === The configuration as a document ===

    /// <summary>The configuration in force, as a document named <paramref name="name"/>.</summary>
    internal ConfigDocument Capture(string name) => new()
    {
        Name = name,
        SavedUtc = DateTime.UtcNow,
        Machine = DescribeMachine(),
        ProcessRules = _store.GetProcessRules()
            .OrderBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(r => { var dto = RuleDtoMapper.ToDto(r); dto.Id = 0; return dto; }).ToList(),
        WatchdogRules = _store.GetWatchdogRules()
            .OrderBy(r => r.Id)
            .Select(r => { var dto = DtoMapper.Map<WatchdogRule, WatchdogRuleDto>(r); dto.Id = 0; return dto; }).ToList(),
        Settings = DtoMapper.Map<AppSettings, EngineSettingsDto>(_store.GetSettings()),
    };

    internal MachineDescription DescribeMachine()
    {
        var sets = _native.GetSystemCpuSets();
        return new MachineDescription
        {
            LogicalProcessors = sets.Count > 0 ? sets.Count : Math.Max(1, _logicalProcessors()),
            CpuSetLayout = sets.Count == 0 ? null : ConfigDocuments.CpuSetLayout(sets.Select(s => (s.Id, (int)s.CoreIndex, (int)s.EfficiencyClass))),
            PowerPlans = _power.GetAvailablePlans().Select(p => new PowerPlanDto { Id = p.Id, Name = p.Name }).ToList(),
        };
    }

    private CommandResult? Load(int id, out ConfigDocument? document)
    {
        document = null;
        var profile = _profiles.GetProfile(id);
        if (profile == null) return CommandResult.Fail("That profile no longer exists. Refresh the list and try again.");
        document = ConfigDocuments.Parse(profile.Document, out var error);
        if (document == null) return CommandResult.Fail($"The {profile.Name} profile can't be read: {error}");
        document.Name = profile.Name;
        return null;
    }

    private static ConfigProfile ToProfile(ConfigDocument document) => new()
    {
        Name = document.Name,
        SavedUtc = document.SavedUtc,
        Document = ConfigDocuments.Serialize(document),
        ProcessRuleCount = document.ProcessRules.Count,
        WatchdogRuleCount = document.WatchdogRules.Count,
    };

    private static ConfigProfileDto ToDto(ConfigProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        SavedUtc = DateTime.SpecifyKind(profile.SavedUtc, DateTimeKind.Utc),
        ProcessRuleCount = profile.ProcessRuleCount,
        WatchdogRuleCount = profile.WatchdogRuleCount,
        Automatic = IsAutomatic(profile.Name),
    };

    private static ProcessRule ToModel(ProcessRuleDto dto)
    {
        var rule = new ProcessRule();
        RuleDtoMapper.CopyTo(dto, rule);
        rule.Id = 0;
        rule.ProcessName = dto.ProcessName.Trim();
        return rule;
    }

    private static WatchdogRule ToModel(WatchdogRuleDto dto)
    {
        var rule = DtoMapper.Map<WatchdogRuleDto, WatchdogRule>(dto);
        rule.Id = 0;
        rule.TargetProcessName = dto.TargetProcessName.Trim();
        rule.ActionArgument = string.IsNullOrWhiteSpace(dto.ActionArgument) ? null : dto.ActionArgument.Trim();
        return rule;
    }

    private AppSettings ToModel(EngineSettingsDto dto)
    {
        var settings = _store.GetSettings().Clone();
        DtoMapper.CopyTo(dto, settings); // never touches Id: the DTO has none
        return settings;
    }

    private static string? CheckName(string name)
    {
        if (name.Length == 0) return "A profile needs a name.";
        if (name.Length > MaxNameLength) return $"A profile name can be at most {MaxNameLength} characters.";
        if (name.Any(char.IsControl)) return "A profile name can't contain control characters.";
        return null;
    }
}
