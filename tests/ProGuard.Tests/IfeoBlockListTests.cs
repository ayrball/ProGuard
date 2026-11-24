using System.Diagnostics;
using Microsoft.Win32;
using ProGuard.Core.Models;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.Shared.Security;
using ProGuard.UI.Resources;

namespace ProGuard.Tests;

/// <summary>
/// Security products and core Windows programs never get launch-time priorities: the rule editor and the engine refuse
/// such a rule, and the engine never writes Image File Execution Options for one, even from a stored rule.
/// </summary>
public class IfeoBlockListTests
{
    private static ProcessRule AtLaunch(string name) => new()
    {
        ProcessName = name,
        PriorityClass = (int)ProcessPriorityClass.BelowNormal,
        ApplyPrioritiesAtCreation = true,
    };

    public static readonly TheoryData<string> Blocked = new()
    {
        // Microsoft Defender and Windows Security.
        "MsMpEng.exe", "MpDefenderCoreService.exe", "NisSrv.exe", "MsSense.exe", "SenseIR.exe", "SecurityHealthService.exe",
        "SecurityHealthSystray.exe", "smartscreen.exe", "MpCmdRun.exe",
        // Third-party antivirus and EDR.
        "avp.exe", "bdservicehost.exe", "ekrn.exe", "avgsvc.exe", "AvastSvc.exe", "mcshield.exe", "SophosHealth.exe",
        "CSFalconService.exe", "SentinelAgent.exe", "cb.exe", "MBAMService.exe",
        // Core Windows programs.
        "csrss.exe", "lsass.exe", "wininit.exe", "winlogon.exe", "smss.exe", "services.exe", "svchost.exe", "dwm.exe",
        // Whatever the case or the .exe.
        "MSMPENG", "msmpeng", " LSASS.EXE ",
    };

    [Theory]
    [MemberData(nameof(Blocked))]
    public void TheseNames_AreBlocked(string name) => Assert.True(ProtectedProcessNames.IsLaunchPriorityBlocked(name));

