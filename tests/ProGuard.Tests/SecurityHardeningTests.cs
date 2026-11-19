using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Security;
using ProGuard.Engine.Services;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.Shared.Security;
using ProGuard.UI.Services;

namespace ProGuard.Tests;

/// <summary>Pins for fake process ids: every process "is running" and nothing is held.</summary>
internal static class TestPins
{
    private sealed class Nothing : IDisposable, ProcessActionService.IPinnedProcess
    {
        public DateTime? StartTimeUtc { get; init; }
        public void Dispose() { }
    }

    public static IDisposable? Any(int pid) => new Nothing();
    public static ProcessActionService.IPinnedProcess? AnyDescendant(int pid) => new Nothing();
    public static ProcessActionService.IPinnedProcess? StartedAt(int pid, DateTime start) => new Nothing { StartTimeUtc = start };
}

/// <summary>Who owns each fake process. A pid it doesn't know has an owner that can't be read.</summary>
internal sealed class FakeOwners : IProcessOwnerReader
{
    public Dictionary<int, TokenIdentity> Owners { get; } = new();
    public int Reads { get; private set; }

    public TokenIdentity? Read(int pid)
    {
        Reads++;
        return Owners.TryGetValue(pid, out var owner) ? owner : null;
    }

    public FakeOwners Own(int pid, string sid, bool elevated = false)
    {
        Owners[pid] = new TokenIdentity(sid, elevated);
        return this;
    }
}

/// <summary>
/// The security review's findings, one region each: rules scoped to their author, the data folder's permissions, the
/// purge settings, pipe-name squatting, the pid race, the retired SetSetting, per-user caps and profile ownership, the
/// pinned signing dlib, and the elevated sign-in task.
/// </summary>
public class SecurityHardeningTests
{
    private const string Alice = ThinCaller.StandardSid;
    private const string Bob = "S-1-5-21-1111-2222-3333-1002";

    private static readonly ThinCaller Standard = new();
    private static readonly ThinCaller Admin = ThinCaller.Admin();

    private static ProcessRule ScopedRule(string name, string author = Alice, Action<ProcessRule>? configure = null)
    {
        var rule = new ProcessRule { Id = 1, ProcessName = name, AuthorSid = author, AuthorIsAdministrator = false };
        configure?.Invoke(rule);
        return rule;
    }

    private static WatchdogRule ScopedWatchdog(WatchdogAction action, string author = Alice) => new()
    {
        Id = 1, TargetProcessName = "hog.exe", IsEnabled = true, ConditionType = WatchdogCondition.CpuAbove, ThresholdValue = 50,
        ActionType = action, CooldownSeconds = 0, AuthorSid = author, AuthorIsAdministrator = false,
    };

    // ================= 1. Rules act only where their author could =================

    [Fact]
    public void RuleScope_AdministratorAndOlderRules_ApplyToEveryProcess_WithoutLookingAtIt()
    {
        var owners = new FakeOwners();
        var scope = new RuleScope(owners);

        Assert.True(scope.AppliesTo(new ProcessRule { ProcessName = "x" }, 1234)); // stored before authors were recorded
        Assert.True(scope.AppliesTo(new ProcessRule { ProcessName = "x", AuthorSid = Alice, AuthorIsAdministrator = true }, 1234));
        Assert.True(scope.AppliesTo(new WatchdogRule { AuthorSid = null, AuthorIsAdministrator = false }, 1234)); // no author: an administrator's
        Assert.Equal(0, owners.Reads);
    }

    [Fact]
    public void RuleScope_AStandardUsersRule_AppliesOnlyToTheirOwnUnelevatedProcesses()
    {
        var owners = new FakeOwners().Own(10, Alice).Own(11, Bob).Own(12, Alice, elevated: true).Own(13, TokenIdentity.LocalSystemSid, elevated: true);
        var scope = new RuleScope(owners);
        var rule = ScopedRule("app.exe");

        Assert.True(scope.AppliesTo(rule, 10));
        Assert.False(scope.AppliesTo(rule, 11)); // another user's
        Assert.False(scope.AppliesTo(rule, 12)); // the author's own, but elevated (a UAC-filtered admin's unelevated UI)
        Assert.False(scope.AppliesTo(rule, 13)); // a service
        Assert.False(scope.AppliesTo(rule, 14)); // owner unreadable: never
        Assert.False(scope.AppliesTo(rule, 4));  // System
    }

    [Fact]
    public void RuleScope_RemembersOwners_ByIdAndStartTime()
    {
        var owners = new FakeOwners().Own(10, Alice);
        var scope = new RuleScope(owners);
        var rule = ScopedRule("app.exe");
        var started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(scope.AppliesTo(rule, 10, started));
        Assert.True(scope.AppliesTo(rule, 10, started));
        Assert.Equal(1, owners.Reads);

        // The id now belongs to Bob's new process: asked again, not answered from the old one.
        owners.Own(10, Bob);
        Assert.False(scope.AppliesTo(rule, 10, started.AddSeconds(5)));
        Assert.Equal(2, owners.Reads);
    }

    [Fact]
    public void SavingARule_RecordsTheCaller_AndIgnoresAnyAuthorTheClientSends()
    {
        var h = new ThinHarness();
        var sneaky = """{"ProcessName":"game.exe","PriorityClass":64,"AuthorSid":"S-1-5-18","AuthorIsAdministrator":true}""";

        Assert.True(h.Send(Standard, CommandTypes.SaveProcessRule, null, sneaky).Success);
        var rule = Assert.Single(h.Rules);
        Assert.Equal(Alice, rule.AuthorSid);
        Assert.False(rule.AuthorIsAdministrator);

        var watchdog = """{"TargetProcessName":"game.exe","ConditionType":0,"ThresholdValue":50,"ActionType":4,"AuthorSid":"S-1-5-18","AuthorIsAdministrator":true}""";
        Assert.True(h.Send(Standard, CommandTypes.SaveWatchdogRule, null, watchdog).Success);
        Assert.Equal(Alice, h.WatchdogRules.Single().AuthorSid);
        Assert.False(h.WatchdogRules.Single().AuthorIsAdministrator);

        // An administrator saving it again makes it an administrator's rule.
        var dto = RuleDtoMapperFor(rule);
        Assert.True(h.Send(Admin, CommandTypes.SaveProcessRule, null, dto).Success);
        Assert.True(h.Rules.Single().AuthorIsAdministrator);
        Assert.Equal(ThinCaller.AdminSid, h.Rules.Single().AuthorSid);
    }

