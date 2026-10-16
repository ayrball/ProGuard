using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.Shared.Profiles;

namespace ProGuard.Tests;

/// <summary>Keeps profiles in memory, as the database would.</summary>
internal sealed class FakeProfileStore : IConfigProfileStore
{
    private readonly List<ConfigProfile> _profiles = new();
    private int _nextId = 1;

    public IReadOnlyList<ConfigProfile> GetProfiles() => _profiles.Select(Copy).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public ConfigProfile? GetProfile(int id) => _profiles.Where(p => p.Id == id).Select(Copy).FirstOrDefault();

    public ConfigProfile Save(ConfigProfile profile)
    {
        var existing = _profiles.FirstOrDefault(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        profile.Id = existing?.Id ?? _nextId++;
        if (existing != null) _profiles.Remove(existing);
        _profiles.Add(Copy(profile));
        return profile;
    }

    public bool Rename(int id, string name)
    {
        var p = _profiles.FirstOrDefault(x => x.Id == id);
        if (p == null) return false;
        p.Name = name;
        return true;
    }

    public bool Delete(int id) => _profiles.RemoveAll(p => p.Id == id) > 0;

    public ConfigProfile? Named(string name) => _profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static ConfigProfile Copy(ConfigProfile p) => new()
    {
        Id = p.Id, Name = p.Name, SavedUtc = p.SavedUtc, Document = p.Document, ProcessRuleCount = p.ProcessRuleCount, WatchdogRuleCount = p.WatchdogRuleCount,
    };
}

/// <summary>
/// <see cref="ThinHarness"/>'s machine with a dispatcher that also has config profiles. The rule store mock replaces its
/// rules, Watchdog rules and settings together, as <see cref="RuleStore.ReplaceConfiguration"/> does.
/// </summary>
internal sealed class ProfileHarness
{
    public ThinHarness T { get; } = new();
    public FakeProfileStore Profiles { get; } = new();
    public IpcDispatcher Dispatcher { get; }
    public int Replacements { get; private set; }
    private int _nextId = 500;

    public ProfileHarness()
    {
        T.Store.Setup(s => s.ReplaceConfiguration(It.IsAny<IReadOnlyList<ProcessRule>>(), It.IsAny<IReadOnlyList<WatchdogRule>>(), It.IsAny<AppSettings>()))
            .Callback<IReadOnlyList<ProcessRule>, IReadOnlyList<WatchdogRule>, AppSettings>((rules, watchdog, settings) =>
            {
                Replacements++;
                T.Rules.Clear();
                foreach (var r in rules) { r.Id = _nextId++; T.Rules.Add(r); }
                T.WatchdogRules.Clear();
                foreach (var r in watchdog) { r.Id = _nextId++; T.WatchdogRules.Add(r); }
                T.Store.Object.UpdateSettings(settings);
            });

        var applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, T.Monitor, T.SmartTrim.Object, T.Power.Object, T.ProBalance.Object,
            foregroundBoost: T.ForegroundBoost, timerResolution: T.TimerResolution);
        var rules = new RuleService(NullLogger<RuleService>.Instance, T.Store.Object, T.Monitor, T.CpuLimiter.Object, T.ProBalance.Object,
            T.Access.Object, T.Manual, launchPriorities: new LaunchPriorityService(registry: T.LaunchRegistry));
        var actions = new ProcessActionService(NullLogger<ProcessActionService>.Instance, T.Tuning, T.Monitor, T.Controller.Object);
        var thin = new ThinClientCommands(NullLogger<ThinClientCommands>.Instance, T.Snapshots, actions, rules,
            new ActivityFeed(T.Store.Object, T.Manual, T.WatchdogEvents), T.Manual, T.Store.Object, T.Power.Object, T.Monitor, T.Tuning);
        var profiles = new ConfigProfileService(NullLogger<ConfigProfileService>.Instance, T.Store.Object, Profiles, rules, applier,
            T.Monitor, T.Power.Object, T.Tuning, T.Manual);
        Dispatcher = new IpcDispatcher(T.Log, T.Store.Object, applier, T.ProBalance.Object, T.Power.Object, T.SmartTrim.Object,
            T.CpuLimiter.Object, T.Controller.Object, T.Monitor, T.WatchdogEvents, thin, timerResolution: T.TimerResolution, profiles: profiles);
    }

