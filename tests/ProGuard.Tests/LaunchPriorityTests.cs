using System.Diagnostics;
using Microsoft.Win32;
using ProGuard.Core.Models;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

/// <summary>Image File Execution Options in memory: what ProGuard wrote, by image name.</summary>
internal sealed class FakeLaunchRegistry : ILaunchPriorityRegistry
{
    public Dictionary<string, PerfOptions> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public bool Deny { get; set; }

    public IReadOnlyDictionary<string, PerfOptions> ReadManaged()
    {
        Reads++;
        return new Dictionary<string, PerfOptions>(Entries, StringComparer.OrdinalIgnoreCase);
    }

    public void Write(string image, PerfOptions options)
    {
        if (Deny) throw new UnauthorizedAccessException("Access to the registry key is denied.");
        Writes++;
        Entries[image] = options;
    }

    public void Remove(string image)
    {
        if (Deny) throw new UnauthorizedAccessException("Access to the registry key is denied.");
        Entries.Remove(image);
    }
}

/// <summary>
/// Launch-time priorities: rules with <see cref="ProcessRule.ApplyPrioritiesAtCreation"/> become Image File Execution
/// Options PerfOptions entries, which go again with the rule or the option; only administrators may ask for them.
/// </summary>
public class LaunchPriorityTests
{
    private static ProcessRule Rule(string name, ProcessPriorityClass? priority = null, int? io = null, int? memory = null, bool atLaunch = true) => new()
    {
        ProcessName = name,
        PriorityClass = (int?)priority,
        IoPriority = io,
        MemoryPriority = memory,
        ApplyPrioritiesAtCreation = atLaunch,
    };

    // ------------------------------------------------------------------ mapping

    [Theory]
    [InlineData("game", "game.exe")]
    [InlineData("Game.EXE", "Game.EXE")]
    [InlineData("  obs64.exe ", "obs64.exe")]
    [InlineData("my tool.exe", "my tool.exe")]
    [InlineData("game*.exe", null)]
    [InlineData("gam?.exe", null)]
    [InlineData(@"C:\Games\game.exe", null)]
    [InlineData("games/game.exe", null)]
    [InlineData("c:game.exe", null)]
    [InlineData("", null)]
    [InlineData("..", null)]
    public void ImageName_IsThePlainExeName_OrNothing(string processName, string? expected) =>
        Assert.Equal(expected, LaunchPriorityService.ImageName(processName));

    [Theory]
    [InlineData(ProcessPriorityClass.Idle, 1)]
    [InlineData(ProcessPriorityClass.Normal, 2)]
    [InlineData(ProcessPriorityClass.High, 3)]
    [InlineData(ProcessPriorityClass.BelowNormal, 5)]
    [InlineData(ProcessPriorityClass.AboveNormal, 6)]
    [InlineData(ProcessPriorityClass.RealTime, null)]
    public void PriorityClass_MapsToTheIfeoNumbering(ProcessPriorityClass priority, int? expected) =>
        Assert.Equal(expected, LaunchPriorityService.CpuPriorityClassValue(priority));

    [Fact]
    public void Desired_TakesOnlyRulesThatAskAndCanBeExpressed()
    {
        var desired = LaunchPriorityService.Desired(new[]
        {
            Rule("game", ProcessPriorityClass.High, io: 3, memory: 5),
            Rule("indexer.exe", io: 0),                                     // I/O only
            Rule("other.exe", ProcessPriorityClass.High, atLaunch: false),  // didn't ask
            Rule("wild*.exe", ProcessPriorityClass.High),                    // can't be expressed
            Rule("nothing.exe"),                                             // nothing to write
            new ProcessRule { ProcessName = "blocked.exe", PriorityClass = (int)ProcessPriorityClass.High, ApplyPrioritiesAtCreation = true, DisallowExecution = true },
            Rule("GAME.EXE", ProcessPriorityClass.Idle),                    // same image again: the first rule wins
        });

        Assert.Equal(2, desired.Count);
        Assert.Equal(new PerfOptions(3, 3, 5), desired["game.exe"]);
        Assert.Equal(new PerfOptions(null, 0, null), desired["indexer.exe"]);
    }

    // ------------------------------------------------------------------ keeping the registry in step

    [Fact]
    public void Sync_WritesNewEntries_RewritesChangedOnes_AndRemovesOnesNoRuleWants()
    {
        var registry = new FakeLaunchRegistry();
        registry.Entries["old.exe"] = new PerfOptions(1, null, null); // a rule deleted while the engine was stopped
        var service = new LaunchPriorityService(registry: registry);

        Assert.Equal(3, service.Sync(new[] { Rule("game.exe", ProcessPriorityClass.High), Rule("tool", io: 1) }, force: true));
        Assert.Equal(new PerfOptions(3, null, null), registry.Entries["game.exe"]);
        Assert.Equal(new PerfOptions(null, 1, null), registry.Entries["tool.exe"]);
        Assert.False(registry.Entries.ContainsKey("old.exe"));

        // The option cleared on one rule, the priority changed on the other.
        Assert.Equal(2, service.Sync(new[] { Rule("game.exe", ProcessPriorityClass.AboveNormal), Rule("tool", io: 1, atLaunch: false) }));
        Assert.Equal(new PerfOptions(6, null, null), registry.Entries["game.exe"]);
        Assert.Single(registry.Entries);
    }