    private static ProcessRuleDto RuleDtoMapperFor(ProcessRule rule) => RuleDtoMapper.ToDto(rule);

    [Fact]
    public void AStandardUser_MayChangeAndDeleteOnlyTheirOwnRules()
    {
        var h = new ThinHarness();
        var bob = new ThinCaller { Name = @"TEST\bob", Sid = Bob };

        var mine = h.Data<ProcessRuleDto>(h.Send(Standard, CommandTypes.SaveProcessRule, null, new ProcessRuleDto { ProcessName = "game.exe", PriorityClass = 16384 }));
        mine.PriorityClass = 32768;
        Assert.True(h.Send(Standard, CommandTypes.SaveProcessRule, null, mine).Success);

        var refused = h.Send(bob, CommandTypes.SaveProcessRule, null, mine);
        Assert.False(refused.Success);
        Assert.Contains("another user saved it", refused.Error);
        Assert.False(h.Send(bob, CommandTypes.DeleteProcessRule, null, mine.Id).Success);

        Assert.True(h.Send(Standard, CommandTypes.DeleteProcessRule, null, mine.Id).Success);
        Assert.Empty(h.Rules);
    }

    [Fact]
    public void ARuleSavedByAStandardUser_IsAppliedOnlyToTheirOwnRunningProcesses()
    {
        var h = new ThinHarness();
        h.Process(70, "victim");
        h.Process(71, "victim");
        h.Owners.Own(70, Alice).Own(71, Bob);
        var caller = new ThinCaller();
        caller.OpenablePids.Add(70); // at save time only Alice's copy runs as far as the old check could tell...
        caller.OpenablePids.Add(71); // ...or Bob's copy started later: either way the rule must not reach it

        Assert.True(h.Send(caller, CommandTypes.SaveProcessRule, null, new ProcessRuleDto { ProcessName = "victim.exe", DisallowExecution = true }).Success);

        h.Access.Verify(a => a.Terminate(70), Times.Once);
        h.Access.Verify(a => a.Terminate(71), Times.Never);
    }

