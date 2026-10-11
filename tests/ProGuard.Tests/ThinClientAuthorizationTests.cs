using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Moq;
using ProGuard.Core.Native;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

/// <summary>
/// Every command is classified on purpose: open to any user, open only for a process the caller could open, or
/// administrators only. A command added to <see cref="CommandTypes"/> without a row here fails the first test.
/// </summary>
public class ThinClientAuthorizationTests
{
    private const int Pid = 4321;

    private static readonly string[] OpenToAnyUser =
    {
        CommandTypes.EnableProBalance, CommandTypes.DisableProBalance, CommandTypes.SetSensitivity,
        CommandTypes.ConfigureIdleSaver, CommandTypes.ConfigureSmartTrim,
        CommandTypes.EnablePerformanceMode, CommandTypes.DisablePerformanceMode,
        CommandTypes.GetSnapshot, CommandTypes.GetProcessRules, CommandTypes.GetWatchdogRules, CommandTypes.GetSettings,
        CommandTypes.GetPower, CommandTypes.GetActivity, CommandTypes.GetCpuSets,
        CommandTypes.SaveSettings, CommandTypes.SetPowerPlan,
        // Open here; the dispatcher then checks the rule itself (see RuleEditAuthorizationTests).
        CommandTypes.SaveProcessRule, CommandTypes.DeleteProcessRule, CommandTypes.SaveWatchdogRule, CommandTypes.DeleteWatchdogRule,
    };

    /// <summary>Command, argument that is valid for it, and the access the caller must have to the target.</summary>
    public static IEnumerable<object?[]> ProcessCommands() => new[]
    {
        new object?[] { CommandTypes.SetPriority, "BelowNormal", ProcessAccessFlags.SetInformation },
        new object?[] { CommandTypes.SetAffinity, "0x3", ProcessAccessFlags.SetInformation },
        new object?[] { CommandTypes.SetCpuLimit, "25", ProcessAccessFlags.SetQuota | ProcessAccessFlags.Terminate },
        new object?[] { CommandTypes.RemoveCpuLimit, null, ProcessAccessFlags.SetQuota | ProcessAccessFlags.Terminate },
        new object?[] { CommandTypes.TerminateProcess, null, ProcessAccessFlags.Terminate },
        new object?[] { CommandTypes.SuspendProcess, null, ProcessAccessFlags.SuspendResume },
        new object?[] { CommandTypes.ResumeProcess, null, ProcessAccessFlags.SuspendResume },
        new object?[] { CommandTypes.TrimWorkingSet, null, ProcessAccessFlags.SetQuota | ProcessAccessFlags.QueryLimitedInformation },
        new object?[] { CommandTypes.SetEfficiencyMode, "true", ProcessAccessFlags.SetInformation },
        new object?[] { CommandTypes.SetIoPriority, "1", ProcessAccessFlags.SetInformation },
        new object?[] { CommandTypes.SetMemoryPriority, "3", ProcessAccessFlags.SetInformation },
        new object?[] { CommandTypes.TerminateProcessTree, null, ProcessAccessFlags.Terminate },
    };

    private static readonly string[] AdministratorsOnly = { CommandTypes.PurgeMemory, CommandTypes.ClearActionLog };

    // Sent by the UI on the foreground pipe only; the command pipe refuses it from standard users.
    private static readonly string[] NotForThisPipe = { CommandTypes.ReportForeground };

