using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

/// <summary>
/// On an installed system only SYSTEM and administrators can write the database, so the unelevated UI saves rules
/// through the engine. A standard user may edit rules, but not put in one what they couldn't do themselves, and not
/// lift a rule an administrator made.
/// </summary>
public class RuleEditAuthorizationTests
{
    private static readonly ThinCaller Standard = new();
    private static readonly ThinCaller Admin = ThinCaller.Admin();

    private static void AssertDenied(CommandResult result, string reason)
    {
        Assert.False(result.Success);
        Assert.StartsWith("Access denied", result.Error);
        Assert.Contains(reason, result.Error);
    }

    private static ProcessRuleDto Rule(string name, Action<ProcessRuleDto>? configure = null)
    {
        var rule = new ProcessRuleDto { ProcessName = name };
        configure?.Invoke(rule);
        return rule;
    }

    private static WatchdogRuleDto Watch(string name, WatchdogResponse action, string? argument = null) => new()
    {
        TargetProcessName = name, ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 50, DurationSeconds = 5,
        ActionType = action, ActionArgument = argument,
    };

    [Fact]
    public void StandardUser_SavesAndDeletesAnOrdinaryRule()
    {
        var h = new ThinHarness();

        var saved = h.Send(Standard, CommandTypes.SaveProcessRule, null,
            Rule("game.exe", r => { r.PriorityClass = (int)ProcessPriorityClass.High; r.AffinityMask = 0xF; r.IoPriority = 1; }));

        var dto = h.Data<ProcessRuleDto>(saved);
        var rule = Assert.Single(h.Rules);
        Assert.Equal(rule.Id, dto.Id);
        Assert.Equal(0xF, rule.AffinityMask);

        Assert.True(h.Send(Standard, CommandTypes.DeleteProcessRule, null, rule.Id).Success);
        Assert.Empty(h.Rules);
    }

    [Fact]
    public void StandardUser_SavesAndDeletesAnOrdinaryWatchdogRule()
    {
        var h = new ThinHarness();

        var dto = h.Data<WatchdogRuleDto>(h.Send(Standard, CommandTypes.SaveWatchdogRule, null, Watch("app.exe", WatchdogResponse.LogAlert)));
        Assert.Single(h.WatchdogRules);
        Assert.True(h.Send(Standard, CommandTypes.DeleteWatchdogRule, null, dto.Id).Success);
        Assert.Empty(h.WatchdogRules);
    }

    [Fact]
    public void StandardUser_MayNotPutRealtimeInARule()
    {
        var h = new ThinHarness();
        AssertDenied(h.Send(Standard, CommandTypes.SaveProcessRule, null, Rule("game.exe", r => r.PriorityClass = (int)ProcessPriorityClass.RealTime)), "Realtime");
        AssertDenied(h.Send(Standard, CommandTypes.SaveProcessRule, null, Rule("game.exe", r => r.StartupBoostPriorityClass = (int)ProcessPriorityClass.RealTime)), "Realtime");
        AssertDenied(h.Send(Standard, CommandTypes.SaveWatchdogRule, null, Watch("game.exe", WatchdogResponse.SetPriority, "RealTime")), "Realtime");
        Assert.Empty(h.Rules);
        Assert.Empty(h.WatchdogRules);

        Assert.True(h.Send(Admin, CommandTypes.SaveProcessRule, null, Rule("game.exe", r => r.PriorityClass = (int)ProcessPriorityClass.RealTime)).Success);
    }

    [Fact]
    public void StandardUser_MayNotRuleSystemProcesses()
    {
        var h = new ThinHarness();
        AssertDenied(h.Send(Standard, CommandTypes.SaveProcessRule, null, Rule("lsass.exe", r => r.IoPriority = 0)), "system process");
        AssertDenied(h.Send(Standard, CommandTypes.SaveWatchdogRule, null, Watch("csrss", WatchdogResponse.LogAlert)), "system process");
        Assert.Empty(h.Rules);
        Assert.Empty(h.WatchdogRules);
    }

