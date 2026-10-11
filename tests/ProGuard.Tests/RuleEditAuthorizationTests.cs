using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Shared.Messages;
using ProGuard.UI.IPC;
using ProGuard.UI.Services;

namespace ProGuard.Tests;

/// <summary>
/// On an installed system only SYSTEM and administrators can write the database, so the unelevated UI saves rules and
/// settings through the engine. A standard user may edit rules, but not put in one what they couldn't do themselves,
/// and not lift a rule an administrator made.
/// </summary>
public class RuleEditAuthorizationTests
{
    private sealed class Caller : IIpcCaller
    {
        public string Name { get; init; } = @"TEST\standard";
        public bool IsAdministrator { get; init; }
        public HashSet<int> OpenablePids { get; } = new();
        public bool CanOpenProcess(int pid, ProcessAccessFlags access) => OpenablePids.Contains(pid);
    }

    private sealed class Harness
    {
        public List<ProcessRule> ProcessRules { get; } = new();
        public List<WatchdogRule> WatchdogRules { get; } = new();
        public List<ProcessInfo> Processes { get; } = new();
        public AppSettings Stored { get; private set; } = new() { Id = 1 };
        public Mock<IRuleStore> Store { get; } = new();
        public IpcDispatcher Dispatcher { get; }
        private int _nextId = 100;

        public Harness()
        {
            Store.Setup(s => s.GetProcessRules()).Returns(() => ProcessRules.ToList());
            Store.Setup(s => s.GetProcessRule(It.IsAny<string>())).Returns((string name) =>
                ProcessRules.FirstOrDefault(r => ProGuard.Core.Helpers.ProcessExtensions.NormalizeName(r.ProcessName)
                                                 == ProGuard.Core.Helpers.ProcessExtensions.NormalizeName(name)));
            Store.Setup(s => s.SaveProcessRule(It.IsAny<ProcessRule>())).Callback<ProcessRule>(r =>
            {
                ProcessRules.RemoveAll(x => x.Id == r.Id || x.ProcessName == r.ProcessName);
                if (r.Id == 0) r.Id = _nextId++;
                ProcessRules.Add(r);
            });
            Store.Setup(s => s.DeleteProcessRule(It.IsAny<int>())).Callback<int>(id => ProcessRules.RemoveAll(r => r.Id == id));
            Store.Setup(s => s.GetWatchdogRules()).Returns(() => WatchdogRules.ToList());
            Store.Setup(s => s.AddWatchdogRule(It.IsAny<WatchdogRule>())).Callback<WatchdogRule>(r => { r.Id = _nextId++; WatchdogRules.Add(r); });
            Store.Setup(s => s.UpdateWatchdogRule(It.IsAny<WatchdogRule>())).Callback<WatchdogRule>(r =>
            {
                WatchdogRules.RemoveAll(x => x.Id == r.Id);
                WatchdogRules.Add(r);
            });
            Store.Setup(s => s.DeleteWatchdogRule(It.IsAny<int>())).Callback<int>(id => WatchdogRules.RemoveAll(r => r.Id == id));
            Store.Setup(s => s.GetSettings()).Returns(() => Stored);
            Store.Setup(s => s.UpdateSettings(It.IsAny<AppSettings>())).Callback<AppSettings>(s => Stored = s);

            var monitor = new Mock<IProcessMonitor>();
            monitor.Setup(m => m.GetAllProcesses()).Returns(() => Processes.ToList());
            var smartTrim = new Mock<ISmartTrimService>();
            var power = new Mock<IPowerManager>();
            var proBalance = new Mock<IProBalanceEngine>();
            var applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, monitor.Object, smartTrim.Object, power.Object, proBalance.Object);
            Dispatcher = new IpcDispatcher(NullLogger<IpcDispatcher>.Instance, Store.Object, applier, proBalance.Object, power.Object,
                smartTrim.Object, new Mock<ICpuLimiter>().Object, new Mock<IProcessController>().Object, monitor.Object);
        }

        public CommandResult Command(IIpcCaller caller, string command, object? argument)
        {
            var text = argument as string ?? (argument == null ? null : JsonSerializer.Serialize(argument));
            var reply = Dispatcher.Dispatch(new CommandMessage { CommandType = command, Argument = text }, caller);
            return JsonSerializer.Deserialize<CommandResult>(reply.Payload)!;
        }

        public CommandResult SetSettings(IIpcCaller caller, string json)
        {
            var reply = Dispatcher.Dispatch(new CommandMessage { Type = MessageType.SetSetting, Payload = json }, caller);
            return JsonSerializer.Deserialize<CommandResult>(reply.Payload)!;
        }