    [Theory]
    [InlineData("game.exe")]
    [InlineData("obs64")]
    [InlineData("chrome.exe")]
    [InlineData("msmpeng2.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryPrograms_AreNot(string? name) => Assert.False(ProtectedProcessNames.IsLaunchPriorityBlocked(name));

    [Fact]
    public void EveryCriticalProcess_IsBlocked()
    {
        foreach (var name in ProtectedProcessNames.CriticalNames)
        {
            Assert.True(CriticalProcesses.IsCriticalName(name), name);
            Assert.True(ProtectedProcessNames.IsLaunchPriorityBlocked(name + ".exe"), name);
        }
        Assert.Contains("lsass.exe", ProtectedProcessNames.LaunchPriorityBlockList);
        Assert.Contains("msmpeng.exe", ProtectedProcessNames.LaunchPriorityBlockList);
    }

    [Fact]
    public void TheListsAreNormalised_SoTheyCanMatch()
    {
        foreach (var name in ProtectedProcessNames.CriticalNames.Concat(ProtectedProcessNames.SecurityProductNames))
            Assert.Equal(ProtectedProcessNames.Normalize(name), name);
    }

    // ------------------------------------------------------------------ the engine never writes them

    [Fact]
    public void Desired_LeavesBlockedNamesOut_EvenFromAStoredRule()
    {
        var desired = LaunchPriorityService.Desired(new[] { AtLaunch("MsMpEng.exe"), AtLaunch("lsass"), AtLaunch("game.exe") });
        Assert.Equal(new[] { "game.exe" }, desired.Keys.ToArray());
        Assert.Equal(new[] { "MsMpEng.exe", "lsass.exe" }, LaunchPriorityService.Blocked(new[] { AtLaunch("MsMpEng.exe"), AtLaunch("lsass"), AtLaunch("game.exe") }));
    }

    [Fact]
    public void Sync_RemovesAnEntryWrittenEarlierForABlockedName_AndWritesNone()
    {
        var registry = new FakeLaunchRegistry();
        registry.Entries["MsMpEng.exe"] = new PerfOptions(5, null, null); // from a version before the block list
        var service = new LaunchPriorityService(registry: registry);

        service.Sync(new[] { AtLaunch("MsMpEng.exe"), AtLaunch("game.exe") }, force: true);

        Assert.Equal(new[] { "game.exe" }, registry.Entries.Keys.ToArray());
    }

    [Fact]
    public void TheRegistry_RefusesToWriteABlockedName_ButStillRemovesProGuardsOwnEntry()
    {
        var path = @"Software\ProGuard.Tests\IFEO-" + Guid.NewGuid().ToString("N");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        try
        {
            var registry = new RegistryLaunchPriorityRegistry(root, path);
            Assert.Throws<ArgumentException>(() => registry.Write("MsMpEng.exe", new PerfOptions(5, null, null)));
            Assert.Null(root.OpenSubKey(path + @"\MsMpEng.exe"));

            // An entry an older version wrote, with the marker: removed at the next sync or uninstall.
            using (var old = root.CreateSubKey(path + @"\MsMpEng.exe\PerfOptions"))
            {
                old.SetValue("CpuPriorityClass", 5, RegistryValueKind.DWord);
                old.SetValue(RegistryLaunchPriorityRegistry.MarkerValue, 1, RegistryValueKind.DWord);
            }
            Assert.True(registry.ReadManaged().ContainsKey("MsMpEng.exe"));
            registry.Remove("MsMpEng.exe");
            Assert.Null(root.OpenSubKey(path + @"\MsMpEng.exe"));
        }
        finally
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void TheRegistry_WritesOnlyTheDocumentedPerfOptionsValues_AndTheMarker()
    {
        var path = @"Software\ProGuard.Tests\IFEO-" + Guid.NewGuid().ToString("N");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        try
        {
            new RegistryLaunchPriorityRegistry(root, path).Write("game.exe", new PerfOptions(3, 2, 5));

            using var image = root.OpenSubKey(path + @"\game.exe")!;
            Assert.Empty(image.GetValueNames());                                    // nothing on the image key itself
            Assert.Equal(new[] { "PerfOptions" }, image.GetSubKeyNames());           // no Debugger, no other subkey
            using var perf = image.OpenSubKey("PerfOptions")!;
            Assert.Equal(new[] { "CpuPriorityClass", "IoPriority", "PagePriority", "ProGuardManaged" }, perf.GetValueNames().OrderBy(n => n, StringComparer.Ordinal));
            Assert.All(perf.GetValueNames(), n => Assert.Equal(RegistryValueKind.DWord, perf.GetValueKind(n)));
            Assert.Equal(new[] { "CpuPriorityClass", "IoPriority", "PagePriority" }, RegistryLaunchPriorityRegistry.ValueNames);
        }
        finally
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    // ------------------------------------------------------------------ refused at save time

    [Theory]
    [InlineData("MsMpEng.exe")]
    [InlineData("CSFalconService")]
    [InlineData("svchost.exe")]
    public void Authorization_RefusesLaunchPriorities_ForBlockedNames_ToAdministratorsToo(string name)
    {
        var refusal = RuleAuthorization.CheckCriticalTarget(AtLaunch(name));
        Assert.NotNull(refusal);
        Assert.Contains("security product or a core Windows program", refusal);

        // The rest of such a rule isn't this check's business: without launch priorities a security product's rule passes it.
        if (!CriticalProcesses.IsCriticalName(name))
            Assert.Null(RuleAuthorization.CheckCriticalTarget(new ProcessRule { ProcessName = name, PriorityClass = (int)ProcessPriorityClass.BelowNormal }));
    }

    [Fact]
    public void SavingSuchARule_IsRefused_ThroughTheCommandPipe_AndWritesNothing()
    {
        var h = new ThinHarness();
        var result = h.SendAsAdmin(CommandTypes.SaveProcessRule, argument: new ProcessRuleDto
        {
            ProcessName = "MsMpEng.exe",
            PriorityClass = (int)ProcessPriorityClass.BelowNormal,
            ApplyPrioritiesAtCreation = true,
        });
        Assert.False(result.Success);
        Assert.Contains("security product or a core Windows program", result.Error);
        Assert.Empty(h.LaunchRegistry.Entries);
    }

    [Fact]
    public void TheEditor_RefusesItWithATranslatedMessage()
    {
        var e = new ProGuard.UI.ViewModels.RuleEditor(8) { ProcessName = "MsMpEng", ApplyAtLaunch = true };
        e.Priority = e.Priorities.Single(c => c.Value == ProcessPriorityClass.BelowNormal);
        Assert.False(e.TryBuild(out _, out var error));
        Assert.Equal(string.Format(Strings.Rule_LaunchProtectedProgram, "MsMpEng.exe"), error);

        e.ApplyAtLaunch = false;
        Assert.True(e.TryBuild(out _, out error), error);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("pt-BR")]
    public void TheMessage_IsTranslated_AndNamesTheProgram(string culture)
    {
        var text = Strings.ResourceManager.GetString(nameof(Strings.Rule_LaunchProtectedProgram), System.Globalization.CultureInfo.GetCultureInfo(culture));
        Assert.NotNull(text);
        Assert.Contains("{0}", text);
        if (culture != "en")
            Assert.NotEqual(Strings.ResourceManager.GetString(nameof(Strings.Rule_LaunchProtectedProgram), System.Globalization.CultureInfo.InvariantCulture), text);
    }
}