    private static IEnumerable<string> AllCommands() =>
        typeof(CommandTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    private static bool Denied(CommandResult r) => !r.Success && r.Error!.StartsWith("Access denied", StringComparison.Ordinal);

    [Fact]
    public void EveryCommandIsClassified()
    {
        var classified = OpenToAnyUser
            .Concat(ProcessCommands().Select(c => (string)c[0]!))
            .Concat(AdministratorsOnly)
            .Concat(NotForThisPipe)
            .ToHashSet();

        var unclassified = AllCommands().Where(c => !classified.Contains(c)).ToList();

        Assert.True(unclassified.Count == 0, "Classify in IpcAuthorization and in this test: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void ThePolicyTables_NameOnlyRealCommands()
    {
        var all = AllCommands().ToHashSet();
        Assert.All(OpenToAnyUser.Concat(AdministratorsOnly).Concat(NotForThisPipe), c => Assert.Contains(c, all));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpenCommands_AreNotRefusedForAnyUser(bool administrator)
    {
        var h = new ThinHarness();
        var caller = new ThinCaller { IsAdministrator = administrator };

        foreach (var command in OpenToAnyUser)
        {
            var result = h.Send(caller, command);
            Assert.False(Denied(result), $"{command} was refused for {(administrator ? "an administrator" : "a standard user")}: {result.Error}");
        }
        Assert.Empty(caller.Checks);
    }

    [Fact]
    public void AdministratorOnlyCommands_AreRefusedForAStandardUser_AndRunForAnAdministrator()
    {
        var h = new ThinHarness();
        foreach (var command in AdministratorsOnly)
        {
            var refused = h.Send(new ThinCaller(), command);
            Assert.True(Denied(refused), command);
            Assert.Contains(command, refused.Error);

            Assert.False(Denied(h.Send(ThinCaller.Admin(), command)), command);
        }
        h.SmartTrim.Verify(t => t.PurgeNow(), Times.Once);
    }

    [Theory]
    [MemberData(nameof(ProcessCommands))]
    public void ProcessCommands_NeedTheRightAccessToTheTarget(string command, string? argument, ProcessAccessFlags access)
    {
        var h = new ThinHarness();
        h.Process(Pid, "target");

        var stranger = new ThinCaller();
        var refused = h.Send(stranger, command, Pid, argument);
        Assert.True(Denied(refused), $"{command}: {refused.Error}");
        Assert.Contains(Pid.ToString(), refused.Error);
        Assert.Equal(new[] { (Pid, access) }, stranger.Checks);

        var owner = new ThinCaller();
        owner.OpenablePids.Add(Pid);
        var allowed = h.Send(owner, command, Pid, argument);
        Assert.False(Denied(allowed), $"{command}: {allowed.Error}");
        Assert.Equal(new[] { (Pid, access) }, owner.Checks.Take(1));

        // Administrators are never asked.
        var admin = ThinCaller.Admin();
        Assert.False(Denied(h.Send(admin, command, Pid, argument)));
        Assert.Empty(admin.Checks);
    }

    [Fact]
    public void ARefusedProcessCommand_ChangesNothing()
    {
        var h = new ThinHarness();
        var caller = new ThinCaller();

        foreach (var command in new[] { CommandTypes.SetEfficiencyMode, CommandTypes.SetIoPriority, CommandTypes.SetMemoryPriority, CommandTypes.TerminateProcessTree })
            h.Send(caller, command, Pid, "1");

        Assert.Empty(h.Tuning.Writes);
        h.Controller.Verify(c => c.Terminate(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void StandardUser_MayNotSetHighIoPriority_EvenOnTheirOwnProcess_ButAdministratorsMay()
    {
        var h = new ThinHarness();
        var caller = new ThinCaller();
        caller.OpenablePids.Add(Pid);

        var refused = h.Send(caller, CommandTypes.SetIoPriority, Pid, "3");
        Assert.True(Denied(refused));
        Assert.Contains("High I/O priority", refused.Error);
        Assert.False(h.Tuning.Io.ContainsKey(Pid));

        Assert.True(h.Send(caller, CommandTypes.SetIoPriority, Pid, "2").Success);
        Assert.True(h.Send(ThinCaller.Admin(), CommandTypes.SetIoPriority, Pid, "3").Success);
        Assert.Equal(3, h.Tuning.Io[Pid]);
    }

    [Fact]
    public void ForegroundReports_AreNotAcceptedOnTheCommandPipe()
    {
        var h = new ThinHarness();
        Assert.True(Denied(h.Send(new ThinCaller(), CommandTypes.ReportForeground, Pid)));
    }

    [Fact]
    public void ProcessCommands_WithoutAPid_GetTheUsualValidationErrorInsteadOfAnAccessCheck()
    {
        var h = new ThinHarness();
        var caller = new ThinCaller();

        var result = h.Send(caller, CommandTypes.SetEfficiencyMode, null, "true");

        Assert.False(result.Success);
        Assert.Contains("needs a TargetPid", result.Error);
        Assert.Empty(caller.Checks);
    }
}