    [Fact]
    public void RulesThatEndProcesses_NeedTheRightToEndEveryRunningInstance()
    {
        var h = new ThinHarness();
        h.Process(50, "victim");
        var caller = new ThinCaller();

        AssertDenied(h.Send(caller, CommandTypes.SaveProcessRule, null, Rule("victim.exe", r => r.DisallowExecution = true)), "pid 50");
        AssertDenied(h.Send(caller, CommandTypes.SaveProcessRule, null, Rule("victim.exe", r => r.MaxInstances = 1)), "pid 50");
        AssertDenied(h.Send(caller, CommandTypes.SaveWatchdogRule, null, Watch("victim.exe", WatchdogResponse.Terminate)), "pid 50");
        AssertDenied(h.Send(caller, CommandTypes.SaveWatchdogRule, null, Watch("victim.exe", WatchdogResponse.Restart)), "pid 50");
        Assert.Empty(h.Rules);

        // Their own instance: fine.
        caller.OpenablePids.Add(50);
        Assert.True(h.Send(caller, CommandTypes.SaveProcessRule, null, Rule("victim.exe", r => r.DisallowExecution = true)).Success);
    }

    [Fact]
    public void StandardUser_MayNotCreateWatchdogLaunchActions()
    {
        var h = new ThinHarness();
        var launch = Watch("app.exe", WatchdogResponse.Launch, "calc.exe");

        AssertDenied(h.Send(Standard, CommandTypes.SaveWatchdogRule, null, launch), "Launch");
        Assert.Empty(h.WatchdogRules);
        Assert.True(h.Send(Admin, CommandTypes.SaveWatchdogRule, null, launch).Success);
    }

    [Fact]
    public void StandardUser_MayNotLiftOrRewriteAnAdministratorsRule()
    {
        var h = new ThinHarness();
        h.Process(50, "victim");
        var processRule = h.Data<ProcessRuleDto>(h.Send(Admin, CommandTypes.SaveProcessRule, null, Rule("victim.exe", r => r.DisallowExecution = true)));
        var watchdogRule = h.Data<WatchdogRuleDto>(h.Send(Admin, CommandTypes.SaveWatchdogRule, null, Watch("app.exe", WatchdogResponse.Launch, "tool.exe")));

        AssertDenied(h.Send(Standard, CommandTypes.DeleteProcessRule, null, processRule.Id), "victim.exe");
        // Rewriting it by id, or by saving a harmless rule under the same name, would remove the Disallow.
        processRule.DisallowExecution = false;
        AssertDenied(h.Send(Standard, CommandTypes.SaveProcessRule, null, processRule), "victim.exe");
        AssertDenied(h.Send(Standard, CommandTypes.SaveProcessRule, null, Rule("victim.exe")), "victim.exe");
        AssertDenied(h.Send(Standard, CommandTypes.DeleteWatchdogRule, null, watchdogRule.Id), "Launch");
        watchdogRule.ActionType = WatchdogResponse.LogAlert;
        watchdogRule.ActionArgument = null;
        AssertDenied(h.Send(Standard, CommandTypes.SaveWatchdogRule, null, watchdogRule), "Launch");

        Assert.True(h.Rules.Single().DisallowExecution);
        Assert.Equal(WatchdogAction.Launch, h.WatchdogRules.Single().ActionType);
    }

    [Fact]
    public void ADenied_Save_ChangesNothingAndStartsNothing()
    {
        var h = new ThinHarness();
        h.Process(50, "victim");

        h.Send(Standard, CommandTypes.SaveProcessRule, null, Rule("victim.exe", r => r.DisallowExecution = true));

        h.Store.Verify(s => s.SaveProcessRule(It.IsAny<ProcessRule>()), Times.Never);
        h.Access.Verify(a => a.Terminate(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void ClearingTheActionLog_IsForAdministrators()
    {
        var h = new ThinHarness();
        AssertDenied(h.Send(Standard, CommandTypes.ClearActionLog), "clear the action log");
        h.Store.Verify(s => s.ClearProBalanceActions(), Times.Never);

        Assert.True(h.Send(Admin, CommandTypes.ClearActionLog).Success);
        h.Store.Verify(s => s.ClearProBalanceActions(), Times.Once);
    }
}
