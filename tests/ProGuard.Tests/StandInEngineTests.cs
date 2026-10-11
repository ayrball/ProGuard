using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ProGuard.Preview;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ActivityKind = ProGuard.Shared.Contracts.ActivityKind;

namespace ProGuard.Tests;

/// <summary>
/// The preview tool's stand-in engine (tools/ProGuard.Preview) has to keep up with the contract: every command in
/// <see cref="CommandTypes"/> gets an answer, and what comes back deserializes into the contract types through the
/// real <see cref="EngineClient"/>. Adding a command to the contract fails these tests until the stand-in answers it.
/// </summary>
public class StandInEngineTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public StandInEngine Engine { get; }
        public EngineClient Client { get; }

        public Rig(bool administrator = true, bool readOnly = false)
        {
            Engine = new StandInEngine("ProGuard.Tests.StandIn." + Guid.NewGuid().ToString("N"), administrator, readOnly).Start();
            Client = new EngineClient(Engine.PipeName);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Engine.DisposeAsync();
        }
    }

    // Pids from the stand-in's machine (StandInData.Processes).
    private const int Code = 9420;           // the foreground editor, owned by the user
    private const int Discord = 8812;        // capped at 10% by a rule
    private const int Spotify = 6320;
    private const int Steam = 4400;          // suspended
    private const int Chrome = 7040;         // has child processes
    private const int Notepad = 12040;
    private const int Lsass = 696;           // a system process: not the standard user's to touch

    // ------------------------------------------------------------------ every command

    /// <summary>One valid request per command, where the command needs a process or an argument.</summary>
    private static (int? Pid, object? Argument) Sample(string command) => command switch
    {
        CommandTypes.SetPriority => (Code, "BelowNormal"),
        CommandTypes.SetAffinity => (Code, "0xFF"),
        CommandTypes.SetCpuLimit => (Code, 25),
        CommandTypes.RemoveCpuLimit => (Discord, null),
        CommandTypes.TerminateProcess => (Notepad, null),
        CommandTypes.SuspendProcess => (Spotify, null),
        CommandTypes.ResumeProcess => (Steam, null),
        CommandTypes.TrimWorkingSet => (Code, null),
        CommandTypes.SetSensitivity => (null, 7),
        CommandTypes.ConfigureIdleSaver => (null, new IdleSaverArgument { Enabled = true, TimeoutMinutes = 10, PlanGuid = StandInData.PowerSaver.ToString() }),
        CommandTypes.ConfigureSmartTrim => (null, new SmartTrimArgument { Enabled = true, IntervalSeconds = 600, ThresholdMb = 512 }),
        CommandTypes.ReportForeground => (Code, 1500),
        CommandTypes.EnablePerformanceMode => (null, true),
        CommandTypes.DisablePerformanceMode => (null, null),
        CommandTypes.SetEfficiencyMode => (Spotify, true),
        CommandTypes.SetIoPriority => (Spotify, 1),
        CommandTypes.SetMemoryPriority => (Spotify, 3),
        CommandTypes.TerminateProcessTree => (Chrome, null),
        CommandTypes.SaveProcessRule => (null, new ProcessRuleDto { ProcessName = "game.exe", PriorityClass = (int)ProcessPriorityClass.High }),
        CommandTypes.DeleteProcessRule => (null, 1),
        CommandTypes.SaveWatchdogRule => (null, new WatchdogRuleDto { TargetProcessName = "game.exe", ThresholdValue = 50, ActionType = WatchdogResponse.LogAlert }),
        CommandTypes.DeleteWatchdogRule => (null, 1),
        CommandTypes.SaveSettings => (null, StandInData.Settings()),
        CommandTypes.SetPowerPlan => (null, StandInData.HighPerformance),
        CommandTypes.SetPowerMode => (null, StandInData.BestPerformanceMode),
        CommandTypes.GetActivity => (null, 10),
        CommandTypes.SetTimerResolution => (null, new TimerResolutionArgument { Resolution = 10000 }),
        _ => (null, null)
    };

    public static IEnumerable<object[]> AllCommandTypes() =>
        typeof(CommandTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => new object[] { (string)f.GetRawConstantValue()! });

    [Theory]
    [MemberData(nameof(AllCommandTypes))]
    public async Task EveryCommandType_IsAnswered(string command)
    {
        await using var rig = new Rig();
        var (pid, argument) = Sample(command);

        var reply = await rig.Client.SendAsync(new CommandMessage
        {
            CommandType = command,
            TargetPid = pid,
            Argument = EngineClientFormat(argument)
        });

        Assert.Equal(MessageType.CommandResult, reply.Type);
        var result = JsonSerializer.Deserialize<CommandResult>(reply.Payload, EngineClient.Json)!;
        Assert.False(StandInMachine.IsUnimplemented(result), $"The stand-in engine has no answer for the contract's '{command}'. Add one in StandInMachine.Dispatch.");
        Assert.True(result.Success, $"{command}: {result.Error}");
    }

    /// <summary>Sends an argument the way <see cref="EngineClient"/> does, by going through it.</summary>
    private static string? EngineClientFormat(object? argument) => argument switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        Guid g => g.ToString(),
        int or long or double => Convert.ToString(argument, System.Globalization.CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(argument, argument.GetType(), EngineClient.Json)
    };

    [Fact]
    public async Task UnknownCommand_IsRefusedByName()
    {
        await using var rig = new Rig();
        var ex = await Assert.ThrowsAsync<EngineCommandException>(() => rig.Client.CommandAsync("NoSuchCommand"));
        Assert.Contains("NoSuchCommand", ex.Message);
    }

    // ------------------------------------------------------------------ what comes back

    [Fact]
    public async Task Snapshot_IsBelievable_AndShowsEveryProcessState()
    {
        await using var rig = new Rig();
        var s = await rig.Client.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot);

        Assert.InRange(s.Processes.Count, 35, 50);
        Assert.Equal(s.Processes.Count, s.Processes.Select(p => p.Pid).Distinct().Count());
        Assert.True(s.CallerIsAdministrator);
        Assert.Equal(StandInData.Logical, s.LogicalProcessors);
        Assert.Equal(s.LogicalProcessors, s.CoreCpuPercent.Count);
        Assert.All(s.CoreCpuPercent, c => Assert.InRange(c, 0, 100));
        Assert.InRange(s.ResponsivenessPercent ?? -1, 0, 100);
        Assert.InRange(s.CpuPercent, 0, 100);
        Assert.InRange(s.Processes.Sum(p => p.CpuPercent), 0, 100);

        Assert.Contains(s.Processes, p => p.IsRestrained);
        Assert.Contains(s.Processes, p => p.CpuLimitPercent.HasValue);
        Assert.Contains(s.Processes, p => p.IsAffinityLimited);
        Assert.Contains(s.Processes, p => p.IsEfficiencyMode);
        Assert.Contains(s.Processes, p => p.IsSuspended);
        Assert.Contains(s.Processes, p => p.IsSystem);
        Assert.Contains(s.Processes, p => p.HasRule);
        Assert.Contains(s.Processes, p => p.AffinityMask != StandInData.AllCores);
        Assert.Equal(1, s.Processes.Count(p => p.IsForeground));
        Assert.Equal(s.ForegroundPid, s.Processes.Single(p => p.IsForeground).Pid);
        Assert.Equal(s.ProBalanceRestrained, s.Processes.Count(p => p.IsRestrained));
        Assert.Equal(s.ProcessRuleCount, (await rig.Client.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules)).Count);
        Assert.Contains(s.Processes, p => s.Processes.Any(c => c.ParentPid == p.Pid && c.Pid != p.Pid));
    }

    [Fact]
    public async Task Snapshots_MoveFromOneToTheNext()
    {
        await using var rig = new Rig();
        var first = await rig.Client.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot);
        var second = await rig.Client.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot);

        Assert.NotEqual(first.CpuPercent, second.CpuPercent);
        Assert.NotEqual(first.CoreCpuPercent, second.CoreCpuPercent);
    }

    [Fact]
    public async Task Rules_UseTheNewFields()
    {
        await using var rig = new Rig();
        var rules = await rig.Client.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules);

        Assert.True(rules.Count >= 10);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.Contains(rules, r => r.CpuLimitThresholdPercent.HasValue && r.CpuLimitCoresToRemove.HasValue);
        Assert.Contains(rules, r => r.InstanceBalancerMode == BalancerMode.EqualCoresPerInstance);
        Assert.Contains(rules, r => r.InstanceBalancerMode == BalancerMode.FixedCoresPerInstance);
        Assert.Contains(rules, r => r.StartupBoostPriorityClass.HasValue && r.StartupBoostSeconds.HasValue);
        Assert.Contains(rules, r => r.IoPriority.HasValue);
        Assert.Contains(rules, r => r.MemoryPriority.HasValue);
        Assert.Contains(rules, r => r.GpuPriority.HasValue && r.GpuPreference.HasValue);
        Assert.Contains(rules, r => r.KeepRunning);
        Assert.Contains(rules, r => r.DisallowExecution);
        Assert.Contains(rules, r => r.ExcludeFromProBalance);
        Assert.Contains(rules, r => r.MaxInstances.HasValue && r.MaxInstancesAllSessions);
        Assert.Contains(rules, r => r.EfficiencyMode == true);
        Assert.Contains(rules, r => r.EfficiencyMode == false);
        Assert.Contains(rules, r => !string.IsNullOrEmpty(r.CpuSetIds));
        Assert.Contains(rules, r => r.PowerProfileGuid != null);
        Assert.Contains(rules, r => r.ProcessName.Contains('*'));
    }

    [Fact]
    public async Task WatchdogRules_CoverEveryConditionAndAction()
    {
        await using var rig = new Rig();
        var rules = await rig.Client.QueryAsync<List<WatchdogRuleDto>>(CommandTypes.GetWatchdogRules);

        Assert.Equal(Enum.GetValues<WatchdogTrigger>().OrderBy(t => t), rules.Select(r => r.ConditionType).Distinct().OrderBy(t => t));
        Assert.Equal(Enum.GetValues<WatchdogResponse>().OrderBy(a => a), rules.Select(r => r.ActionType).Distinct().OrderBy(a => a));
        Assert.Contains(rules, r => !r.IsEnabled);
        Assert.Contains(rules, r => r.MemoryMetric == MemoryMetric.PrivateBytes);
    }

    [Fact]
    public async Task Settings_PowerCpuSetsAndActivity_AreFull()
    {
        await using var rig = new Rig();

        var settings = await rig.Client.QueryAsync<EngineSettingsDto>(CommandTypes.GetSettings);
        Assert.InRange(settings.ProBalanceSensitivity, 1, 10);
        Assert.False(string.IsNullOrEmpty(settings.IdlePowerPlanGuid));

        var power = await rig.Client.QueryAsync<PowerStateDto>(CommandTypes.GetPower);
        Assert.True(power.Plans.Count >= 3);
        Assert.Contains(power.Plans, p => p.Id == power.ActivePlanId);

        var sets = await rig.Client.QueryAsync<List<CpuSetDto>>(CommandTypes.GetCpuSets);
        Assert.Equal(StandInData.Logical, sets.Count);
        Assert.Equal(2, sets.Select(c => c.EfficiencyClass).Distinct().Count());
        Assert.Equal(sets.Count, sets.Select(c => c.Id).Distinct().Count());

        var activity = await rig.Client.QueryAsync<List<ActivityEntry>>(CommandTypes.GetActivity);
        Assert.Equal(Enum.GetValues<ActivityKind>().OrderBy(k => k), activity.Select(a => a.Kind).Distinct().OrderBy(k => k));
        Assert.Equal(activity.OrderByDescending(a => a.TimestampUtc).Select(a => a.TimestampUtc), activity.Select(a => a.TimestampUtc));
        Assert.Equal(3, (await rig.Client.QueryAsync<List<ActivityEntry>>(CommandTypes.GetActivity, argument: 3)).Count);
    }

    [Fact]
    public async Task ResultCommands_ReturnTheirDocumentedData()
    {
        await using var rig = new Rig();

        Assert.True(await rig.Client.QueryAsync<long>(CommandTypes.TrimWorkingSet, Code) > 0);
        Assert.True(await rig.Client.QueryAsync<long>(CommandTypes.TrimWorkingSet) > 0);

        var purge = await rig.Client.QueryAsync<MemoryPurgeResultDto>(CommandTypes.PurgeMemory);
        Assert.False(string.IsNullOrWhiteSpace(purge.Summary));

        // Chrome and the processes it started.
        Assert.True(await rig.Client.QueryAsync<long>(CommandTypes.TerminateProcessTree, Chrome) > 1);
    }

    // ------------------------------------------------------------------ actions change the machine

    [Fact]
    public async Task Actions_ChangeWhatTheNextSnapshotShows()
    {
        await using var rig = new Rig();

        await rig.Client.CommandAsync(CommandTypes.SetPriority, Code, "Idle");
        await rig.Client.CommandAsync(CommandTypes.SetCpuLimit, Code, 40);
        await rig.Client.CommandAsync(CommandTypes.SuspendProcess, Code);
        var s = await rig.Client.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot);
        var code = s.Processes.Single(p => p.Pid == Code);
        Assert.Equal(ProcessPriorityClass.Idle, code.Priority);
        Assert.Equal(40, code.CpuLimitPercent);
        Assert.True(code.IsSuspended);
        Assert.Equal(0, code.CpuPercent);

        await rig.Client.CommandAsync(CommandTypes.TerminateProcess, Notepad);
        Assert.DoesNotContain((await rig.Client.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot)).Processes, p => p.Pid == Notepad);

        var activity = await rig.Client.QueryAsync<List<ActivityEntry>>(CommandTypes.GetActivity, argument: 5);
        Assert.Contains(activity, a => a.Kind == ActivityKind.Manual && a.Pid == Notepad);
    }

    [Fact]
    public async Task SavedRule_GetsAnId_AndMarksTheProcessesItMatches()
    {
        await using var rig = new Rig();

        var saved = await rig.Client.QueryAsync<ProcessRuleDto>(CommandTypes.SaveProcessRule,
            argument: new ProcessRuleDto { ProcessName = "Spotify", PriorityClass = (int)ProcessPriorityClass.BelowNormal });
        Assert.True(saved.Id > 0);

        Assert.Contains(await rig.Client.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules), r => r.Id == saved.Id);
        Assert.True((await rig.Client.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot)).Processes.Single(p => p.Pid == Spotify).HasRule);

        await rig.Client.CommandAsync(CommandTypes.DeleteProcessRule, argument: saved.Id);
        Assert.DoesNotContain(await rig.Client.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules), r => r.Id == saved.Id);
        Assert.False((await rig.Client.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot)).Processes.Single(p => p.Pid == Spotify).HasRule);
    }

    [Fact]
    public async Task InvalidRequests_AreRefusedWithAReason()
    {
        await using var rig = new Rig();

        await Assert.ThrowsAsync<EngineCommandException>(() => rig.Client.CommandAsync(CommandTypes.SaveProcessRule, argument: new ProcessRuleDto()));
        await Assert.ThrowsAsync<EngineCommandException>(() => rig.Client.CommandAsync(CommandTypes.SetCpuLimit, Code, 150));
        await Assert.ThrowsAsync<EngineCommandException>(() => rig.Client.CommandAsync(CommandTypes.SetPriority, 999999, "Idle"));
        await Assert.ThrowsAsync<EngineCommandException>(() => rig.Client.CommandAsync(CommandTypes.TerminateProcess, 4));
    }

    [Fact]
    public async Task ReadOnlyEngine_AcceptsActionsButChangesNothing()
    {
        await using var rig = new Rig(readOnly: true);

        await rig.Client.CommandAsync(CommandTypes.SetPriority, Code, "Idle");
        await rig.Client.CommandAsync(CommandTypes.TerminateProcess, Notepad);
        var saved = await rig.Client.QueryAsync<ProcessRuleDto>(CommandTypes.SaveProcessRule, argument: new ProcessRuleDto { ProcessName = "x.exe" });
        Assert.True(saved.Id > 0);

        var s = await rig.Client.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot);
        Assert.Equal(ProcessPriorityClass.AboveNormal, s.Processes.Single(p => p.Pid == Code).Priority);
        Assert.Contains(s.Processes, p => p.Pid == Notepad);
        Assert.DoesNotContain(await rig.Client.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules), r => r.ProcessName == "x.exe");
    }

    // ------------------------------------------------------------------ the standard user

    [Fact]
    public async Task StandardUser_IsToldSo_AndSeesNoPathsOfProcessesTheyDontOwn()
    {
        await using var rig = new Rig(administrator: false);
        var s = await rig.Client.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot);

        Assert.False(s.CallerIsAdministrator);
        Assert.All(s.Processes.Where(p => p.IsSystem), p => Assert.Null(p.FilePath));
        Assert.Contains(s.Processes, p => !p.IsSystem && p.FilePath != null);
    }

    [Theory]
    [InlineData(CommandTypes.PurgeMemory)]
    [InlineData(CommandTypes.ClearActionLog)]
    public async Task StandardUser_IsDeniedWhatNeedsAdministrator(string command)
    {
        await using var rig = new Rig(administrator: false);
        var (pid, argument) = Sample(command);

        var ex = await Assert.ThrowsAsync<EngineCommandException>(() => rig.Client.CommandAsync(command, pid, argument));
        Assert.True(ex.IsAccessDenied, ex.Message);
    }

    [Fact]
    public async Task StandardUser_MayEditRules_WithinTheEnginesLimits()
    {
        // Like the engine's RuleAuthorization: ordinary rules are fine; system programs, Realtime, ending programs
        // the caller couldn't end and Watchdog Launch actions are refused, with the reason in words.
        await using var rig = new Rig(administrator: false);

        var saved = await rig.Client.QueryAsync<ProcessRuleDto>(CommandTypes.SaveProcessRule, argument: new ProcessRuleDto { ProcessName = "game.exe", PriorityClass = (int)ProcessPriorityClass.High });
        Assert.NotEqual(0, saved.Id);
        await rig.Client.CommandAsync(CommandTypes.DeleteProcessRule, argument: saved.Id);
        await rig.Client.QueryAsync<WatchdogRuleDto>(CommandTypes.SaveWatchdogRule, argument: new WatchdogRuleDto { TargetProcessName = "game.exe", ThresholdValue = 50, ActionType = WatchdogResponse.LogAlert });

        async Task<string> Refused(string command, object argument) =>
            (await Assert.ThrowsAsync<EngineCommandException>(() => rig.Client.CommandAsync(command, argument: argument))).Message;

        Assert.Contains("a system process", await Refused(CommandTypes.SaveProcessRule, new ProcessRuleDto { ProcessName = "lsass.exe", IoPriority = 1 }));
        Assert.Contains("Realtime", await Refused(CommandTypes.SaveProcessRule, new ProcessRuleDto { ProcessName = "game.exe", PriorityClass = (int)ProcessPriorityClass.RealTime }));
        Assert.Contains("ends svchost.exe", await Refused(CommandTypes.SaveProcessRule, new ProcessRuleDto { ProcessName = "svchost.exe", DisallowExecution = true }));
        Assert.Contains("Launch", await Refused(CommandTypes.SaveWatchdogRule, new WatchdogRuleDto { TargetProcessName = "game.exe", ThresholdValue = 50, ActionType = WatchdogResponse.Launch, ActionArgument = "calc.exe" }));
    }

    [Fact]
    public async Task StandardUser_MayActOnTheirOwnProcesses_ButNotSystemOnes_OrRealtime()
    {
        await using var rig = new Rig(administrator: false);

        await rig.Client.CommandAsync(CommandTypes.SetPriority, Code, "BelowNormal");
        await rig.Client.CommandAsync(CommandTypes.SetCpuLimit, Code, 30);

        var system = await Assert.ThrowsAsync<EngineCommandException>(() => rig.Client.CommandAsync(CommandTypes.SetPriority, Lsass, "Idle"));
        Assert.True(system.IsAccessDenied, system.Message);

        var realtime = await Assert.ThrowsAsync<EngineCommandException>(() => rig.Client.CommandAsync(CommandTypes.SetPriority, Code, "RealTime"));
        Assert.True(realtime.IsAccessDenied, realtime.Message);
    }

    [Fact]
    public async Task Administrator_MayDoAll_OfTheAbove()
    {
        await using var rig = new Rig();

        await rig.Client.CommandAsync(CommandTypes.SetPriority, Lsass, "Idle");
        await rig.Client.CommandAsync(CommandTypes.SetPriority, Code, "RealTime");
        await rig.Client.QueryAsync<MemoryPurgeResultDto>(CommandTypes.PurgeMemory);
    }

    // ------------------------------------------------------------------ the transport

    [Fact]
    public async Task ManyClients_AreServedAtOnce()
    {
        await using var rig = new Rig();
        var clients = Enumerable.Range(0, 4).Select(_ => new EngineClient(rig.Engine.PipeName)).ToList();
        try
        {
            var snapshots = await Task.WhenAll(clients.Select(c => c.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot)));
            Assert.All(snapshots, s => Assert.NotEmpty(s.Processes));
        }
        finally
        {
            foreach (var client in clients) await client.DisposeAsync();
        }
    }
}