    [Fact]
    public void Sync_LeavesTheRegistryAlone_WhileTheRulesAskForTheSame()
    {
        var registry = new FakeLaunchRegistry();
        var service = new LaunchPriorityService(registry: registry);
        var rules = new[] { Rule("game.exe", ProcessPriorityClass.High) };

        service.Sync(rules, force: true);
        int reads = registry.Reads;
        // A reload caused by the ProBalance log, with the same rules: no registry access at all.
        Assert.Equal(0, service.Sync(rules));
        Assert.Equal(reads, registry.Reads);

        // Forced (engine start), it reads again, and finds nothing to do.
        Assert.Equal(0, service.Sync(rules, force: true));
        Assert.Equal(reads + 1, registry.Reads);
        Assert.Equal(1, registry.Writes);
    }

    [Fact]
    public void Sync_WithoutRights_LogsAndCarriesOn()
    {
        var registry = new FakeLaunchRegistry { Deny = true };
        var service = new LaunchPriorityService(registry: registry);

        Assert.Equal(0, service.Sync(new[] { Rule("game.exe", ProcessPriorityClass.High) }, force: true));
        Assert.Empty(registry.Entries);
    }

    // ------------------------------------------------------------------ the real registry, under HKCU

    [Fact]
    public void Registry_WritesMarksReadsAndRemovesOnlyItsOwnEntries()
    {
        var path = @"Software\ProGuard.Tests\IFEO-" + Guid.NewGuid().ToString("N");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        try
        {
            var registry = new RegistryLaunchPriorityRegistry(root, path);

            // Someone else's PerfOptions, and another tool's setting on an image ProGuard will manage.
            using (var theirs = root.CreateSubKey(path + @"\theirs.exe\PerfOptions")) theirs.SetValue("CpuPriorityClass", 3, RegistryValueKind.DWord);
            using (var shared = root.CreateSubKey(path + @"\shared.exe")) shared.SetValue("UseLargePages", 1, RegistryValueKind.DWord);

            registry.Write("game.exe", new PerfOptions(3, 2, 5));
            registry.Write("shared.exe", new PerfOptions(null, 0, null));
            using (var perf = root.OpenSubKey(path + @"\game.exe\PerfOptions")!)
            {
                Assert.Equal(3, perf.GetValue("CpuPriorityClass"));
                Assert.Equal(2, perf.GetValue("IoPriority"));
                Assert.Equal(5, perf.GetValue("PagePriority"));
                Assert.Equal(RegistryValueKind.DWord, perf.GetValueKind("IoPriority"));
            }

            var managed = registry.ReadManaged();
            Assert.Equal(2, managed.Count);
            Assert.Equal(new PerfOptions(3, 2, 5), managed["game.exe"]);
            Assert.Equal(new PerfOptions(null, 0, null), managed["shared.exe"]);

            // A value dropped from the rule goes from the key.
            registry.Write("game.exe", new PerfOptions(3, null, null));
            Assert.Null(root.OpenSubKey(path + @"\game.exe\PerfOptions")!.GetValue("IoPriority"));

            registry.Remove("game.exe");
            registry.Remove("shared.exe");
            Assert.Null(root.OpenSubKey(path + @"\game.exe"));                       // nothing else there: gone entirely
            Assert.Null(root.OpenSubKey(path + @"\shared.exe\PerfOptions"));
            Assert.Equal(1, root.OpenSubKey(path + @"\shared.exe")!.GetValue("UseLargePages")); // the other setting stays
            Assert.Equal(3, root.OpenSubKey(path + @"\theirs.exe\PerfOptions")!.GetValue("CpuPriorityClass"));
            Assert.Empty(registry.ReadManaged());
        }
        finally
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    // ------------------------------------------------------------------ through the command pipe

    private static ProcessRuleDto Dto(string name, bool atLaunch = true, ProcessPriorityClass? priority = ProcessPriorityClass.High) => new()
    {
        ProcessName = name,
        PriorityClass = (int?)priority,
        ApplyPrioritiesAtCreation = atLaunch,
    };

    [Fact]
    public void SavingAndDeletingARule_WritesAndRemovesItsEntry()
    {
        var h = new ThinHarness();

        var saved = h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, argument: Dto("game")));
        Assert.Equal(new PerfOptions(3, null, null), h.LaunchRegistry.Entries["game.exe"]);
        Assert.True(saved.ApplyPrioritiesAtCreation);