    public CommandResult Send(IIpcCaller caller, string command, object? argument = null)
    {
        var text = argument switch
        {
            null => null,
            string s => s,
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(argument),
        };
        var reply = Dispatcher.Dispatch(new CommandMessage { CommandType = command, Argument = text }, caller);
        Assert.Equal(MessageType.CommandResult, reply.Type);
        return JsonSerializer.Deserialize<CommandResult>(reply.Payload)!;
    }

    public CommandResult Admin(string command, object? argument = null) => Send(ThinCaller.Admin(), command, argument);

    public T2 Data<T2>(CommandResult result) => T.Data<T2>(result);

    public ConfigDocument Export(int? id = null) => Data<ConfigDocument>(Admin(CommandTypes.ExportConfig, id?.ToString()));

    public int SaveProfile(string name) => Data<ConfigProfileDto>(Admin(CommandTypes.SaveProfile, name)).Id;

    public void AddRule(string name, Action<ProcessRule>? set = null)
    {
        var rule = new ProcessRule { ProcessName = name, PriorityClass = (int)ProcessPriorityClass.BelowNormal };
        set?.Invoke(rule);
        T.Store.Object.SaveProcessRule(rule);
    }

    public void AddWatchdog(string name, WatchdogAction action = WatchdogAction.LogAlert, string? argument = null) =>
        T.Store.Object.AddWatchdogRule(new WatchdogRule
        {
            TargetProcessName = name, IsEnabled = true, ConditionType = WatchdogCondition.CpuAbove, ThresholdValue = 80,
            DurationSeconds = 10, ActionType = action, ActionArgument = argument,
        });

    public void Settings(Action<AppSettings> change)
    {
        var s = T.Stored.Clone();
        change(s);
        T.Store.Object.UpdateSettings(s);
    }
}

public class ConfigProfileTests
{
    private static readonly ThinCaller Standard = new();

    // ------------------------------------------------------------------ export and import