        /// <summary>A running process named <paramref name="name"/> that belongs to someone else.</summary>
        public void RunsAsAnotherUser(int pid, string name) => Processes.Add(new ProcessInfo { Pid = pid, Name = name });
    }

    private static readonly Caller Standard = new();
    private static readonly Caller Admin = new() { Name = @"TEST\admin", IsAdministrator = true };

    private static void AssertDenied(CommandResult result, string reason)
    {
        Assert.False(result.Success);
        Assert.StartsWith("Access denied", result.Error);
        Assert.Contains(reason, result.Error);
    }

    [Fact]
    public void StandardUser_SavesAndDeletesAnOrdinaryRule()
    {
        var h = new Harness();

        var saved = h.Command(Standard, CommandTypes.SaveProcessRule,
            new ProcessRule { ProcessName = "game.exe", PriorityClass = (int)ProcessPriorityClass.High, AffinityMask = 0xF, IoPriority = 1 });

        Assert.True(saved.Success, saved.Error);
        var rule = Assert.Single(h.ProcessRules);
        Assert.Equal(rule.Id.ToString(), saved.Data);
        Assert.Equal(0xF, rule.AffinityMask);

        Assert.True(h.Command(Standard, CommandTypes.DeleteProcessRule, rule.Id.ToString()).Success);
        Assert.Empty(h.ProcessRules);
    }

    [Fact]
    public void StandardUser_MayNotPutRealtimeInARule()
    {
        var h = new Harness();
        AssertDenied(h.Command(Standard, CommandTypes.SaveProcessRule,
            new ProcessRule { ProcessName = "game.exe", PriorityClass = (int)ProcessPriorityClass.RealTime }), "Realtime");
        AssertDenied(h.Command(Standard, CommandTypes.SaveProcessRule,
            new ProcessRule { ProcessName = "game.exe", StartupBoostPriorityClass = (int)ProcessPriorityClass.RealTime }), "Realtime");
        AssertDenied(h.Command(Standard, CommandTypes.SaveWatchdogRule,
            new WatchdogRule { TargetProcessName = "game.exe", ActionType = WatchdogAction.SetPriority, ActionArgument = "RealTime" }), "Realtime");
        Assert.Empty(h.ProcessRules);
        Assert.Empty(h.WatchdogRules);

        Assert.True(h.Command(Admin, CommandTypes.SaveProcessRule,
            new ProcessRule { ProcessName = "game.exe", PriorityClass = (int)ProcessPriorityClass.RealTime }).Success);
    }

    [Fact]
    public void StandardUser_MayNotRuleSystemProcesses()
    {
        var h = new Harness();
        AssertDenied(h.Command(Standard, CommandTypes.SaveProcessRule, new ProcessRule { ProcessName = "lsass.exe", IoPriority = 0 }), "system process");
        AssertDenied(h.Command(Standard, CommandTypes.SaveWatchdogRule,
            new WatchdogRule { TargetProcessName = "csrss", ActionType = WatchdogAction.LogAlert }), "system process");
    }

    [Fact]
    public void RulesThatEndProcesses_NeedTheRightToEndEveryRunningInstance()
    {
        var h = new Harness();
        h.RunsAsAnotherUser(50, "victim");
        var caller = new Caller();

        AssertDenied(h.Command(caller, CommandTypes.SaveProcessRule, new ProcessRule { ProcessName = "victim.exe", DisallowExecution = true }), "pid 50");
        AssertDenied(h.Command(caller, CommandTypes.SaveProcessRule, new ProcessRule { ProcessName = "victim.exe", MaxInstances = 1 }), "pid 50");
        AssertDenied(h.Command(caller, CommandTypes.SaveWatchdogRule,
            new WatchdogRule { TargetProcessName = "victim.exe", ActionType = WatchdogAction.Terminate }), "pid 50");
        AssertDenied(h.Command(caller, CommandTypes.SaveWatchdogRule,
            new WatchdogRule { TargetProcessName = "victim.exe", ActionType = WatchdogAction.Restart }), "pid 50");

        // Their own instance: fine.
        caller.OpenablePids.Add(50);
        Assert.True(h.Command(caller, CommandTypes.SaveProcessRule, new ProcessRule { ProcessName = "victim.exe", DisallowExecution = true }).Success);
    }

    [Fact]
    public void StandardUser_MayNotCreateWatchdogLaunchActions()
    {
        var h = new Harness();
        var launch = new WatchdogRule { TargetProcessName = "app.exe", ConditionType = WatchdogCondition.RunningLongerThan, ActionType = WatchdogAction.Launch, ActionArgument = "calc.exe" };

        AssertDenied(h.Command(Standard, CommandTypes.SaveWatchdogRule, launch), "Launch");
        Assert.Empty(h.WatchdogRules);
        Assert.True(h.Command(Admin, CommandTypes.SaveWatchdogRule, launch).Success);
    }

    [Fact]
    public void StandardUser_MayNotLiftOrRewriteAnAdministratorsRule()
    {
        var h = new Harness();
        h.RunsAsAnotherUser(50, "victim");
        Assert.True(h.Command(Admin, CommandTypes.SaveProcessRule, new ProcessRule { ProcessName = "victim.exe", DisallowExecution = true }).Success);
        Assert.True(h.Command(Admin, CommandTypes.SaveWatchdogRule,
            new WatchdogRule { TargetProcessName = "app.exe", ActionType = WatchdogAction.Launch, ActionArgument = "tool.exe" }).Success);
        var processRule = h.ProcessRules.Single();
        var watchdogRule = h.WatchdogRules.Single();

        AssertDenied(h.Command(Standard, CommandTypes.DeleteProcessRule, processRule.Id.ToString()), "victim.exe");
        // Saving a harmless rule under the same name would replace (and so remove) the Disallow.
        AssertDenied(h.Command(Standard, CommandTypes.SaveProcessRule, new ProcessRule { ProcessName = "victim.exe" }), "victim.exe");
        AssertDenied(h.Command(Standard, CommandTypes.DeleteWatchdogRule, watchdogRule.Id.ToString()), "Launch");
        AssertDenied(h.Command(Standard, CommandTypes.SaveWatchdogRule,
            new WatchdogRule { Id = watchdogRule.Id, TargetProcessName = "app.exe", ActionType = WatchdogAction.LogAlert }), "Launch");

        Assert.True(h.ProcessRules.Single().DisallowExecution);
        Assert.Equal(WatchdogAction.Launch, h.WatchdogRules.Single().ActionType);
    }

    [Fact]
    public void ClearingTheActionLog_IsForAdministrators()
    {
        var h = new Harness();
        AssertDenied(h.Command(Standard, CommandTypes.ClearActionLog, null), "ClearActionLog");
        h.Store.Verify(s => s.ClearProBalanceActions(), Times.Never);

        Assert.True(h.Command(Admin, CommandTypes.ClearActionLog, null).Success);
        h.Store.Verify(s => s.ClearProBalanceActions(), Times.Once);
    }

    [Fact]
    public void SetSetting_ChangesOnlyTheNamedSettings()
    {
        var h = new Harness();
        h.Stored.ProBalanceSensitivity = 7;
        h.Stored.SmartTrimExclusions = "keep.exe";

        var result = h.SetSettings(Standard, """{"IdleSaverEnabled": true, "IdleTimeoutMinutes": 3, "Id": 99}""");

        Assert.True(result.Success, result.Error);
        Assert.True(h.Stored.IdleSaverEnabled);
        Assert.Equal(3, h.Stored.IdleTimeoutMinutes);
        Assert.Equal(7, h.Stored.ProBalanceSensitivity);
        Assert.Equal("keep.exe", h.Stored.SmartTrimExclusions);
        Assert.Equal(1, h.Stored.Id);
    }

    // ---------- the UI side ----------

    [Fact]
    public void ConfigWriter_SendsOnlyTheSettingsThatChanged()
    {
        var stored = new AppSettings { Id = 1, ProBalanceSensitivity = 7 };
        var updated = stored.Clone();
        updated.ProBalanceSensitivity = 7; // unchanged
        updated.IdleSaverEnabled = true;
        updated.SmartTrimExclusions = "a.exe";

        var changes = ConfigWriter.ChangedProperties(stored, updated);

        Assert.Equal(new[] { nameof(AppSettings.IdleSaverEnabled), nameof(AppSettings.SmartTrimExclusions) }, changes.Keys.OrderBy(k => k));
    }

    [Fact]
    public async Task ConfigWriter_WithNoEngine_WritesTheDatabaseItself()
    {
        // TestEnvironment points the default pipe at one no engine serves, and the database at a temp file.
        var writer = new ConfigWriter(new EngineProxy());
        var name = $"cfg{Guid.NewGuid():N}.exe";

        var saved = await writer.SaveProcessRuleAsync(new ProcessRule { ProcessName = name, IoPriority = 1 });
        Assert.True(saved.Success, saved.Error);
        Assert.NotNull(saved.Id);

        var updated = await writer.UpdateProcessRuleAsync(name, r => { r.KeepRunning = true; return true; });
        Assert.True(updated.Success, updated.Error);
        Assert.Equal(saved.Id, updated.Id);

        var settings = await writer.UpdateSettingsAsync(s => s.SmartTrimLogNoAction = true);
        Assert.True(settings.Success, settings.Error);

        using var db = new ProGuardDbContext();
        var rule = db.ProcessRules.Single(r => r.ProcessName == name);
        Assert.True(rule.KeepRunning);
        Assert.Equal(1, rule.IoPriority);
        Assert.True(db.Settings.First().SmartTrimLogNoAction);

        Assert.True((await writer.DeleteProcessRuleAsync(rule.Id)).Success);
    }
}