        saved.ApplyPrioritiesAtCreation = false;
        Assert.True(h.SendAsAdmin(CommandTypes.SaveProcessRule, argument: saved).Success);
        Assert.Empty(h.LaunchRegistry.Entries);

        saved.ApplyPrioritiesAtCreation = true;
        Assert.True(h.SendAsAdmin(CommandTypes.SaveProcessRule, argument: saved).Success);
        Assert.Single(h.LaunchRegistry.Entries);
        Assert.True(h.SendAsAdmin(CommandTypes.DeleteProcessRule, argument: saved.Id).Success);
        Assert.Empty(h.LaunchRegistry.Entries);
    }

    [Fact]
    public void RenamingARule_MovesItsEntry()
    {
        var h = new ThinHarness();
        var saved = h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, argument: Dto("game.exe")));

        saved.ProcessName = "game2.exe";
        Assert.True(h.SendAsAdmin(CommandTypes.SaveProcessRule, argument: saved).Success);
        Assert.Equal(new[] { "game2.exe" }, h.LaunchRegistry.Entries.Keys.ToArray());
    }

    // No rule may name a pattern or a path (rules match file names only), so those are refused before the launch-time
    // check gets to them.
    [Theory]
    [InlineData("game*.exe", ProcessPriorityClass.High, "would never match anything")]
    [InlineData(@"C:\Games\game.exe", ProcessPriorityClass.High, "would never match anything")]
    [InlineData("game.exe", ProcessPriorityClass.RealTime, "Realtime")]
    [InlineData("game.exe", null, "need a priority")]
    public void RulesThatCantBeWritten_AreRefused(string name, ProcessPriorityClass? priority, string message)
    {
        var h = new ThinHarness();
        var result = h.SendAsAdmin(CommandTypes.SaveProcessRule, argument: Dto(name, priority: priority));
        Assert.False(result.Success);
        Assert.Contains(message, result.Error);
        Assert.Empty(h.LaunchRegistry.Entries);
    }

    [Fact]
    public void AStandardUser_MayNotAskForLaunchPriorities_NorChangeOrDeleteARuleThatHasThem()
    {
        var h = new ThinHarness();
        var user = new ThinCaller();

        var refused = h.Send(user, CommandTypes.SaveProcessRule, argument: Dto("game.exe"));
        Assert.False(refused.Success);
        Assert.Contains("Access denied", refused.Error);
        Assert.Contains("at launch", refused.Error);
        Assert.Empty(h.LaunchRegistry.Entries);

        // The same rule without the option is fine.
        Assert.True(h.Send(user, CommandTypes.SaveProcessRule, argument: Dto("game.exe", atLaunch: false)).Success);

        // An administrator's rule with the option can't be weakened or removed by a standard user.
        var adminRule = h.Data<ProcessRuleDto>(h.SendAsAdmin(CommandTypes.SaveProcessRule, argument: Dto("tool.exe")));
        adminRule.ApplyPrioritiesAtCreation = false;
        Assert.Contains("Access denied", h.Send(user, CommandTypes.SaveProcessRule, argument: adminRule).Error);
        Assert.Contains("Access denied", h.Send(user, CommandTypes.DeleteProcessRule, argument: adminRule.Id).Error);
        Assert.True(h.LaunchRegistry.Entries.ContainsKey("tool.exe"));
    }

    // ------------------------------------------------------------------ the rule editor

    [Fact]
    public void Editor_LoadsAndSavesTheOption_AndSaysWhatsWrongBeforeTheEngineDoes()
    {
        var loaded = new ProGuard.UI.ViewModels.RuleEditor(8, new ProcessRuleDto { Id = 3, ProcessName = "game.exe", PriorityClass = (int)ProcessPriorityClass.High, ApplyPrioritiesAtCreation = true });
        Assert.True(loaded.ApplyAtLaunch);
        Assert.True(loaded.TryBuild(out var dto, out _));
        Assert.True(dto.ApplyPrioritiesAtCreation);
        Assert.Contains("AT LAUNCH", loaded.AdvancedSummary);

        var e = new ProGuard.UI.ViewModels.RuleEditor(8) { ProcessName = "game*.exe", ApplyAtLaunch = true };
        e.Priority = e.Priorities.Single(c => c.Value == ProcessPriorityClass.High);
        Assert.False(e.TryBuild(out _, out var error));
        Assert.Contains("plain program name", error);

        e.ProcessName = "game";
        Assert.True(e.TryBuild(out _, out error), error);

        e.Priority = e.Priorities.Single(c => c.Value == ProcessPriorityClass.RealTime);
        Assert.False(e.TryBuild(out _, out error));
        Assert.Contains("Realtime", error);

        e.Priority = e.Priorities[0];
        Assert.False(e.TryBuild(out _, out error));
        Assert.Contains("needs a priority", error);
        e.IoPriority = e.IoPriorities.Single(c => c.Value == 0);
        Assert.True(e.TryBuild(out _, out error), error);

        e.IsRestricted = true;
        Assert.False(e.CanSetAtLaunch);
    }
}