    [Fact]
    public void ExportThenImport_RestoresRulesWatchdogRulesAndSettings()
    {
        var h = new ProfileHarness();
        h.AddRule("game.exe", r => { r.PriorityClass = (int)ProcessPriorityClass.High; r.AffinityMask = 0x3; r.IoPriority = 3; r.EfficiencyMode = false; });
        h.AddRule("chrome", r => { r.CpuLimitPercent = 40; r.ExcludeFromProBalance = true; });
        h.AddWatchdog("leaky.exe", WatchdogAction.TrimWorkingSet, "continuous");
        h.AddWatchdog("hog.exe", WatchdogAction.SetAffinity, "0x1");
        h.Settings(s => { s.ProBalanceSensitivity = 8; s.SmartTrimEnabled = true; s.SmartTrimExclusions = "game.exe"; s.IdlePowerPlanGuid = Powrprof.PowerSaver.ToString(); });

        var exported = h.Export();
        var file = ConfigDocuments.Serialize(exported);
        Assert.Contains("\"Format\": \"ProGuard.Config\"", file);
        Assert.Contains("\"SchemaVersion\": 1", file);
        Assert.Contains("\"TrimWorkingSet\"", file); // enums by name in the file

        // Somebody wipes everything.
        h.T.Rules.Clear();
        h.T.WatchdogRules.Clear();
        h.Settings(s => { s.ProBalanceSensitivity = 3; s.SmartTrimEnabled = false; s.SmartTrimExclusions = null; s.IdlePowerPlanGuid = null; });

        var preview = h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.ImportConfig, new ImportConfigArgument { Document = file, Preview = true }));
        Assert.False(preview.Applied);
        Assert.True(preview.CanApply);
        Assert.Empty(h.T.Rules); // a preview changes nothing
        Assert.Contains(preview.Changes, c => c.Kind == ConfigChangeKind.Added && c.Area == "Process rule" && c.Name == "game.exe");
        Assert.Contains(preview.Changes, c => c.Kind == ConfigChangeKind.Added && c.Area == "Watchdog rule" && c.Name == "leaky.exe");
        Assert.Contains(preview.Changes, c => c.Area == "Setting" && c.Name == "ProBalance sensitivity" && c.Detail == "3 → 8");
        Assert.Empty(preview.MachineSpecific);

        var applied = h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.ImportConfig, new ImportConfigArgument { Document = file }));
        Assert.True(applied.Applied);
        Assert.Equal(ConfigProfileService.BeforeImport, applied.BackupName);

        var again = h.Export();
        Assert.Equal(Comparable(exported), Comparable(again));
        Assert.Equal(8, h.T.Stored.ProBalanceSensitivity);
        Assert.Equal(1, h.T.Stored.Id); // the stored settings row keeps its key
        Assert.NotNull(h.Profiles.Named(ConfigProfileService.BeforeImport));
    }

    /// <summary>Rules, Watchdog rules and settings as JSON, without what differs between two exports of the same thing.</summary>
    private static string Comparable(ConfigDocument d) => JsonSerializer.Serialize(new { d.ProcessRules, d.WatchdogRules, d.Settings });

    [Fact]
    public void Import_OfTheSameConfiguration_ChangesNothing()
    {
        var h = new ProfileHarness();
        h.AddRule("game.exe");
        h.AddWatchdog("hog.exe");
        var file = ConfigDocuments.Serialize(h.Export());

        var preview = h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.ImportConfig, new ImportConfigArgument { Document = file, Preview = true }));

        Assert.Empty(preview.Changes);
    }

    [Theory]
    [InlineData("""{"Format":"ProGuard.Config","SchemaVersion":2,"ProcessRules":"a shape this build doesn't know"}""", "newer version of ProGuard")]
    [InlineData("""{"Format":"Something.Else","SchemaVersion":1}""", "isn't a ProGuard config")]
    [InlineData("""{"SchemaVersion":1}""", "isn't a ProGuard config")]
    [InlineData("""{"Format":"ProGuard.Config"}""", "no valid schema version")]
    [InlineData("""{"Format":"ProGuard.Config","SchemaVersion":0}""", "no valid schema version")]
    [InlineData("""{"Format":"ProGuard.Config","SchemaVersion":"1"}""", "no valid schema version")]
    [InlineData("not json at all", "isn't valid JSON")]
    [InlineData("", "empty")]
    [InlineData("""{"Format":"ProGuard.Config","SchemaVersion":1,"ProcessRules":{"oops":1}}""", "damaged")]
    public void Import_RefusesWhatItCantRead(string text, string expected)
    {
        var h = new ProfileHarness();
        h.AddRule("game.exe");

        var result = h.Admin(CommandTypes.ImportConfig, new ImportConfigArgument { Document = text, Preview = true });

        Assert.False(result.Success);
        Assert.Contains(expected, result.Error);
        Assert.Single(h.T.Rules);
        Assert.Equal(0, h.Replacements);
    }

    [Fact]
    public void Parse_ReadsVersionOne_WithEnumsAsNamesOrNumbers_AndIgnoresUnknownFields()
    {
        var text = """
            {
              "format": "ProGuard.Config", "schemaVersion": 1, "Name": "Hand made", "SomethingNew": [1, 2, 3],
              "ProcessRules": [ { "Id": 77, "ProcessName": "game.exe", "PriorityClass": 128 } ],
              "WatchdogRules": [ { "TargetProcessName": "a.exe", "ConditionType": "RamAbove", "ActionType": 6, "ThresholdValue": 500 } ]
            }
            """;

        var document = ConfigDocuments.Parse(text, out var error);

        Assert.Null(error);
        Assert.Equal(ConfigDocument.CurrentSchemaVersion, document!.SchemaVersion);
        Assert.Equal(0, document.ProcessRules.Single().Id); // ids from elsewhere mean nothing here
        Assert.Equal(WatchdogTrigger.RamAbove, document.WatchdogRules.Single().ConditionType);
        Assert.Equal(WatchdogResponse.TrimWorkingSet, document.WatchdogRules.Single().ActionType);
        Assert.NotNull(document.Settings); // absent: the defaults
    }

    [Fact]
    public void Parse_RefusesADocumentTooLargeToBeAConfig()
    {
        var text = new string(' ', ConfigDocuments.MaxDocumentLength + 1);
        Assert.Null(ConfigDocuments.Parse("{" + text + "}", out var error));
        Assert.Contains("too large", error);
    }

    [Fact]
    public void Import_WithInvalidRules_ListsTheProblems_AndAppliesNothing()
    {
        var h = new ProfileHarness();
        var doc = h.Export();
        doc.ProcessRules.Add(new ProcessRuleDto { ProcessName = "a.exe", CpuLimitPercent = 150 });
        doc.ProcessRules.Add(new ProcessRuleDto { ProcessName = "b.exe" });
        doc.ProcessRules.Add(new ProcessRuleDto { ProcessName = "B" });
        doc.Settings.ProBalanceSensitivity = 42;
        var file = ConfigDocuments.Serialize(doc);

        var preview = h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.ImportConfig, new ImportConfigArgument { Document = file, Preview = true }));
        Assert.False(preview.CanApply);
        Assert.Contains(preview.Problems, p => p.Contains("a.exe") && p.Contains("CPU limit"));
        Assert.Contains(preview.Problems, p => p.Contains("more than one process rule for b.exe"));
        Assert.Contains(preview.Problems, p => p.StartsWith("Settings: Sensitivity"));

        var result = h.Admin(CommandTypes.ImportConfig, new ImportConfigArgument { Document = file });
        Assert.False(result.Success);
        Assert.Equal(0, h.Replacements);
        Assert.Null(h.Profiles.Named(ConfigProfileService.BeforeImport));
    }

    // ------------------------------------------------------------------ profiles and switching

    [Fact]
    public void Switch_ReplacesEverythingAtOnce_AfterSavingBeforeSwitch_WhichUndoesIt()
    {
        var h = new ProfileHarness();
        h.AddRule("game.exe", r => r.PriorityClass = (int)ProcessPriorityClass.High);
        h.AddWatchdog("hog.exe");
        h.Settings(s => s.ProBalanceSensitivity = 9);
        var gaming = h.SaveProfile("Gaming");

        h.T.Rules.Clear();
        h.AddRule("backup.exe", r => r.PriorityClass = (int)ProcessPriorityClass.Idle);
        h.T.WatchdogRules.Clear();
        h.Settings(s => s.ProBalanceSensitivity = 2);
        var work = Comparable(h.Export());

        var switched = h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.SwitchProfile, new SwitchProfileArgument { Id = gaming }));

        Assert.True(switched.Applied);
        Assert.Equal(1, h.Replacements); // one replacement, not a rule at a time
        Assert.Equal(new[] { "game.exe" }, h.T.Rules.Select(r => r.ProcessName));
        Assert.Equal(new[] { "hog.exe" }, h.T.WatchdogRules.Select(r => r.TargetProcessName));
        Assert.Equal(9, h.T.Stored.ProBalanceSensitivity);
        h.T.ProBalance.Verify(p => p.SetSensitivity(9)); // applied through the SettingsApplier
        Assert.Contains(switched.Changes, c => c.Kind == ConfigChangeKind.Removed && c.Name == "backup.exe");

        // What was in force was kept, and switching to it undoes the switch.
        var list = h.Data<List<ConfigProfileDto>>(h.Admin(CommandTypes.ListProfiles));
        var before = list.Single(p => p.Name == ConfigProfileService.BeforeSwitch);
        Assert.True(before.Automatic);
        Assert.Equal(1, before.ProcessRuleCount);
        Assert.False(list.Single(p => p.Name == "Gaming").Automatic);

        h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.SwitchProfile, new SwitchProfileArgument { Id = before.Id }));
        Assert.Equal(work, Comparable(h.Export()));
        Assert.Equal(2, h.T.Stored.ProBalanceSensitivity);

        // ...and that switch saved Gaming's state as the new "Before switch", so undo works both ways.
        var undoAgain = h.Data<List<ConfigProfileDto>>(h.Admin(CommandTypes.ListProfiles)).Single(p => p.Name == ConfigProfileService.BeforeSwitch);
        Assert.Equal(Comparable(h.Export(gaming)), Comparable(h.Export(undoAgain.Id)));
    }

    [Fact]
    public void Switch_PreviewChangesNothing()
    {
        var h = new ProfileHarness();
        var empty = h.SaveProfile("Empty");
        h.AddRule("game.exe");

        var preview = h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.SwitchProfile, new SwitchProfileArgument { Id = empty, Preview = true }));

        Assert.False(preview.Applied);
        Assert.Contains(preview.Changes, c => c.Kind == ConfigChangeKind.Removed && c.Name == "game.exe");
        Assert.Single(h.T.Rules);
        Assert.Null(h.Profiles.Named(ConfigProfileService.BeforeSwitch));
    }

    [Fact]
    public void Switch_PutsTheRulesIntoEffectOnRunningProcesses()
    {
        var h = new ProfileHarness();
        h.AddRule("game.exe", r => r.PriorityClass = (int)ProcessPriorityClass.High);
        var gaming = h.SaveProfile("Gaming");
        h.T.Rules.Clear();
        h.T.Process(4242, "game.exe");

        h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.SwitchProfile, new SwitchProfileArgument { Id = gaming }));

        h.T.Access.Verify(a => a.SetPriorityClass(4242, ProcessPriorityClass.High));
    }

    [Fact]
    public void Profiles_SaveRenameDelete_WithNameRules()
    {
        var h = new ProfileHarness();
        var id = h.SaveProfile("Work");
        Assert.Equal(id, h.SaveProfile("work")); // same name, any case: replaced, with the new spelling
        Assert.Single(h.Data<List<ConfigProfileDto>>(h.Admin(CommandTypes.ListProfiles)));

        Assert.Contains("kept for the copy", h.Admin(CommandTypes.SaveProfile, "before switch").Error);
        Assert.Contains("needs a name", h.Admin(CommandTypes.SaveProfile, "   ").Error);
        Assert.Contains("at most", h.Admin(CommandTypes.SaveProfile, new string('x', 61)).Error);

        var other = h.SaveProfile("Play");
        Assert.Contains("already a profile called", h.Admin(CommandTypes.RenameProfile, new RenameProfileArgument { Id = other, Name = "WORK" }).Error);
        Assert.Equal("Games", h.Data<ConfigProfileDto>(h.Admin(CommandTypes.RenameProfile, new RenameProfileArgument { Id = other, Name = " Games " })).Name);

        Assert.True(h.Admin(CommandTypes.DeleteProfile, other).Success);
        Assert.False(h.Admin(CommandTypes.DeleteProfile, other).Success);
        Assert.Equal(new[] { "work" }, h.Data<List<ConfigProfileDto>>(h.Admin(CommandTypes.ListProfiles)).Select(p => p.Name));
    }

    // ------------------------------------------------------------------ standard users

    [Fact]
    public void StandardUser_MaySaveListExportRenameAndDelete()
    {
        var h = new ProfileHarness();
        h.AddRule("game.exe");

        var saved = h.Data<ConfigProfileDto>(h.Send(Standard, CommandTypes.SaveProfile, "Mine"));
        Assert.Single(h.Data<List<ConfigProfileDto>>(h.Send(Standard, CommandTypes.ListProfiles)));
        Assert.Single(h.Data<ConfigDocument>(h.Send(Standard, CommandTypes.ExportConfig)).ProcessRules);
        Assert.Single(h.Data<ConfigDocument>(h.Send(Standard, CommandTypes.ExportConfig, saved.Id.ToString())).ProcessRules);
        Assert.True(h.Send(Standard, CommandTypes.RenameProfile, new RenameProfileArgument { Id = saved.Id, Name = "Ours" }).Success);
        Assert.True(h.Send(Standard, CommandTypes.DeleteProfile, saved.Id).Success);
    }

    [Fact]
    public void StandardUser_MayImportRulesTheyCouldCreate()
    {
        var h = new ProfileHarness();
        var doc = h.Export();
        doc.ProcessRules.Add(new ProcessRuleDto { ProcessName = "game.exe", PriorityClass = (int)ProcessPriorityClass.AboveNormal });
        doc.Settings.SmartTrimEnabled = true;

        var result = h.Data<ConfigPreviewDto>(h.Send(Standard, CommandTypes.ImportConfig, new ImportConfigArgument { Document = ConfigDocuments.Serialize(doc) }));

        Assert.True(result.Applied);
        Assert.Single(h.T.Rules);
        Assert.True(h.T.Stored.SmartTrimEnabled);
    }

    [Fact]
    public void StandardUser_CantImportARuleTheyCouldntCreate()
    {
        var h = new ProfileHarness();
        h.T.Process(900, "spoolsv.exe"); // a service the standard user can't open
        var doc = h.Export();
        doc.ProcessRules.Add(new ProcessRuleDto { ProcessName = "game.exe", PriorityClass = (int)ProcessPriorityClass.RealTime });
        doc.ProcessRules.Add(new ProcessRuleDto { ProcessName = "spoolsv.exe", PriorityClass = (int)ProcessPriorityClass.Idle });
        doc.WatchdogRules.Add(new WatchdogRuleDto { TargetProcessName = "x.exe", ThresholdValue = 5, ActionType = WatchdogResponse.Launch, ActionArgument = "calc.exe" });
        var file = ConfigDocuments.Serialize(doc);

        var preview = h.Data<ConfigPreviewDto>(h.Send(Standard, CommandTypes.ImportConfig, new ImportConfigArgument { Document = file, Preview = true }));
        Assert.False(preview.CanApply);
        Assert.Contains(preview.Refusals, r => r.StartsWith("Access denied") && r.Contains("Realtime"));
        Assert.Contains(preview.Refusals, r => r.Contains("spoolsv.exe") && r.Contains("pid 900"));
        Assert.Contains(preview.Refusals, r => r.Contains("Launch"));

        var result = h.Send(Standard, CommandTypes.ImportConfig, new ImportConfigArgument { Document = file });
        Assert.False(result.Success);
        Assert.StartsWith("Access denied", result.Error);
        Assert.Empty(h.T.Rules);
        Assert.Equal(0, h.Replacements);
        Assert.Null(h.Profiles.Named(ConfigProfileService.BeforeImport));

        // An administrator may.
        Assert.True(h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.ImportConfig, new ImportConfigArgument { Document = file })).Applied);
        Assert.Equal(2, h.T.Rules.Count);
    }

    [Fact]
    public void StandardUser_CantDropAnAdministratorsRule_BySwitchingToAProfileWithoutIt()
    {
        var h = new ProfileHarness();
        var empty = h.SaveProfile("Empty");
        h.AddRule("game.exe", r => r.PriorityClass = (int)ProcessPriorityClass.RealTime); // an administrator's rule

        var result = h.Send(Standard, CommandTypes.SwitchProfile, new SwitchProfileArgument { Id = empty });

        Assert.False(result.Success);
        Assert.Contains("Realtime", result.Error);
        Assert.Single(h.T.Rules);
    }

    [Fact]
    public void StandardUser_MaySwitch_WhenAnAdministratorsRuleStaysAsItIs()
    {
        var h = new ProfileHarness();
        h.AddRule("game.exe", r => r.PriorityClass = (int)ProcessPriorityClass.RealTime);
        h.Settings(s => s.SmartTrimEnabled = true);
        var profile = h.SaveProfile("Trim off");
        h.Settings(s => s.SmartTrimEnabled = false);

        var result = h.Send(Standard, CommandTypes.SwitchProfile, new SwitchProfileArgument { Id = profile });

        Assert.True(result.Success, result.Error);
        Assert.True(h.T.Stored.SmartTrimEnabled);
    }

    [Fact]
    public void StandardUser_CantChangeAdministratorOnlySettings_ThroughAProfile()
    {
        var h = new ProfileHarness();
        h.Settings(s => s.SetTimerResolutionAtStartup = 5000);
        var profile = h.SaveProfile("Fine timer");
        h.Settings(s => s.SetTimerResolutionAtStartup = 0);

        var result = h.Send(Standard, CommandTypes.SwitchProfile, new SwitchProfileArgument { Id = profile });

        Assert.False(result.Success);
        Assert.Contains("timer resolution", result.Error);
        Assert.Equal(0, h.T.Stored.SetTimerResolutionAtStartup);
        Assert.Equal(0, h.Replacements);
    }

    // ------------------------------------------------------------------ machine-specific values

    private static readonly Guid Ultimate = Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61");

    [Fact]
    public void Import_FromAnotherMachine_LeavesOutAndFlagsWhatDoesntTravel()
    {
        var h = new ProfileHarness(); // three CPU Sets (FakeProcessTuning), three plans
        var doc = h.Export();
        doc.Machine = new MachineDescription
        {
            LogicalProcessors = 16, CpuSetLayout = "256:0:1,257:0:1,258:1:0,259:1:0",
            PowerPlans = { new PowerPlanDto { Id = Ultimate, Name = "Ultimate Performance" } },
        };
        doc.ProcessRules.Add(new ProcessRuleDto { ProcessName = "game.exe", AffinityMask = 0xF0, CpuSetIds = "256,257", PowerProfileGuid = Ultimate.ToString(), PriorityClass = (int)ProcessPriorityClass.High });
        doc.ProcessRules.Add(new ProcessRuleDto { ProcessName = "ok.exe", AffinityMask = 0x3, CpuSetIds = "" });
        doc.WatchdogRules.Add(new WatchdogRuleDto { TargetProcessName = "hog.exe", ThresholdValue = 50, ActionType = WatchdogResponse.SetAffinity, ActionArgument = "0x100" });
        doc.Settings.ProBalanceRestraintAffinity = 0xFF00;
        doc.Settings.PerformanceModePlanGuid = Ultimate.ToString();
        doc.Settings.IdlePowerPlanGuid = Powrprof.PowerSaver.ToString(); // installed here: kept
        var file = ConfigDocuments.Serialize(doc);

        var preview = h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.ImportConfig, new ImportConfigArgument { Document = file, Preview = true }));

        var flags = preview.MachineSpecific;
        Assert.Contains(flags, f => f.Where == "Process rule game.exe" && f.Setting == "Cores (affinity)" && f.Value == "0xF0" && f.Reason.Contains("it has 3"));
        Assert.Contains(flags, f => f.Where == "Process rule game.exe" && f.Setting == "CPU Sets" && f.Value == "256,257" && f.Reason.Contains("laid out differently"));
        Assert.Contains(flags, f => f.Where == "Process rule game.exe" && f.Setting == "Power plan" && f.Value.StartsWith("Ultimate Performance"));
        Assert.Contains(flags, f => f.Where == "Watchdog rule hog.exe" && f.Outcome.Contains("turned off"));
        Assert.Contains(flags, f => f.Where == "Settings" && f.Setting == "ProBalance restraint cores");
        Assert.Contains(flags, f => f.Where == "Settings" && f.Setting == "Performance Mode plan" && f.Outcome.Contains("High performance"));
        Assert.Equal(6, flags.Count); // ok.exe's mask fits and its empty CPU Set list travels; the installed idle plan is kept
        Assert.True(preview.CanApply);

        h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.ImportConfig, new ImportConfigArgument { Document = file }));
        var game = h.T.Rules.Single(r => r.ProcessName == "game.exe");
        Assert.Null(game.AffinityMask);
        Assert.Null(game.CpuSetIds);
        Assert.Null(game.PowerProfileGuid);
        Assert.Equal((int)ProcessPriorityClass.High, game.PriorityClass); // the rest of the rule is kept
        Assert.Equal(0x3, h.T.Rules.Single(r => r.ProcessName == "ok.exe").AffinityMask);
        Assert.False(h.T.WatchdogRules.Single().IsEnabled);
        Assert.Equal(0, h.T.Stored.ProBalanceRestraintAffinity);
        Assert.Null(h.T.Stored.PerformanceModePlanGuid);
        Assert.Equal(Powrprof.PowerSaver.ToString(), h.T.Stored.IdlePowerPlanGuid);
    }

    [Fact]
    public void SwitchOnTheSameMachine_KeepsCpuSetIds_AndFlagsNothing()
    {
        var h = new ProfileHarness();
        h.AddRule("game.exe", r => { r.CpuSetIds = "256,257"; r.AffinityMask = 0x5; r.PowerProfileGuid = Powrprof.HighPerformance.ToString(); });
        var profile = h.SaveProfile("Pinned");
        h.T.Rules.Clear();

        var switched = h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.SwitchProfile, new SwitchProfileArgument { Id = profile }));

        Assert.Empty(switched.MachineSpecific);
        Assert.Equal("256,257", h.T.Rules.Single().CpuSetIds);
    }

    [Fact]
    public void Switch_AfterAPowerPlanWasUninstalled_FlagsIt()
    {
        var h = new ProfileHarness();
        h.T.Plans.Add((Ultimate, "Ultimate Performance"));
        h.Settings(s => s.StartWithPowerPlanGuid = Ultimate.ToString());
        var profile = h.SaveProfile("Ultimate");
        h.T.Plans.RemoveAll(p => p.Item1 == Ultimate);

        var switched = h.Data<ConfigPreviewDto>(h.Admin(CommandTypes.SwitchProfile, new SwitchProfileArgument { Id = profile }));

        var flag = Assert.Single(switched.MachineSpecific);
        Assert.Equal("Plan at engine start", flag.Setting);
        Assert.Equal($"Ultimate Performance ({Ultimate})", flag.Value); // named from the machine it was saved on
        Assert.Null(h.T.Stored.StartWithPowerPlanGuid);
    }

    // ------------------------------------------------------------------ the database

    private sealed class TempDbContext : ProGuardDbContext
    {
        private readonly string _path;
        public TempDbContext(string path) { _path = path; }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlite($"Data Source={_path}");
    }

    private static void WithDatabase(Action<string, ServiceProvider> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"proguard_profiles_{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddScoped<ProGuardDbContext>(_ => new TempDbContext(path));
        using var provider = services.BuildServiceProvider();
        try { test(path, provider); }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void ReplaceConfiguration_StoresAllOrNothing()
    {
        WithDatabase((path, provider) =>
        {
            var store = new RuleStore(NullLogger<RuleStore>.Instance, provider);
            store.SaveProcessRule(new ProcessRule { ProcessName = "old.exe", PriorityClass = 64 });
            store.AddWatchdogRule(new WatchdogRule { TargetProcessName = "old.exe", ThresholdValue = 1 });

            var settings = store.GetSettings().Clone();
            settings.ProBalanceSensitivity = 9;
            store.ReplaceConfiguration(
                new[] { new ProcessRule { ProcessName = "new.exe", PriorityClass = 128 }, new ProcessRule { ProcessName = "two.exe" } },
                new[] { new WatchdogRule { TargetProcessName = "new.exe", ThresholdValue = 2 } },
                settings);

            Assert.Equal(new[] { "new.exe", "two.exe" }, store.GetProcessRules().Select(r => r.ProcessName).OrderBy(n => n));
            Assert.Equal(9, store.GetSettings().ProBalanceSensitivity);
            using (var db = new TempDbContext(path))
            {
                Assert.Equal(2, db.ProcessRules.Count());
                Assert.Equal("new.exe", db.WatchdogRules.Single().TargetProcessName);
                Assert.Equal(9, db.Settings.Single().ProBalanceSensitivity);
            }

            // A rule the database refuses (no name: NOT NULL) part-way through: nothing changes, on disk or in memory.
            var worse = settings.Clone();
            worse.ProBalanceSensitivity = 1;
            Assert.ThrowsAny<Exception>(() => store.ReplaceConfiguration(
                new[] { new ProcessRule { ProcessName = "third.exe" }, new ProcessRule { ProcessName = null! } },
                Array.Empty<WatchdogRule>(), worse));

            Assert.Equal(new[] { "new.exe", "two.exe" }, store.GetProcessRules().Select(r => r.ProcessName).OrderBy(n => n));
            Assert.Single(store.GetWatchdogRules());
            Assert.Equal(9, store.GetSettings().ProBalanceSensitivity);
            using (var db = new TempDbContext(path))
            {
                Assert.Equal(2, db.ProcessRules.Count());
                Assert.Single(db.WatchdogRules);
                Assert.Equal(9, db.Settings.Single().ProBalanceSensitivity);
            }
        });
    }

    [Fact]
    public void ProfileTable_IsAddedToAnExistingDatabase_AndKeepsProfiles()
    {
        WithDatabase((path, provider) =>
        {
            // A database from before profiles: every table but ConfigProfiles.
            using (var db = new TempDbContext(path)) db.EnsureSchema();
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TABLE ConfigProfiles";
                drop.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            SchemaUpgraderCacheReset();

            var profiles = new ConfigProfileStore(provider);
            var saved = profiles.Save(new ConfigProfile { Name = "Work", SavedUtc = DateTime.UtcNow, Document = "{}", ProcessRuleCount = 2 });
            Assert.Equal(saved.Id, profiles.Save(new ConfigProfile { Name = "WORK", Document = "{}" }).Id);
            Assert.True(profiles.Rename(saved.Id, "Office"));
            var listed = Assert.Single(new ConfigProfileStore(provider).GetProfiles());
            Assert.Equal("Office", listed.Name);
            Assert.True(profiles.Delete(saved.Id));
            Assert.Empty(profiles.GetProfiles());
        });
    }

    /// <summary>EnsureSchema remembers databases it has checked; this one was changed behind its back.</summary>
    private static void SchemaUpgraderCacheReset()
    {
        var field = typeof(SchemaUpgrader).GetField("Checked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var cache = (System.Collections.IDictionary)field.GetValue(null)!;
        cache.Clear();
    }
}