    [Fact]
    public void TheOrchestrator_LeavesOtherUsersProcessesAlone()
    {
        // A real process, so that a regression would really end it (and not this test run).
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            var monitor = new FakeProcessMonitor();
            var rules = new List<ProcessRule> { ScopedRule("ping.exe", configure: r => r.DisallowExecution = true) };
            var orchestrator = new ProcessOrchestrator(NullLogger<ProcessOrchestrator>.Instance, monitor, Mock.Of<IResponsivenessGuardEngine>(),
                Mock.Of<ICpuLimiter>(), Mock.Of<IMemoryTrimService>(), Mock.Of<IPowerManager>(), Mock.Of<IWatchdogService>(), FakeRules.Store(rules).Object)
            {
                Scope = new RuleScope(new FakeOwners().Own(child.Id, Bob)),
            };
            orchestrator.Start();

            // Bob's copy starts: Alice's Disallow rule must not end it.
            monitor.Start(child.Id, "ping");

            Assert.False(child.WaitForExit(500));
            orchestrator.Stop();
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void TheCpuLimiter_AStandardUsersRule_ConstrainsOnlyTheirOwnProcesses()
    {
        using var h = new CpuAffinityLimiterHarness();
        var rule = h.AddRule(over: 1);
        rule.AuthorSid = Alice;
        rule.AuthorIsAdministrator = false;
        h.Add(11, cpu: 95);
        h.Add(12, cpu: 95);
        h.Limiter.Scope = new RuleScope(new FakeOwners().Own(11, Bob).Own(12, Alice));

        h.At(0);
        h.At(1500);
        h.At(3000);

        Assert.Contains(h.Access.Sets, s => s.Pid == 12);
        Assert.DoesNotContain(h.Access.Sets, s => s.Pid == 11);
    }

    [Fact]
    public void ReapplyingRules_AStandardUsersRule_PutsBackOnlyTheirOwnProcesses()
    {
        var native = new FakeProcessTuning();
        native.Io[11] = 2;
        native.Io[12] = 2;
        var processes = new List<ProcessInfo> { new() { Pid = 11, Name = "game" }, new() { Pid = 12, Name = "game" } };
        var monitor = new Mock<IProcessMonitor>();
        monitor.Setup(m => m.GetAllProcesses()).Returns(() => processes);
        monitor.Setup(m => m.PollingInterval).Returns(TimeSpan.FromSeconds(1));
        var store = FakeRules.Store(new List<ProcessRule> { ScopedRule("game.exe", configure: r => { r.IoPriority = 0; r.ReapplyIfChanged = true; }) });
        var service = new ProGuard.Engine.Services.ProcessRules.PersistentRuleService(new ListLogger<ProGuard.Engine.Services.ProcessRules.PersistentRuleService>(),
            new ProGuard.Engine.Services.ProcessRules.IProcessRuleApplier[] { new ProGuard.Engine.Services.ProcessRules.IoPriorityApplier(native) }, monitor.Object, store.Object)
        {
            Scope = new RuleScope(new FakeOwners().Own(11, Alice).Own(12, Bob)),
        };

        service.ReapplyChanged();

        Assert.Equal(0, native.Io[11]);
        Assert.Equal(2, native.Io[12]);
    }

    [Fact]
    public void ResponsivenessGuard_AStandardUsersExclusion_SparesOnlyTheirOwnProcesses()
    {
        using var h = new ResponsivenessGuardHarness();
        h.Add(100, cpu: 50, name: "render");
        h.Store.Setup(s => s.GetProcessRule("render")).Returns(ScopedRule("render.exe", configure: r => r.ExcludeFromResponsivenessGuard = true));
        h.Engine.RestrainAfterMs = 0;
        h.Engine.Scope = new RuleScope(new FakeOwners().Own(100, Bob)); // someone else's render.exe

        h.At(0);
        h.At(1000);

        Assert.True(h.Engine.IsEngaged);
    }

    [Fact]
    public void InstanceSpread_AStandardUsersRule_SpreadsOnlyTheirOwnInstances()
    {
        var monitor = new FakeProcessMonitor();
        var access = new FakeRuleProcessAccess();
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (var pid in new[] { 11, 12, 13 })
        {
            monitor.Add(pid, "worker", cpu: 50);
            access.Affinity[pid] = 0xFF;
            access.Start[pid] = start.AddSeconds(pid);
        }
        var rules = new List<ProcessRule> { ScopedRule("worker.exe", configure: r => r.InstanceSpreadMode = InstanceSpreadMode.EqualCoresPerInstance) };
        var spread = new InstanceSpread(NullLogger<InstanceSpread>.Instance, monitor, FakeRules.Store(rules).Object, access,
            FakeRules.EightThreadsFourCores, new ManualTimeProvider())
        {
            Scope = new RuleScope(new FakeOwners().Own(11, Alice).Own(12, Bob).Own(13, Alice)),
        };

        spread.Tick();

        Assert.Null(spread.GetAppliedMask(12));
        Assert.DoesNotContain(access.AffinityWrites, w => w.Pid == 12);
        Assert.Contains(access.AffinityWrites, w => w.Pid == 11);
        Assert.Contains(access.AffinityWrites, w => w.Pid == 13);
    }

    [Fact]
    public void AnAdministratorsSwitch_PutsAStandardUsersRuleBack_AsTheirs()
    {
        var h = new ProfileHarness();
        // The standard user's own rule; an administrator saves the configuration, empties it, and switches back.
        Assert.True(h.Send(Standard, CommandTypes.SaveProcessRule, new ProcessRuleDto { ProcessName = "mine.exe", PriorityClass = 16384 }).Success);
        var saved = h.SaveProfile("With the standard user's rule");
        h.T.Rules.Clear();
        var empty = h.SaveProfile("Empty");
        Assert.True(h.Admin(CommandTypes.SwitchProfile, new SwitchProfileArgument { Id = empty }).Success);
        Assert.Empty(h.T.Rules);

        Assert.True(h.Admin(CommandTypes.SwitchProfile, new SwitchProfileArgument { Id = saved }).Success);

        var back = Assert.Single(h.T.Rules);
        Assert.False(back.AuthorIsAdministrator);
        Assert.Equal(Alice, back.AuthorSid);
    }

    [Fact]
    public void Watchdog_AStandardUsersRule_SeesAndActsOnlyOnTheirOwnProcesses()
    {
        var actions = new FakeProcessActions();
        var processes = new List<ProcessInfo>
        {
            new() { Pid = 10, Name = "hog", CpuPercent = 90 },
            new() { Pid = 11, Name = "hog", CpuPercent = 90 },
            new() { Pid = 12, Name = "hog", CpuPercent = 90 },
        };
        foreach (var p in processes) actions.Running.Add(p.Pid);
        var monitor = new Mock<IProcessMonitor>();
        monitor.Setup(m => m.GetAllProcesses()).Returns(() => processes.ToList());
        var store = new Mock<IRuleStore>();
        var rules = new List<WatchdogRule> { ScopedWatchdog(WatchdogAction.Terminate) };
        store.Setup(s => s.GetWatchdogRules()).Returns(() => rules.ToList());
        store.Setup(s => s.GetProcessRules()).Returns(Array.Empty<ProcessRule>());
        var clock = new ManualClock();
        var watchdog = new WatchdogService(NullLogger<WatchdogService>.Instance, monitor.Object, store.Object, actions, new FakeLauncher(), new WatchdogEventLog(clock), null, clock)
        {
            Scope = new RuleScope(new FakeOwners().Own(10, Alice).Own(11, Bob).Own(12, Alice, elevated: true)),
        };

        watchdog.EvaluateRules();

        Assert.Equal(new[] { "terminate 10" }, actions.Calls);

        // An instance-count rule counts only the author's instances too: one of hers is not "more than one".
        actions.Calls.Clear();
        rules[0] = ScopedWatchdog(WatchdogAction.Terminate);
        rules[0].ConditionType = WatchdogCondition.InstanceCountAbove;
        rules[0].ThresholdValue = 1;
        processes.RemoveAll(p => p.Pid == 10);
        processes.Add(new ProcessInfo { Pid = 20, Name = "hog" });
        actions.Running.Add(20);
        watchdog.EvaluateRules();
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public void KeepRunning_AStandardUsersRule_NeverBorrowsAnotherUsersCommandLine()
    {
        var actions = new FakeProcessActions();
        var launcher = new FakeLauncher();
        var processes = new List<ProcessInfo> { new() { Pid = 11, Name = "tool" } };
        actions.Running.Add(11);
        actions.LaunchInfo[11] = new ProcessLaunchInfo(@"C:\bob\tool.exe", "\"C:\\bob\\tool.exe\" --as-bob", @"C:\bob", 2);
        var store = new Mock<IRuleStore>();
        store.Setup(s => s.GetProcessRules()).Returns(() => new[] { ScopedRule("tool.exe", configure: r => r.KeepRunning = true) });
        var clock = new ManualClock();
        var keepRunning = new KeepRunningService(NullLogger<KeepRunningService>.Instance, store.Object, actions, launcher, new WatchdogEventLog(clock), clock)
        {
            Scope = new RuleScope(new FakeOwners().Own(11, Bob)),
        };

        keepRunning.Tick(processes);
        processes.Clear();
        actions.Running.Remove(11);
        for (var i = 0; i < 30; i++) { clock.Advance(TimeSpan.FromSeconds(1)); keepRunning.Tick(processes); }

        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public void InstanceLimit_FromAStandardUser_CountsAndEndsOnlyTheirOwnInstances()
    {
        var monitor = new FakeProcessMonitor();
        var access = new FakeRuleProcessAccess();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var pid in new[] { 21, 22, 23, 24 })
        {
            var info = monitor.Add(pid, "app");
            info.StartTime = start.AddSeconds(pid);
            access.Session[pid] = 1;
            access.Start[pid] = info.StartTime;
        }
        var owners = new FakeOwners().Own(21, Bob).Own(22, Bob).Own(23, Alice).Own(24, Alice);
        var limiter = new InstanceLimiter(NullLogger<InstanceLimiter>.Instance, monitor, access) { Scope = new RuleScope(owners) };
        var rule = ScopedRule("app.exe", configure: r => r.MaxInstances = 1);

        // Bob's instance starting: her rule doesn't apply to it at all, however many are running.
        Assert.False(limiter.Enforce(monitor.GetProcess(22)!, rule));
        Assert.Empty(access.Terminated);

        // Her second instance: over her limit of one, counting only hers; the newest of hers is ended, none of Bob's.
        Assert.True(limiter.Enforce(monitor.GetProcess(24)!, rule));
        Assert.Equal(new[] { 24 }, access.Terminated);
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("consent.exe")]
    [InlineData("logonui.exe")]
    [InlineData("userinit.exe")]
    [InlineData("sihost.exe")]
    [InlineData("svchost.exe")]
    [InlineData("spoolsv.exe")]
    [InlineData("msiexec.exe")]
    [InlineData("mmc.exe")]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("cmd.exe")]
    [InlineData("conhost.exe")]
    [InlineData("dwm.exe")]
    [InlineData("ctfmon.exe")]
    [InlineData("fontdrvhost.exe")]
    [InlineData("taskhostw.exe")]
    [InlineData("RuntimeBroker.exe")]
    [InlineData("SearchHost.exe")]
    [InlineData("StartMenuExperienceHost.exe")]
    [InlineData("ShellExperienceHost.exe")]
    [InlineData("SecurityHealthService.exe")]
    public void StandardUsers_MayNotWriteRulesForShellHostOrSecurityProcesses(string name)
    {
        Assert.True(CriticalProcesses.IsAdministratorOnlyRuleTarget(name));

        var h = new ThinHarness();
        var refused = h.Send(Standard, CommandTypes.SaveProcessRule, null, new ProcessRuleDto { ProcessName = name, PriorityClass = 16384 });
        Assert.False(refused.Success);
        Assert.Contains("a system process", refused.Error);
        // An administrator may, except for the session and security processes no rule acts on (SafetyGuardTests).
        Assert.Equal(!CriticalProcesses.IsCriticalName(name),
            h.Send(Admin, CommandTypes.SaveProcessRule, null, new ProcessRuleDto { ProcessName = name, PriorityClass = 16384 }).Success);
    }

    [Theory]
    // Never acted on, by anyone's rule: ending them logs the user off, breaks UAC or takes services down.
    [InlineData("consent", true)]
    [InlineData("logonui", true)]
    [InlineData("userinit", true)]
    [InlineData("sihost", true)]
    [InlineData("svchost", true)]
    [InlineData("SecurityHealthService", true)]
    // Administrators may still limit them, and ending a process tree still reaches consoles and script hosts.
    [InlineData("cmd", false)]
    [InlineData("conhost", false)]
    [InlineData("powershell", false)]
    [InlineData("pwsh", false)]
    [InlineData("explorer", false)]
    [InlineData("RuntimeBroker", false)]
    [InlineData("msiexec", false)]
    public void TheNeverActOnTier_IsOnlyForSessionAndSecurityProcesses(string name, bool critical) =>
        Assert.Equal(critical, CriticalProcesses.IsCriticalName(name));

    [Fact]
    public void SwitchingProfile_KeepsTheAuthorOfUnchangedRules_AndStampsTheCallerOnNewOnes()
    {
        var h = new ProfileHarness();
        h.AddRule("admin.exe", r => r.PriorityClass = 32768); // an administrator's
        var doc = h.Export();
        doc.ProcessRules.Add(new ProcessRuleDto { ProcessName = "mine.exe", PriorityClass = 16384 });

        var result = h.Send(Standard, CommandTypes.ImportConfig, new ImportConfigArgument { Document = ProGuard.Shared.Profiles.ConfigDocuments.Serialize(doc) });

        Assert.True(result.Success, result.Error);
        var kept = h.T.Rules.Single(r => r.ProcessName == "admin.exe");
        Assert.True(kept.AuthorIsAdministrator);
        var added = h.T.Rules.Single(r => r.ProcessName == "mine.exe");
        Assert.False(added.AuthorIsAdministrator);
        Assert.Equal(Alice, added.AuthorSid);
    }

    [Fact]
    public void TheSchemaUpgrade_MakesExistingRules_AdministratorRules()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ProGuard.Tests", "authors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "proguard.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE "ProcessRules" ("Id" INTEGER NOT NULL CONSTRAINT "PK_ProcessRules" PRIMARY KEY AUTOINCREMENT, "ProcessName" TEXT NOT NULL);
                    INSERT INTO "ProcessRules" ("ProcessName") VALUES ('game.exe');
                    CREATE TABLE "WatchdogRules" ("Id" INTEGER NOT NULL CONSTRAINT "PK_WatchdogRules" PRIMARY KEY AUTOINCREMENT, "TargetProcessName" TEXT NOT NULL);
                    INSERT INTO "WatchdogRules" ("TargetProcessName") VALUES ('hog.exe');
                    CREATE TABLE "ConfigProfiles" ("Id" INTEGER NOT NULL CONSTRAINT "PK_ConfigProfiles" PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL);
                    INSERT INTO "ConfigProfiles" ("Name") VALUES ('Old');
                    """;
                command.ExecuteNonQuery();
            }

            using (var db = new FileDb(path)) db.EnsureSchema();

            using (var db = new FileDb(path))
            {
                var rule = db.ProcessRules.Single();
                Assert.Null(rule.AuthorSid);
                Assert.True(rule.AuthorIsAdministrator);
                Assert.False(rule.IsScopedToAuthor());
                var watchdog = db.WatchdogRules.Single();
                Assert.True(watchdog.AuthorIsAdministrator);
                Assert.False(watchdog.IsScopedToAuthor());
                Assert.Null(db.ConfigProfiles.Single().CreatorSid);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class FileDb : ProGuardDbContext
    {
        private readonly string _path;
        public FileDb(string path) => _path = path;
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlite($"Data Source={_path}");
    }

    // ================= 2. The data folder =================

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ProGuard.Tests", "data-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Root);
        public string Data => Path.Combine(Root, "ProGuard");

        public void Dispose()
        {
            // Top down: a protected folder has to be given back before what is inside it can even be listed.
            GiveBackAll(Root);
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        private static void GiveBackAll(string dir)
        {
            GiveBack(dir);
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir)) GiveBackAll(sub);
            }
            catch (Exception) { /* not ours to fix */ }
        }

        /// <summary>Puts this user back in charge of a folder a test protected, so it can be deleted.</summary>
        private static void GiveBack(string path)
        {
            try
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return;
                var security = new DirectorySecurity();
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(security);
            }
            catch (Exception) { /* not ours to fix */ }
        }
    }

    private static void Junction(string link, string target)
    {
        Directory.CreateDirectory(target);
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "mklink /J didn't create a junction");
    }

    [Fact]
    public void DataFolder_TheProtectedDacl_IsSystemAndAdministratorsOnly_AndProtected()
    {
        var security = DataDirectoryGuard.ProtectedSecurity();

        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.All(rules, r => Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights & FileSystemRights.FullControl));
        Assert.All(rules, r => Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags));
        Assert.Equal(
            new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.OrderBy(s => s.Value),
            rules.Select(r => (SecurityIdentifier)r.IdentityReference).OrderBy(s => s.Value));
    }

    [Fact]
    public void DataFolder_WithoutAdministratorRights_IsLeftAlone_WithAWarning()
    {
        using var temp = new TempDir();
        var guard = new DataDirectoryGuard(elevated: false);

        Assert.True(guard.SecureOwnDirectory(temp.Data));
        Assert.False(Directory.Exists(temp.Data));
        Assert.Contains(guard.Findings, f => f.Level == LogLevel.Warning);
    }

    [Fact]
    public void DataFolder_ThatIsAJunction_IsRenamedAside_AndAFreshOneCreated()
    {
        using var temp = new TempDir();
        var elsewhere = Path.Combine(temp.Root, "elsewhere");
        Junction(temp.Data, elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "proguard.db"), "planted");
        var guard = new DataDirectoryGuard(elevated: true, now: new DateTime(2026, 11, 19, 1, 2, 3)) { ApplyDacl = false, IsTrustedOwner = _ => true };

        Assert.True(guard.SecureOwnDirectory(temp.Data));

        Assert.False(File.GetAttributes(temp.Data).HasFlag(FileAttributes.ReparsePoint));
        Assert.False(File.Exists(Path.Combine(temp.Data, "proguard.db")));
        Assert.True(Directory.Exists(temp.Data + ".untrusted-20261119-010203"));
        Assert.True(Directory.Exists(Path.Combine(temp.Data, "logs")));
        Assert.Contains(guard.Findings, f => f.Level == LogLevel.Error && f.Message.Contains("link"));
    }

    [Fact]
    public void DataFolder_OwnedByAStandardUser_IsRenamedAside_WithEverythingInIt()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Data);
        File.WriteAllText(Path.Combine(temp.Data, "proguard.db"), "planted rules");
        var guard = new DataDirectoryGuard(elevated: true, now: new DateTime(2026, 11, 19, 1, 2, 3)) { ApplyDacl = false, IsTrustedOwner = _ => false };

        Assert.True(guard.SecureOwnDirectory(temp.Data));

        Assert.False(File.Exists(Path.Combine(temp.Data, "proguard.db")));
        Assert.Equal("planted rules", File.ReadAllText(Path.Combine(temp.Data + ".untrusted-20261119-010203", "proguard.db")));
    }

    [Fact]
    public void DataFolder_ALogsJunction_AHardLinkAndAnUntrustedFile_AreMovedOut()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Data);
        Junction(Path.Combine(temp.Data, "logs"), Path.Combine(temp.Root, "redirected"));
        var outside = Path.Combine(temp.Root, "outside.txt");
        File.WriteAllText(outside, "someone else's file");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{Path.Combine(temp.Data, "power-recovery.json")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        File.WriteAllText(Path.Combine(temp.Data, "proguard.db"), "kept");
        var guard = new DataDirectoryGuard(elevated: true, now: new DateTime(2026, 11, 19, 1, 2, 3)) { ApplyDacl = false, IsTrustedOwner = _ => true };

        Assert.True(guard.SecureOwnDirectory(temp.Data));

        var logs = Path.Combine(temp.Data, "logs");
        Assert.True(Directory.Exists(logs));
        Assert.False(File.GetAttributes(logs).HasFlag(FileAttributes.ReparsePoint));
        Assert.False(File.Exists(Path.Combine(temp.Data, "power-recovery.json")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(temp.Data, "proguard.db")));
        var quarantine = Path.Combine(temp.Data, "untrusted-20261119-010203");
        Assert.True(Directory.Exists(Path.Combine(quarantine, "logs")));
        Assert.True(File.Exists(Path.Combine(quarantine, "power-recovery.json")));
        Assert.Equal("someone else's file", File.ReadAllText(outside));
    }

    // Elevated only: once the folder is SYSTEM and Administrators only, an unelevated host can't even list it, as intended.
    [ElevationFact]
    public void DataFolder_ProtectedDacl_IsApplied_AndChildrenLoseTheirOwnEntries()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Data);
        var db = Path.Combine(temp.Data, "proguard.db");
        File.WriteAllText(db, "x");
        var fileSecurity = new FileInfo(db).GetAccessControl();
        fileSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(db).SetAccessControl(fileSecurity);
        var guard = new DataDirectoryGuard(elevated: true) { IsTrustedOwner = _ => true };

        Assert.True(guard.SecureOwnDirectory(temp.Data), string.Join(" | ", guard.Findings.Select(f => f.Message)));

        var folder = new DirectoryInfo(temp.Data).GetAccessControl();
        Assert.True(folder.AreAccessRulesProtected);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        Assert.DoesNotContain(folder.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            r => r.IdentityReference.Equals(users) || r.IdentityReference.Equals(authenticated));
    }

    [Fact]
    public void ADatabaseElsewhere_ThatUsersCanWrite_IsRefused_WhenTheEngineIsElevated()
    {
        using var temp = new TempDir();
        var guard = new DataDirectoryGuard(elevated: true) { IsTrustedOwner = _ => true };

        // A folder in this user's temp: this user (not an administrator's group) has full control of it.
        Assert.False(guard.CheckOtherDirectory(temp.Root));
        Assert.Contains(guard.Findings, f => f.Level == LogLevel.Critical && f.Message.Contains(ProGuardDbContext.DatabasePathVariable));

        Assert.True(new DataDirectoryGuard(elevated: false).CheckOtherDirectory(temp.Root));
    }

    [Fact]
    public void TheInstaller_GivesTheDataFolderAProtectedDacl_WithNothingForUsers()
    {
        var wxs = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "Package.wxs"));
        Assert.Contains("<CreateFolder>", wxs);
        Assert.Contains("Directory Id=\"DATAFOLDER\" Name=\"ProGuard\"", wxs);
        Assert.Contains("Sddl=\"D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)\"", wxs);
        Assert.DoesNotContain(";;;BU)", wxs);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ProGuard.sln"))) return dir.FullName;
        throw new InvalidOperationException("ProGuard.sln not found");
    }

    // ================= 3. The automatic purges are administrator-only =================

    [Theory]
    [InlineData(nameof(EngineSettingsDto.StandbyPurgeEnabled))]
    [InlineData(nameof(EngineSettingsDto.StandbyPurgeFreeRamThresholdMB))]
    [InlineData(nameof(EngineSettingsDto.StandbyPurgeOnlyInPerformanceMode))]
    [InlineData(nameof(EngineSettingsDto.FileCachePurgeEnabled))]
    public void PurgeSettings_OnlyAdministratorsMayChangeThem(string setting)
    {
        Assert.Contains(setting, IpcAuthorization.AdministratorOnlySettings);

        var h = new ThinHarness();
        var settings = h.Data<EngineSettingsDto>(h.SendAsAdmin(CommandTypes.GetSettings));
        var property = typeof(EngineSettingsDto).GetProperty(setting)!;
        property.SetValue(settings, property.PropertyType == typeof(bool) ? !(bool)property.GetValue(settings)! : (object)4096L);

        var refused = h.Send(Standard, CommandTypes.SaveSettings, null, settings);
        Assert.False(refused.Success);
        Assert.StartsWith("Access denied", refused.Error);

        Assert.True(h.SendAsAdmin(CommandTypes.SaveSettings, null, settings).Success);
    }

    // ================= 4. Pipe-name squatting =================

    private static readonly TokenIdentity Me = new("S-1-5-21-1-2-3-1001", Elevated: false);
    private static readonly TokenIdentity MeElevated = new("S-1-5-21-1-2-3-1001", Elevated: true);

    [Fact]
    public void PipeServerCheck_TrustsTheServiceAndElevatedEngines()
    {
        Assert.Null(PipeServerCheck.Problem(TokenIdentity.AdministratorsSid, null, Me)); // a service: the client can't open it
        Assert.Null(PipeServerCheck.Problem(TokenIdentity.LocalSystemSid, new TokenIdentity(TokenIdentity.LocalSystemSid, true), MeElevated));
        Assert.Null(PipeServerCheck.Problem(TokenIdentity.AdministratorsSid, new TokenIdentity("S-1-5-21-1-2-3-500", true), MeElevated));
        // A portable engine run by this same user, unelevated, for an unelevated UI: nothing to gain.
        Assert.Null(PipeServerCheck.Problem(Me.UserSid, Me, Me));
    }

    [Fact]
    public void PipeServerCheck_RefusesASquatter()
    {
        var other = "S-1-5-21-1-2-3-1002";
        // Another user's pipe, whether or not the client may open their process.
        Assert.NotNull(PipeServerCheck.Problem(other, null, Me));
        Assert.NotNull(PipeServerCheck.Problem(other, new TokenIdentity(other, false), MeElevated));
        // This user's own unelevated program, waiting for this user's elevated UI.
        Assert.NotNull(PipeServerCheck.Problem(Me.UserSid, Me, MeElevated));
        // A trusted-looking owner but a server process that isn't.
        Assert.NotNull(PipeServerCheck.Problem(TokenIdentity.AdministratorsSid, new TokenIdentity(other, false), Me));
        // Nothing readable.
        Assert.NotNull(PipeServerCheck.Problem(null, null, Me));
        Assert.NotNull(PipeServerCheck.Problem(TokenIdentity.LocalSystemSid, null, null));
    }

    [Fact]
    public async Task TheCommandPipe_IsCreatedAsTheFirstInstance_AndSaysSoLoudlyWhenTheNameIsTaken()
    {
        var name = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
        var log = new ListLogger<PipeServer>();
        var server = new PipeServer(log, new ThinHarness().Dispatcher) { PipeName = name };
        server.Start();
        try
        {
            Assert.True(await WaitFor(() => { lock (log.Entries) return log.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("another process already owns")); }));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task TheForegroundPipe_IsCreatedAsTheFirstInstance_AndDeniesNetworkLogons()
    {
        var network = ForegroundPipeServer.CreateSecurity().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()
            .Single(r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)));
        Assert.Equal(AccessControlType.Deny, network.AccessControlType);

        var name = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        using var squatter = new NamedPipeServerStream(name, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var log = new ListLogger<ForegroundPipeServer>();
        var server = new ForegroundPipeServer(log, new ForegroundTracker(NullLogger<ForegroundTracker>.Instance)) { PipeName = name };
        server.Start();
        try
        {
            Assert.True(await WaitFor(() => { lock (log.Entries) return log.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("another process already owns")); }));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task TheEngineClient_StillTalksToARealServer_AfterCheckingIt()
    {
        var name = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        var server = new PipeServer(NullLogger<PipeServer>.Instance, new ThinHarness().Dispatcher) { PipeName = name };
        server.Start();
        try
        {
            await using var client = new ProGuard.Shared.Client.EngineClient(name, connectTimeout: TimeSpan.FromSeconds(5));
            Assert.Equal(MessageType.Pong, (await client.SendAsync(new IpcMessage { Type = MessageType.Ping })).Type);
        }
        finally
        {
            server.Stop();
        }
    }

    // ================= 5. The pid can't change hands between the check and the action =================

    [Fact]
    public void AProcessRequest_FromAStandardUser_HoldsThePinUntilTheActionIsDone()
    {
        var h = new ThinHarness();
        var events = new List<string>();
        h.Controller.Setup(c => c.Terminate(4321)).Callback(() => events.Add("terminate"));
        var dispatcher = DispatcherWithPin(h, pid => { events.Add($"pin {pid}"); return new Disposer(() => events.Add("unpin")); });
        var caller = new ThinCaller();
        caller.OpenablePids.Add(4321);

        var reply = dispatcher.Dispatch(new CommandMessage { CommandType = CommandTypes.TerminateProcess, TargetPid = 4321 }, caller);

        Assert.True(JsonSerializer.Deserialize<CommandResult>(reply.Payload)!.Success);
        Assert.Equal(new[] { "pin 4321", "terminate", "unpin" }, events);
        Assert.Equal(4321, caller.Checks.Single().Pid); // the check ran while pinned
    }

    [Fact]
    public void AProcessRequest_ForAProcessThatIsGone_IsRefusedBeforeTheCheck()
    {
        var h = new ThinHarness();
        var dispatcher = DispatcherWithPin(h, _ => null);
        var caller = new ThinCaller();
        caller.OpenablePids.Add(4321);

        var result = JsonSerializer.Deserialize<CommandResult>(dispatcher.Dispatch(new CommandMessage { CommandType = CommandTypes.TerminateProcess, TargetPid = 4321 }, caller).Payload)!;

        Assert.False(result.Success);
        Assert.Contains("not running", result.Error);
        Assert.Empty(caller.Checks);
        h.Controller.Verify(c => c.Terminate(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void TerminateTree_SkipsADescendantWhoseIdNowBelongsToALaterProcess()
    {
        var h = new ThinHarness();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        h.Process(100, "root", start: start);
        h.Process(101, "child", parent: 100, start: start.AddSeconds(1));
        h.Process(102, "child", parent: 100, start: start.AddSeconds(2));
        var actions = new ProcessActionService(NullLogger<ProcessActionService>.Instance, h.Tuning, h.Monitor, h.Controller.Object)
        {
            // 102 exited and its id went to a newer process before it could be pinned.
            PinProcess = pid => TestPins.StartedAt(pid, pid == 102 ? start.AddMinutes(5) : h.Monitor.GetProcess(pid)!.StartTime),
        };
        var caller = new ThinCaller();
        caller.OpenablePids.UnionWith(new[] { 100, 101, 102 });

        Assert.Equal(2, actions.TerminateTree(100, caller));
        h.Controller.Verify(c => c.Terminate(101), Times.Once);
        h.Controller.Verify(c => c.Terminate(102), Times.Never);
    }

    private sealed class Disposer : IDisposable
    {
        private readonly Action _action;
        public Disposer(Action action) => _action = action;
        public void Dispose() => _action();
    }

    private static IpcDispatcher DispatcherWithPin(ThinHarness h, Func<int, IDisposable?> pin)
    {
        var applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, h.Monitor, h.MemoryTrim.Object, h.Power.Object, h.ResponsivenessGuard.Object);
        return new IpcDispatcher(NullLogger<IpcDispatcher>.Instance, h.Store.Object, applier, h.ResponsivenessGuard.Object, h.Power.Object,
            h.MemoryTrim.Object, h.CpuLimiter.Object, h.Controller.Object, h.Monitor) { PinProcess = pin };
    }

    // ================= 6. Every settings route is validated =================

    [Fact]
    public void AMemoryTrimIntervalOfZero_NeverReachesConfigure()
    {
        var h = new ThinHarness();

        var configure = h.Send(Standard, CommandTypes.ConfigureMemoryTrim, null, new MemoryTrimArgument { Enabled = true, IntervalSeconds = 0, ThresholdMb = 100 });
        Assert.False(configure.Success);

        var settings = h.Data<EngineSettingsDto>(h.SendAsAdmin(CommandTypes.GetSettings));
        settings.MemoryTrimEnabled = true;
        settings.MemoryTrimIntervalSeconds = 0;
        Assert.False(h.SendAsAdmin(CommandTypes.SaveSettings, null, settings).Success);

        h.MemoryTrim.Verify(m => m.Configure(It.IsAny<bool>(), TimeSpan.Zero, It.IsAny<long>()), Times.Never);
        Assert.NotEqual(0, h.Stored.MemoryTrimIntervalSeconds);
    }

    [Fact]
    public void ACommandThatChangesOneSetting_IsValidatedLikeSaveSettings()
    {
        var h = new ThinHarness();
        Assert.False(h.Send(Standard, CommandTypes.ConfigureMemoryTrim, null, new MemoryTrimArgument { Enabled = true, IntervalSeconds = 600, ThresholdMb = -1 }).Success);
        Assert.True(h.Send(Standard, CommandTypes.ConfigureMemoryTrim, null, new MemoryTrimArgument { Enabled = true, IntervalSeconds = 600, ThresholdMb = 100 }).Success);
        Assert.Equal(600, h.Stored.MemoryTrimIntervalSeconds);
    }

    // ================= 7. Per-user caps, and profiles belong to who made them =================

    [Fact]
    public async Task OneAccount_CantHoldEveryCommandPipeInstance()
    {
        var name = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        var server = new PipeServer(NullLogger<PipeServer>.Instance, new ThinHarness().Dispatcher)
        {
            PipeName = name,
            AccountOf = _ => "S-1-5-21-same-user",
            MaxPerAccount = 2,
        };
        server.Start();
        var clients = new List<NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                await c.ConnectAsync(5000);
                c.ReadMode = PipeTransmissionMode.Message;
                clients.Add(c);
            }

            // The third is dropped at once; the first two still get answers.
            Assert.True(await ClosedByServer(clients[2]));
            Assert.Equal(MessageType.Pong, await Ping(clients[0]));
            Assert.Equal(MessageType.Pong, await Ping(clients[1]));
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
            server.Stop();
        }
    }

    [Fact]
    public async Task ASilentCommandPipeClient_IsDropped()
    {
        var name = "ProGuardTest_" + Guid.NewGuid().ToString("N");
        var server = new PipeServer(NullLogger<PipeServer>.Instance, new ThinHarness().Dispatcher)
        {
            PipeName = name,
            FirstRequestTimeout = TimeSpan.FromMilliseconds(300),
        };
        server.Start();
        try
        {
            using var silent = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await silent.ConnectAsync(5000);
            Assert.True(await ClosedByServer(silent));
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>True once the server has closed its end: a read then ends (or fails) without data.</summary>
    private static async Task<bool> ClosedByServer(NamedPipeClientStream pipe)
    {
        var buffer = new byte[16];
        try
        {
            var read = await pipe.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            return read == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task<MessageType> Ping(NamedPipeClientStream pipe)
    {
        var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcMessage { Type = MessageType.Ping }));
        var reader = new StreamReader(pipe, leaveOpen: true);
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return JsonSerializer.Deserialize<IpcMessage>(line!)!.Type;
    }

    [Fact]
    public void Profiles_AStandardUserMayChangeOnlyTheirOwn()
    {
        var h = new ProfileHarness();
        var bob = new ThinCaller { Name = @"TEST\bob", Sid = Bob };
        var adminsProfile = h.SaveProfile("Admin's");
        var mine = h.Data<ConfigProfileDto>(h.Send(Standard, CommandTypes.SaveProfile, "Mine")).Id;

        Assert.Equal(Alice, h.Profiles.Named("Mine")!.CreatorSid);

        // Someone else's: no renaming, deleting or overwriting by name.
        foreach (var (caller, id, profileName) in new[] { (Standard, adminsProfile, "Admin's"), (bob, mine, "Mine") })
        {
            Assert.StartsWith("Access denied", h.Send(caller, CommandTypes.RenameProfile, new RenameProfileArgument { Id = id, Name = "Taken" }).Error);
            Assert.StartsWith("Access denied", h.Send(caller, CommandTypes.DeleteProfile, id).Error);
            Assert.StartsWith("Access denied", h.Send(caller, CommandTypes.SaveProfile, profileName).Error);
        }

        // Their own: yes. An administrator: anything.
        Assert.True(h.Send(Standard, CommandTypes.RenameProfile, new RenameProfileArgument { Id = mine, Name = "Still mine" }).Success);
        Assert.True(h.Send(Standard, CommandTypes.SaveProfile, "Still mine").Success);
        Assert.True(h.Admin(CommandTypes.DeleteProfile, mine).Success);
        Assert.True(h.Admin(CommandTypes.RenameProfile, new RenameProfileArgument { Id = adminsProfile, Name = "Renamed" }).Success);
    }

    // ================= 8. The signing dlib is pinned =================

    [Fact]
    public void TheReleaseWorkflow_PinsTheSigningDlib_AndChecksItsHash()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));
        var install = workflow.Split('\n').Single(l => l.Contains("nuget install Microsoft.ArtifactSigning.Client"));

        Assert.Contains("-Version $env:TS_VERSION", install);
        Assert.Matches(@"TS_VERSION: '\d+\.\d+\.\d+'", workflow);
        Assert.Matches(@"TS_SHA512: '[A-Za-z0-9+/]{86}=='", workflow);
        Assert.Contains("Get-FileHash $package -Algorithm SHA512", workflow);
        Assert.Contains("-ne $env:TS_SHA512) { throw", workflow);
    }

    // ================= 9. The elevated sign-in task only for a protected copy =================

    private sealed class FakeElevation : IElevationLauncher
    {
        public bool IsElevated { get; init; } = true;
        public bool AlwaysElevate { get; set; } = true;
        public ElevationOutcome Restart(IReadOnlyList<string> arguments, out string? error)
        {
            error = null;
            return default;
        }
    }

    private sealed class FakeScheduler : ITaskScheduler
    {
        public Dictionary<string, string> Tasks { get; } = new();
        public string? Query(string name) => Tasks.GetValueOrDefault(name);
        public void Create(string name, string xml) => Tasks[name] = xml;
        public void Delete(string name) => Tasks.Remove(name);
    }

    private sealed class FakeRunKey : IStartupRegistration
    {
        public bool IsEnabled { get; set; }
        public void Enable() => IsEnabled = true;
        public void Disable() => IsEnabled = false;
    }

    [Fact]
    public void SignIn_AReplaceableCopy_GetsTheRunKey_NotAnElevatedTask_AndSaysWhy()
    {
        var tasks = new FakeScheduler();
        var runKey = new FakeRunKey();
        var startup = new SignInStartup(new FakeElevation(), runKey, tasks, @"C:\Users\me\Desktop\ProGuard\ProGuard.UI.exe", "S-1-5-21-9",
            whyReplaceable: _ => @"C:\Users\me\Desktop\ProGuard: me may change it");

        startup.Enable();

        Assert.Empty(tasks.Tasks);
        Assert.True(runKey.IsEnabled);
        Assert.Contains("me may change it", startup.Notice);
    }

    [Fact]
    public void SignIn_AnExistingTaskForAReplaceableCopy_IsRemovedOnTheNextElevatedStart()
    {
        var tasks = new FakeScheduler();
        var exe = @"C:\Users\me\Desktop\ProGuard\ProGuard.UI.exe";
        var startup = new SignInStartup(new FakeElevation(), new FakeRunKey(), tasks, exe, "S-1-5-21-9", whyReplaceable: _ => "writable");
        tasks.Tasks[startup.TaskName] = SignInStartup.TaskXml("S-1-5-21-9", exe);

        startup.Reconcile();

        Assert.Empty(tasks.Tasks);
        Assert.True(startup.IsEnabled); // still starts at sign-in, through the Run key
    }

    [Fact]
    public void SignIn_AProtectedCopy_StillGetsTheTask()
    {
        var tasks = new FakeScheduler();
        var startup = new SignInStartup(new FakeElevation(), new FakeRunKey(), tasks, @"C:\Program Files\ProGuard\ProGuard.UI.exe", "S-1-5-21-9", whyReplaceable: _ => null);

        startup.Enable();

        Assert.Single(tasks.Tasks);
        Assert.Null(startup.Notice);
    }

    [Fact]
    public void DirectoryTrust_SystemFilesAreProtected_AndATempFileIsNot()
    {
        Assert.Null(DirectoryTrust.WhyReplaceable(Path.Combine(Environment.SystemDirectory, "cmd.exe")));

        var temp = Path.Combine(Path.GetTempPath(), "ProGuard.Tests", "trust-" + Guid.NewGuid().ToString("N"), "ProGuard.UI.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllText(temp, "not really");
        try
        {
            Assert.NotNull(DirectoryTrust.WhyReplaceable(temp));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(temp)!, recursive: true); } catch { /* best effort */ }
        }
    }

    // ================= helpers =================

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
