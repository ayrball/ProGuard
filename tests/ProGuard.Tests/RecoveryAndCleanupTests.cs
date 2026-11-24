using System.IO;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Moq;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Engine;
using ProGuard.Engine.Security;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Shared.Security;

namespace ProGuard.Tests;

/// <summary>
/// What survives the engine (CPU caps after a crash), what survives an uninstall (launch priorities, graphics
/// preferences, Run values), how long the action log keeps entries, and starting when the data folder is held hostage.
/// </summary>
public class RecoveryAndCleanupTests
{
    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ProGuard.Tests", "rc-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Root);
        public string At(string name) => System.IO.Path.Combine(Root, name);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ================= 4. A hard CPU cap a crash left in force is lifted at the next start =================

    private static Process StartChild() =>
        Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;

    private static uint? RateOfJob(string name)
    {
        var job = CpuCapNative.OpenJob(name);
        if (job == IntPtr.Zero) return null;
        try { return CpuCapNative.CpuRateOf(job); }
        finally { Kernel32.CloseHandle(job); }
    }

    [Fact]
    public void ACapLeftByACrash_IsLiftedByTheNextRun()
    {
        using var temp = new Temp();
        var store = new FileCpuCapRecoveryStore(temp.At("cpu-cap-recovery.json"));
        using var child = StartChild();
        try
        {
            var crashed = new CpuLimiter(NullLogger<CpuLimiter>.Instance, store);
            crashed.AddLimit(child.Id, 20);
            var name = crashed.JobNameOf(child.Id);
            Assert.NotNull(name);
            Assert.Single(store.Load());

            // The engine dies: its handle closes without the cap being lifted, and the record stays behind.
            crashed.AbandonForTest();
            Assert.Equal(2000u, RateOfJob(name!)); // still capped, and the job can still be found by its name
            var watch = CpuCapNative.OpenJob(name!); // to look at the job once the next run has let go of it
            var log = new ListLogger<CpuLimiter>();
            var next = new CpuLimiter(log, store);
            Assert.True(next.RecoverFromLastRun() == 1, string.Join(" | ", log.Entries.Select(e => e.Message)));
            Assert.Equal(10000u, CpuCapNative.CpuRateOf(watch)); // lifted
            Kernel32.CloseHandle(watch);
            Assert.Null(RateOfJob(name!)); // the handle placed in the process went too, so the name went with ours
            Assert.Empty(store.Load());
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void ARecordForAProcessThatTookOverThePid_LiftsNothing()
    {
        using var child = StartChild();
        try
        {
            var store = new MemoryCpuCapRecoveryStore();
            var crashed = new CpuLimiter(NullLogger<CpuLimiter>.Instance, store);
            crashed.AddLimit(child.Id, 20);
            var name = crashed.JobNameOf(child.Id)!;
            crashed.AbandonForTest();

            // Same pid, another creation time: as if the capped process exited and Windows reused its id.
            var record = Assert.Single(store.Caps);
            store.Caps[0] = record with { CreationTime = record.CreationTime - 1 };

            Assert.Equal(0, new CpuLimiter(NullLogger<CpuLimiter>.Instance, store).RecoverFromLastRun());
            Assert.Equal(2000u, RateOfJob(name));
            Assert.Empty(store.Caps); // the stale record is dropped
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void RemovingACap_ClearsItsRecord_AndLiftsIt()
    {
        using var child = StartChild();
        try
        {
            var store = new MemoryCpuCapRecoveryStore();
            using var limiter = new CpuLimiter(NullLogger<CpuLimiter>.Instance, store);
            limiter.AddLimit(child.Id, 30);
            var name = limiter.JobNameOf(child.Id)!;
            Assert.NotEqual(0, Assert.Single(store.Caps).RemoteHandle);

            limiter.RemoveLimit(child.Id);
            Assert.Empty(store.Caps);
            // Nothing holds the job open any more (the handle placed in the process went too), so its name is gone.
            Assert.Null(RateOfJob(name));
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void ARecordNamingSomeoneElsesJob_IsIgnored()
    {
        var store = new MemoryCpuCapRecoveryStore();
        store.Caps.Add(new CpuCapRecord(Environment.ProcessId, 0, "SomethingElse", 0));
        Assert.Equal(0, new CpuLimiter(NullLogger<CpuLimiter>.Instance, store).RecoverFromLastRun());
    }

    // ================= 5. The Responsiveness Guard action log is kept for a month, and to 50,000 entries =================

    private sealed class Db : ProGuardDbContext
    {
        private readonly string _path;
        public Db(string path) => _path = path;
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlite($"Data Source={_path}");
    }

    private static (RuleStore Store, ServiceProvider Provider) Store(string dbPath, Func<string, string?>? trust = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<ProGuardDbContext>(_ => new Db(dbPath));
        var provider = services.BuildServiceProvider();
        return (new RuleStore(NullLogger<RuleStore>.Instance, provider, trust), provider);
    }

    [Fact]
    public void TheActionLog_DropsEntriesOlderThan30Days_AndKeepsTheNewestWithinTheCap()
    {
        using var temp = new Temp();
        var (store, provider) = Store(temp.At("p.db"));
        using (provider)
        {
            var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
            using (var db = new Db(temp.At("p.db")))
            {
                for (var i = 0; i < 10; i++) db.ResponsivenessGuardActions.Add(new ResponsivenessGuardAction { ProcessName = "old", Timestamp = now.AddDays(-31).AddMinutes(i) });
                for (var i = 0; i < 20; i++) db.ResponsivenessGuardActions.Add(new ResponsivenessGuardAction { ProcessName = "new", Timestamp = now.AddDays(-1).AddMinutes(i) });
                db.SaveChanges();
            }

            Assert.Equal(10 + 5, store.PruneResponsivenessGuardActions(now, maxActions: 15));

            using var check = new Db(temp.At("p.db"));
            var left = check.ResponsivenessGuardActions.OrderBy(a => a.Id).ToList();
            Assert.Equal(15, left.Count);
            Assert.All(left, a => Assert.Equal("new", a.ProcessName));
            Assert.Equal(15, left.Select(a => a.Timestamp).Distinct().Count());
            Assert.Equal(now.AddDays(-1).AddMinutes(19), DateTime.SpecifyKind(left[^1].Timestamp, DateTimeKind.Utc)); // the newest stay
        }
    }

    [Fact]
    public void TheActionLogLimits_AreAMonthAnd50000()
    {
        Assert.Equal(TimeSpan.FromDays(30), RuleStore.ActionRetention);
        Assert.Equal(50_000, RuleStore.MaxActions);
    }

    // ================= 9. A database elsewhere is checked as a file, and again before every reload =================

    [Fact]
    public void ADatabaseElsewhere_IsRefused_WhenItOrEitherFolderAboveIsReplaceable()
    {
        var asked = new List<string>();
        var guard = new DataDirectoryGuard(elevated: true) { WhyDatabaseReplaceable = path => { asked.Add(path); return "Users may change it"; } };
        Assert.False(guard.CheckOtherDatabase(@"C:\portable\data\proguard.db"));
        Assert.Equal(@"C:\portable\data\proguard.db", Assert.Single(asked)); // the file, not just its folder
        Assert.Contains(guard.Findings, f => f.Level == LogLevel.Critical && f.Message.Contains("Users may change it"));

        Assert.True(new DataDirectoryGuard(elevated: true) { WhyDatabaseReplaceable = _ => null }.CheckOtherDatabase(@"C:\portable\data\proguard.db"));
        Assert.True(new DataDirectoryGuard(elevated: false) { WhyDatabaseReplaceable = _ => "anything" }.CheckOtherDatabase(@"C:\x\proguard.db"));
    }

    [Fact]
    public void WhyReplaceable_ForADatabaseNotYetCreated_ChecksTheFoldersThatExist()
    {
        using var temp = new Temp();
        var missing = Path.Combine(temp.Root, "not", "yet", "proguard.db");

        // This user's own temp folder: replaceable, and said so about a folder that exists, not as "can't be read".
        var why = DirectoryTrust.WhyReplaceable(missing, allowMissingFile: true);
        Assert.NotNull(why);
        Assert.DoesNotContain("can't be read", why);
        Assert.Contains(temp.Root, why, StringComparison.OrdinalIgnoreCase);

        // Without the allowance a missing file is still refused, as the sign-in task check expects.
        Assert.NotNull(DirectoryTrust.WhyReplaceable(missing));
    }

    [Fact]
    public void TheRuleStore_DoesNotReload_FromADatabaseThatBecameReplaceable()
    {
        using var temp = new Temp();
        string? problem = null;
        var (store, provider) = Store(temp.At("p.db"), _ => problem);
        using (provider)
        {
            using (var db = new Db(temp.At("p.db")))
            {
                db.ProcessRules.Add(new ProcessRule { ProcessName = "planted.exe", PriorityClass = 256 });
                db.SaveChanges();
            }

            problem = "Users may change it";
            Assert.False(store.ReloadIfChanged());
            Assert.Null(store.GetProcessRule("planted"));

            problem = null;
            Assert.True(store.ReloadIfChanged());
            Assert.NotNull(store.GetProcessRule("planted"));
        }
    }

    // ================= 10. A data folder someone holds open doesn't keep the engine from starting =================

    [Fact]
    public void ADataFolderThatCantBeQuarantined_FallsBackToAFreshSibling_AndKeepsUsingIt()
    {
        using var temp = new Temp();
        var preferred = temp.At("ProGuard");
        // A file where the folder should be, held open so it can't be renamed aside.
        using (new FileStream(preferred, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var guard = new DataDirectoryGuard(elevated: true) { ApplyDacl = false, IsTrustedOwner = _ => true };
            var used = guard.SecureDataDirectory(preferred);

            Assert.Equal(preferred + ".1", used);
            Assert.True(Directory.Exists(Path.Combine(preferred + ".1", "logs")));
            Assert.Contains(guard.Findings, f => f.Level == LogLevel.Critical && f.Message.Contains("INSTEAD OF"));

            // The next start, still blocked, picks the same one rather than another.
            Assert.Equal(preferred + ".1", new DataDirectoryGuard(elevated: true) { ApplyDacl = false, IsTrustedOwner = _ => true }.SecureDataDirectory(preferred));
        }

        // Once nothing holds it, the real folder is used again (the planted file is renamed aside as before).
        Assert.Equal(preferred, new DataDirectoryGuard(elevated: true) { ApplyDacl = false, IsTrustedOwner = _ => true }.SecureDataDirectory(preferred));
    }

    [Fact]
    public void AFallbackSiblingThatIsALink_IsNeverAdopted()
    {
        using var temp = new Temp();
        var preferred = temp.At("ProGuard");
        Directory.CreateDirectory(temp.At("elsewhere"));
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{preferred}.1\" \"{temp.At("elsewhere")}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        using (new FileStream(preferred, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var guard = new DataDirectoryGuard(elevated: true) { ApplyDacl = false, IsTrustedOwner = _ => true };
            Assert.Equal(preferred + ".2", guard.SecureDataDirectory(preferred));
            Assert.Contains(guard.Findings, f => f.Message.Contains(preferred + ".1") && f.Message.Contains("link"));
        }
    }

    [Fact]
    public void WithoutAdministratorRights_ThePreferredFolderIsUsedAsItIs()
    {
        using var temp = new Temp();
        Assert.Equal(temp.At("ProGuard"), new DataDirectoryGuard(elevated: false).SecureDataDirectory(temp.At("ProGuard")));
    }

    [Fact]
    public void TheDataDirectory_CanBeRelocated_AndTheDatabaseAndLogFollow()
    {
        // Only the default (no PROGUARD_DB_PATH) follows; the tests set one, so check the parts that don't depend on it.
        Assert.Equal(ProGuardDbContext.DefaultDataDirectory, ProGuardDbContext.DataDirectory);
        Assert.Equal(Path.Combine(ProGuardDbContext.DataDirectory, "logs", "engine.log"), ProGuard.Engine.Logging.FileLoggerProvider.DefaultPath);
    }

    // ================= 3 and 7. What --cleanup-uninstall removes =================

    private sealed class Hives : IDisposable
    {
        public string KeyPath { get; } = @"Software\ProGuardTests\" + Guid.NewGuid().ToString("N");
        public RegistryKey Root { get; }
        public Hives() => Root = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        public void Dispose()
        {
            Root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        }
    }

    private sealed class FakeTasks : ISignInTaskFolder
    {
        public int Calls { get; private set; }
        public int RemoveAll() { Calls++; return 0; }
    }

    private sealed class MemoryLedger : IGpuPreferenceLedger
    {
        public List<GpuPreferenceEntry> Entries { get; } = new();
        public IReadOnlyList<GpuPreferenceEntry> All() => Entries.ToList();
        public void Record(string sid, string exePath, int written, int? found)
        {
            var i = Entries.FindIndex(e => e.Sid == sid && e.ExePath == exePath);
            if (i >= 0) Entries[i] = Entries[i] with { Written = written };
            else Entries.Add(new GpuPreferenceEntry(sid, exePath, written, found));
        }
        public void Forget(GpuPreferenceEntry entry) => Entries.RemoveAll(e => e.Sid == entry.Sid && e.ExePath == entry.ExePath);
    }

    private const string Alice = "S-1-5-21-1-2-3-1001";
    private const string Bob = "S-1-5-21-1-2-3-1002";
    private const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public void Cleanup_RemovesEveryMarkedLaunchPriority_AndNothingElse()
    {
        using var hives = new Hives();
        var ifeo = new RegistryLaunchPriorityRegistry(hives.Root, "IFEO");
        ifeo.Write("game.exe", new PerfOptions(3, null, null));
        ifeo.Write("tool.exe", new PerfOptions(null, 1, null));
        using (var other = hives.Root.CreateSubKey(@"IFEO\theirs.exe\PerfOptions")) other.SetValue("CpuPriorityClass", 3);

        var cleanup = NewCleanup(hives, ifeo, new MemoryLedger());
        Assert.Equal(2, cleanup.RemoveLaunchPriorities());

        Assert.Empty(ifeo.ReadManaged());
        using var kept = hives.Root.OpenSubKey(@"IFEO\theirs.exe\PerfOptions");
        Assert.Equal(3, kept!.GetValue("CpuPriorityClass"));
    }

    [Fact]
    public void Cleanup_RemovesRunValuesThatStartThisCopy_FromEveryLoadedUser()
    {
        using var hives = new Hives();
        var install = @"C:\Program Files\ProGuard\";
        using (var run = hives.Root.CreateSubKey($@"{Alice}\{RunKey}")) run.SetValue("ProGuard.UI", $"\"{install}ProGuard.UI.exe\" --tray");
        using (var run = hives.Root.CreateSubKey($@"{Bob}\{RunKey}"))
        {
            run.SetValue("ProGuard.UI", "\"D:\\portable\\ProGuard\\ProGuard.UI.exe\" --tray"); // a portable copy: not ours
            run.SetValue("Other", "other.exe");
        }
        using (var run = hives.Root.CreateSubKey($@"{Alice}_Classes\{RunKey}")) run.SetValue("ProGuard.UI", $"\"{install}ProGuard.UI.exe\"");

        var cleanup = NewCleanup(hives, new RegistryLaunchPriorityRegistry(hives.Root, "IFEO"), new MemoryLedger(), install);
        Assert.Equal(1, cleanup.RemoveRunValues());

        using (var alice = hives.Root.OpenSubKey($@"{Alice}\{RunKey}")) Assert.Null(alice!.GetValue("ProGuard.UI"));
        using (var bob = hives.Root.OpenSubKey($@"{Bob}\{RunKey}"))
        {
            Assert.NotNull(bob!.GetValue("ProGuard.UI"));
            Assert.NotNull(bob.GetValue("Other"));
        }
    }

    [Fact]
    public void Cleanup_PutsBackGraphicsPreferences_OnlyWhereTheyStillHoldWhatProGuardWrote()
    {
        using var hives = new Hives();
        using (var key = hives.Root.CreateSubKey($@"{Alice}\{GpuKey}"))
        {
            key.SetValue(@"C:\Games\game.exe", "GpuPreference=2;");                               // ProGuard's, nothing before
            key.SetValue(@"C:\Games\other.exe", "GpuPreference=2;SwapEffectUpgradeEnable=1;");    // ProGuard's, over the user's 1
            key.SetValue(@"C:\Games\changed.exe", "GpuPreference=0;");                            // the user changed it since
        }
        var ledger = new MemoryLedger();
        ledger.Record(Alice, @"C:\Games\game.exe", 2, null);
        ledger.Record(Alice, @"C:\Games\other.exe", 2, 1);
        ledger.Record(Alice, @"C:\Games\changed.exe", 2, null);
        ledger.Record(Bob, @"C:\Games\game.exe", 2, null); // Bob isn't signed in: his hive isn't loaded

        var cleanup = NewCleanup(hives, new RegistryLaunchPriorityRegistry(hives.Root, "IFEO"), ledger);
        cleanup.GpuPreferences.RemoveAll();

        using var after = hives.Root.OpenSubKey($@"{Alice}\{GpuKey}")!;
        Assert.Null(after.GetValue(@"C:\Games\game.exe"));
        Assert.Equal("GpuPreference=1;SwapEffectUpgradeEnable=1;", after.GetValue(@"C:\Games\other.exe"));
        Assert.Equal("GpuPreference=0;", after.GetValue(@"C:\Games\changed.exe"));
        Assert.Equal(Bob, Assert.Single(ledger.Entries).Sid); // kept for when Bob's hive is loaded
        Assert.False(hives.Root.GetSubKeyNames().Contains(Bob)); // and nothing was created for him
    }

    [Fact]
    public void DeletingTheRule_TakesItsGraphicsPreferenceBackOut()
    {
        using var hives = new Hives();
        using (var key = hives.Root.CreateSubKey($@"{Alice}\{GpuKey}")) key.SetValue(@"C:\Games\game.exe", "GpuPreference=2;");
        var ledger = new MemoryLedger();
        ledger.Record(Alice, @"C:\Games\game.exe", 2, null);
        var service = new GpuPreferenceService(ledger, new UserGpuPreferences(hives.Root));

        Assert.Equal(0, service.Sync(new[] { new ProcessRule { ProcessName = "game.exe", GpuPreference = 2 } }));
        using (var key = hives.Root.OpenSubKey($@"{Alice}\{GpuKey}")) Assert.NotNull(key!.GetValue(@"C:\Games\game.exe"));

        Assert.Equal(1, service.Sync(new[] { new ProcessRule { ProcessName = "game.exe", PriorityClass = 32 } })); // option cleared
        using (var key = hives.Root.OpenSubKey($@"{Alice}\{GpuKey}")) Assert.Null(key!.GetValue(@"C:\Games\game.exe"));
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public void TheApplier_RecordsWhatItWrites_AndWhatWasThereBefore()
    {
        var native = new Mock<IProcessTuningNative>();
        native.Setup(n => n.GetProcessOwnerSid(7)).Returns(Alice);
        native.Setup(n => n.ReadUserGpuPreference(Alice, @"C:\g.exe")).Returns("GpuPreference=1;");
        var ledger = new MemoryLedger();
        var applier = new GpuPreferenceApplier(native.Object, ledger);

        var result = applier.Apply(new ProcessRule { ProcessName = "g.exe", GpuPreference = 2 }, new ProcessTarget(7, "g", @"C:\g.exe"), onlyIfChanged: false);

        Assert.Equal(RuleApplyOutcome.Applied, result.Outcome);
        Assert.Equal(new GpuPreferenceEntry(Alice, @"C:\g.exe", 2, 1), Assert.Single(ledger.Entries));
    }

    [Fact]
    public void Strip_KeepsTheOtherEntries()
    {
        Assert.Null(GpuPreferenceApplier.Strip("GpuPreference=2;"));
        Assert.Equal("SwapEffectUpgradeEnable=1;", GpuPreferenceApplier.Strip("GpuPreference=2;SwapEffectUpgradeEnable=1;"));
    }

    [Fact]
    public void Cleanup_RunsEveryStep_EvenWhenOneFails()
    {
        using var hives = new Hives();
        var ifeo = new Mock<ILaunchPriorityRegistry>();
        ifeo.Setup(r => r.ReadManaged()).Throws(new UnauthorizedAccessException());
        var tasks = new FakeTasks();
        var cleanup = new UninstallCleanup
        {
            Logger = NullLogger.Instance,
            LaunchPriorities = ifeo.Object,
            GpuPreferences = new GpuPreferenceService(new MemoryLedger(), new UserGpuPreferences(hives.Root)),
            Users = hives.Root,
            InstallDirectory = @"C:\Program Files\ProGuard\",
            Tasks = tasks,
        };

        cleanup.RunAll();
        Assert.Equal(1, tasks.Calls);
    }

    [Theory]
    [InlineData("--cleanup-uninstall", true)]
    [InlineData("--CLEANUP-UNINSTALL", true)]
    [InlineData("--console", false)]
    public void TheCleanupMode_IsChosenByItsArgument(string argument, bool expected) =>
        Assert.Equal(expected, UninstallCleanup.IsRequested(new[] { argument }));

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001", true)]
    [InlineData("S-1-12-1-1-2-3-4", true)]
    [InlineData("S-1-5-21-1-2-3-1001_Classes", false)]
    [InlineData("S-1-5-18", false)]
    [InlineData(".DEFAULT", false)]
    public void OnlyUserProfileHives_AreLookedAt(string name, bool expected) => Assert.Equal(expected, UninstallCleanup.IsUserHive(name));

    [Fact]
    public void TheInstaller_RunsTheCleanup_AsSystem_OnlyWhenProGuardIsRemoved()
    {
        var wxs = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "Package.wxs"));
        Assert.Contains("ExeCommand=\"--cleanup-uninstall\"", wxs);
        var action = wxs[wxs.IndexOf("Id=\"CleanupUninstall\"", StringComparison.Ordinal)..];
        action = action[..action.IndexOf("/>", StringComparison.Ordinal)];
        Assert.Contains("Execute=\"deferred\"", action);
        Assert.Contains("Impersonate=\"no\"", action);
        Assert.Contains("Return=\"ignore\"", action);
        Assert.Contains("<Custom Action=\"CleanupUninstall\" Before=\"RemoveFiles\" Condition=\"REMOVE~=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE\" />", wxs);
        // Both data folder entries also make Administrators the owner, whoever created the folder.
        Assert.Equal(2, CountOf(wxs, "Sddl=\"O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)\""));
    }

    private static int CountOf(string text, string what)
    {
        var count = 0;
        for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ProGuard.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root wasn't found");
    }

    private static UninstallCleanup NewCleanup(Hives hives, ILaunchPriorityRegistry ifeo, IGpuPreferenceLedger ledger, string install = @"C:\Program Files\ProGuard\") => new()
    {
        Logger = NullLogger.Instance,
        LaunchPriorities = ifeo,
        GpuPreferences = new GpuPreferenceService(ledger, new UserGpuPreferences(hives.Root)),
        Users = hives.Root,
        InstallDirectory = install,
        Tasks = new FakeTasks(),
    };
}
