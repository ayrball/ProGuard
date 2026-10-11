using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public DateTime UtcNow => _now.UtcDateTime;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Records every action and answers queries from per-pid state the test sets up.</summary>
internal sealed class FakeProcessActions : IProcessActions
{
    public Dictionary<int, ProcessPriorityClass> Priorities { get; } = new();
    public Dictionary<int, long> Affinities { get; } = new();
    public Dictionary<int, int> IoPriorities { get; } = new();
    public Dictionary<int, ProcessMemory> Memory { get; } = new();
    public Dictionary<int, DateTime> StartTimes { get; } = new();
    public Dictionary<int, int> Sessions { get; } = new();
    public Dictionary<int, ProcessLaunchInfo> LaunchInfo { get; } = new();
    public HashSet<int> Running { get; } = new();
    public List<string> Calls { get; } = new();

    public ProcessPriorityClass? GetPriority(int pid) => Priorities.TryGetValue(pid, out var p) ? p : null;
    public void SetPriority(int pid, ProcessPriorityClass priority) { Calls.Add($"priority {pid} {priority}"); Priorities[pid] = priority; }
    public long? GetAffinity(int pid) => Affinities.TryGetValue(pid, out var a) ? a : null;
    public void SetAffinity(int pid, long mask) { Calls.Add($"affinity {pid} 0x{mask:X}"); Affinities[pid] = mask; }
    public int? GetIoPriority(int pid) => IoPriorities.TryGetValue(pid, out var io) ? io : null;
    public void SetIoPriority(int pid, int ioPriority) { Calls.Add($"io {pid} {ioPriority}"); IoPriorities[pid] = ioPriority; }
    public void TrimWorkingSet(int pid) => Calls.Add($"trim {pid}");
    /// <summary>Simulates a process that takes a while to die after TerminateProcess.</summary>
    public bool TerminateLeavesRunning { get; set; }
    public void Terminate(int pid) { Calls.Add($"terminate {pid}"); if (!TerminateLeavesRunning) Running.Remove(pid); }
    public bool IsRunning(int pid) => Running.Contains(pid);
    public DateTime? GetStartTimeUtc(int pid) => StartTimes.TryGetValue(pid, out var t) ? t : null;
    public ProcessMemory? GetMemory(int pid) => Memory.TryGetValue(pid, out var m) ? m : null;
    public int? GetSessionId(int pid) => Sessions.TryGetValue(pid, out var s) ? s : null;
    public ProcessLaunchInfo? GetLaunchInfo(int pid) => LaunchInfo.TryGetValue(pid, out var info) ? info : null;

    public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
}

internal sealed class FakeLauncher : IUserSessionLauncher
{
    private int _nextPid = 5000;
    public List<LaunchRequest> Requests { get; } = new();
    public Exception? Failure { get; set; }

    public int Launch(LaunchRequest request)
    {
        Requests.Add(request);
        if (Failure != null) throw Failure;
        return _nextPid++;
    }
}

public class WatchdogTests
{
    private sealed class Harness
    {
        public ManualClock Clock { get; } = new();
        public FakeProcessActions Actions { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public List<ProcessInfo> Processes { get; } = new();
        public List<WatchdogRule> Rules { get; } = new();
        public List<ProcessRule> ProcessRules { get; } = new();
        public WatchdogEventLog Events { get; }
        public KeepRunningService KeepRunning { get; }
        public WatchdogService Watchdog { get; }

        public Harness()
        {
            var monitor = new Mock<IProcessMonitor>();
            monitor.Setup(m => m.GetAllProcesses()).Returns(() => Processes.ToList());
            var store = new Mock<IRuleStore>();
            store.Setup(s => s.GetWatchdogRules()).Returns(() => Rules.ToList());
            store.Setup(s => s.GetProcessRules()).Returns(() => ProcessRules.ToList());

            Events = new WatchdogEventLog(Clock);
            KeepRunning = new KeepRunningService(NullLogger<KeepRunningService>.Instance, store.Object, Actions, Launcher, Events, Clock);
            Watchdog = new WatchdogService(NullLogger<WatchdogService>.Instance, monitor.Object, store.Object, Actions, Launcher, Events, KeepRunning, Clock);
        }

        public ProcessInfo AddProcess(int pid, string name = "hog", double cpu = 0)
        {
            var info = new ProcessInfo { Pid = pid, Name = name, CpuPercent = cpu };
            Processes.Add(info);
            Actions.Running.Add(pid);
            return info;
        }

        public void RemoveProcess(int pid)
        {
            Processes.RemoveAll(p => p.Pid == pid);
            Actions.Running.Remove(pid);
        }

        public WatchdogRule AddRule(WatchdogAction action, WatchdogCondition condition = WatchdogCondition.CpuAbove,
            double threshold = 50, int duration = 0, string? argument = null, int cooldown = 0, int actionDuration = 0)
        {
            var rule = new WatchdogRule
            {
                Id = Rules.Count + 1, TargetProcessName = "hog.exe", IsEnabled = true, ConditionType = condition,
                ThresholdValue = threshold, DurationSeconds = duration, ActionType = action, ActionArgument = argument,
                CooldownSeconds = cooldown, ActionDurationSeconds = actionDuration
            };
            Rules.Add(rule);
            return rule;
        }

        /// <summary>Polls once, then moves the clock on by a second, as the real timer would.</summary>
        public void Tick(int times = 1)
        {
            for (var i = 0; i < times; i++)
            {
                Watchdog.EvaluateRules();
                Clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        public List<WatchdogEventDto> AllEvents() => Events.GetSince(0).Events;
    }

    // ---------- Sustained conditions ----------

    [Fact]
    public void Fires_OnlyAfterTheConditionHoldsForTheWholeDuration()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.AddRule(WatchdogAction.Terminate, threshold: 50, duration: 30);

        h.Tick(30); // samples at 0..29 s
        Assert.Equal(0, h.Actions.Count("terminate"));

        h.Watchdog.EvaluateRules(); // 30 s
        Assert.Equal(new[] { "terminate 100" }, h.Actions.Calls);
    }

    [Fact]
    public void ABreakInTheCondition_StartsTheDurationOver()
    {
        var h = new Harness();
        var hog = h.AddProcess(100, cpu: 90);
        h.AddRule(WatchdogAction.Terminate, threshold: 50, duration: 30);

        h.Tick(20);
        hog.CpuPercent = 10;
        h.Tick();
        hog.CpuPercent = 90;
        h.Tick(30); // held for 0..29 s again
        Assert.Equal(0, h.Actions.Count("terminate"));

        h.Tick();
        Assert.Equal(1, h.Actions.Count("terminate"));
    }

    [Fact]
    public void ZeroDuration_FiresOnTheFirstSample()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.AddRule(WatchdogAction.TrimWorkingSet, threshold: 50);

        h.Tick();
        Assert.Equal(new[] { "trim 100" }, h.Actions.Calls);
    }

    [Fact]
    public void AfterFiring_StaysQuietUntilTheConditionClears()
    {
        var h = new Harness();
        var hog = h.AddProcess(100, cpu: 90);
        h.AddRule(WatchdogAction.TrimWorkingSet, threshold: 50, duration: 5, cooldown: 0);

        h.Tick(120);
        Assert.Equal(1, h.Actions.Count("trim"));

        hog.CpuPercent = 0;
        h.Tick();
        hog.CpuPercent = 90;
        h.Tick(5);
        Assert.Equal(1, h.Actions.Count("trim"));
        h.Tick();
        Assert.Equal(2, h.Actions.Count("trim"));
    }

    [Fact]
    public void AfterFiring_FiresAgainOnceTheCooldownPassesWithTheConditionStillTrue()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.AddRule(WatchdogAction.TrimWorkingSet, threshold: 50, cooldown: 60);

        h.Tick(60); // fires at 0 s, quiet until 59 s
        Assert.Equal(1, h.Actions.Count("trim"));
        h.Tick();   // 60 s
        Assert.Equal(2, h.Actions.Count("trim"));
    }

    [Fact]
    public void EachProcessIsTrackedSeparately()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.AddRule(WatchdogAction.Terminate, threshold: 50, duration: 10);

        h.Tick(5);
        h.AddProcess(200, cpu: 90);
        h.Tick(6); // 100 has held for 10 s at the last sample, 200 for 5 s
        Assert.Equal(new[] { "terminate 100" }, h.Actions.Calls);
    }

    // ---------- Conditions ----------

    [Fact]
    public void MemoryRule_UsesTheSelectedMetric()
    {
        var h = new Harness();
        h.AddProcess(100);
        h.Actions.Memory[100] = new ProcessMemory(WorkingSetBytes: 100L << 20, PrivateBytes: 900L << 20);

        var workingSet = h.AddRule(WatchdogAction.LogAlert, WatchdogCondition.RamAbove, threshold: 500);
        var privateBytes = h.AddRule(WatchdogAction.Notify, WatchdogCondition.RamAbove, threshold: 500);
        privateBytes.MemoryMetric = WatchdogMemoryMetric.PrivateBytes;

        h.Tick();
        var fired = h.AllEvents();
        Assert.Single(fired);
        Assert.Equal(privateBytes.Id, fired[0].RuleId);
        Assert.Contains("private bytes 900 MB", fired[0].Message);
    }

    [Fact]
    public void RunningLongerThan_ComparesTheProcessAgeWithTheThreshold()
    {
        var h = new Harness();
        h.AddProcess(100);
        h.Actions.StartTimes[100] = h.Clock.UtcNow - TimeSpan.FromSeconds(50);
        h.AddRule(WatchdogAction.Terminate, WatchdogCondition.RunningLongerThan, threshold: 60);

        h.Tick(10); // ages 50..59 s
        Assert.Equal(0, h.Actions.Count("terminate"));
        h.Tick();   // 60 s: not yet longer than 60
        h.Tick();   // 61 s
        Assert.Equal(1, h.Actions.Count("terminate"));
    }

    [Fact]
    public void InstanceCount_ActsOnTheNewestInstancesOverTheLimit()
    {
        var h = new Harness();
        for (var i = 0; i < 4; i++)
        {
            h.AddProcess(100 + i);
            h.Actions.StartTimes[100 + i] = h.Clock.UtcNow.AddMinutes(i);
        }
        h.AddRule(WatchdogAction.Terminate, WatchdogCondition.InstanceCountAbove, threshold: 2, duration: 3);

        h.Tick(3);
        Assert.Empty(h.Actions.Calls);
        h.Tick();
        Assert.Equal(new[] { "terminate 103", "terminate 102" }, h.Actions.Calls);
    }

    [Fact]
    public void InstanceCount_NotifiesOncePerFiring()
    {
        var h = new Harness();
        h.AddProcess(100);
        h.AddProcess(101);
        h.AddRule(WatchdogAction.Notify, WatchdogCondition.InstanceCountAbove, threshold: 1, argument: "Too many hogs");

        h.Tick(5);
        var notified = Assert.Single(h.AllEvents());
        Assert.True(notified.Notify);
        Assert.StartsWith("Too many hogs", notified.Message);
    }

    // ---------- Actions ----------

    [Fact]
    public void SetPriority_UsesTheArgument_AndRestoresAfterTheDuration()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.Priorities[100] = ProcessPriorityClass.AboveNormal;
        h.AddRule(WatchdogAction.SetPriority, argument: "Below Normal", actionDuration: 10);

        h.Tick();
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.Actions.Priorities[100]);
        h.Tick(9);
        Assert.Equal(ProcessPriorityClass.BelowNormal, h.Actions.Priorities[100]);
        h.Tick(); // 10 s after the change
        Assert.Equal(ProcessPriorityClass.AboveNormal, h.Actions.Priorities[100]);
        Assert.Contains(h.AllEvents(), e => e.Kind == WatchdogEventKinds.Restored);
    }

    [Fact]
    public void SetPriority_WithoutDuration_IsPermanent()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.Priorities[100] = ProcessPriorityClass.Normal;
        h.AddRule(WatchdogAction.SetPriority, argument: "Idle");

        h.Tick(120);
        Assert.Equal(new[] { "priority 100 Idle" }, h.Actions.Calls);
    }

    [Fact]
    public void SetPriority_WithAnUnknownArgument_FailsWithoutTouchingTheProcess()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.AddRule(WatchdogAction.SetPriority, argument: "Turbo");

        h.Tick();
        Assert.Empty(h.Actions.Calls);
        Assert.Contains(h.AllEvents(), e => e.Kind == WatchdogEventKinds.Failed && e.Message.Contains("Turbo"));
    }

    [Fact]
    public void SetIoPriority_SetsTheHint_AndRestoresIt()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.IoPriorities[100] = WatchdogArguments.IoNormal;
        h.AddRule(WatchdogAction.SetIoPriority, argument: "VeryLow", actionDuration: 5);

        h.Tick();
        Assert.Equal(WatchdogArguments.IoVeryLow, h.Actions.IoPriorities[100]);
        h.Tick(5);
        Assert.Equal(WatchdogArguments.IoNormal, h.Actions.IoPriorities[100]);
    }

    [Fact]
    public void SetAffinity_IsRestoredAfterTheDuration()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.Affinities[100] = 0xFF;
        h.AddRule(WatchdogAction.SetAffinity, argument: "0x3", actionDuration: 5);

        h.Tick();
        Assert.Equal(0x3, h.Actions.Affinities[100]);
        h.Tick(5);
        Assert.Equal(0xFF, h.Actions.Affinities[100]);
    }

    [Fact]
    public void SetAffinity_IsNotRestoredWhenSomethingElseChangedItMeanwhile()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.Affinities[100] = 0xFF;
        h.AddRule(WatchdogAction.SetAffinity, argument: "0x3", actionDuration: 5);

        h.Tick();
        h.Actions.Affinities[100] = 0xF0; // the user picked something else
        h.Tick(10);
        Assert.Equal(0xF0, h.Actions.Affinities[100]);
    }

    [Fact]
    public void Restore_KeepsTheValueFromBeforeTheFirstChange_WhenTheRuleFiresAgain()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.Priorities[100] = ProcessPriorityClass.High;
        h.AddRule(WatchdogAction.SetPriority, argument: "Idle", actionDuration: 30, cooldown: 10);

        h.Tick(45); // fires at 0, 10, 20, ... each extends the restore
        Assert.Equal(ProcessPriorityClass.Idle, h.Actions.Priorities[100]);

        h.Rules.Clear();
        h.Tick(31);
        Assert.Equal(ProcessPriorityClass.High, h.Actions.Priorities[100]);
    }

    [Fact]
    public void PendingRestores_AreAppliedWhenTheServiceStops()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.Priorities[100] = ProcessPriorityClass.Normal;
        h.AddRule(WatchdogAction.SetPriority, argument: "Idle", actionDuration: 600);

        h.Tick();
        h.Watchdog.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(ProcessPriorityClass.Normal, h.Actions.Priorities[100]);
    }

    [Fact]
    public void Trim_Continuous_TrimsRepeatedlyWhileTheConditionHolds()
    {
        var h = new Harness();
        var hog = h.AddProcess(100, cpu: 90);
        h.AddRule(WatchdogAction.TrimWorkingSet, argument: WatchdogArguments.ContinuousTrim);

        h.Tick(11); // 0, 5, 10 s
        Assert.Equal(3, h.Actions.Count("trim"));

        hog.CpuPercent = 0;
        h.Tick(20);
        Assert.Equal(3, h.Actions.Count("trim"));
    }

    [Fact]
    public void Restart_TerminatesThenRelaunchesWithTheSameCommandLine()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        var info = new ProcessLaunchInfo(@"C:\apps\hog.exe", "\"C:\\apps\\hog.exe\" --serve 8080", @"C:\apps\", 2);
        h.Actions.LaunchInfo[100] = info;
        h.AddRule(WatchdogAction.Restart);

        h.Tick();
        Assert.Equal(new[] { "terminate 100" }, h.Actions.Calls);
        var request = Assert.Single(h.Launcher.Requests);
        Assert.Equal(LaunchRequest.From(info), request);
    }

    [Fact]
    public void Restart_WaitsForTheOldProcessToExit()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.LaunchInfo[100] = new ProcessLaunchInfo(@"C:\apps\hog.exe", "hog.exe", null, 2);
        h.Actions.TerminateLeavesRunning = true;
        h.AddRule(WatchdogAction.Restart);

        h.Tick(2);
        Assert.Empty(h.Launcher.Requests);

        h.Actions.Running.Remove(100);
        h.Tick();
        Assert.Single(h.Launcher.Requests);
    }

    [Fact]
    public void Restart_LaunchesAnywayWhenTheOldProcessWontDie()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.LaunchInfo[100] = new ProcessLaunchInfo(@"C:\apps\hog.exe", "hog.exe", null, 2);
        h.Actions.TerminateLeavesRunning = true;
        h.AddRule(WatchdogAction.Restart);

        h.Tick((int)WatchdogService.RestartExitTimeout.TotalSeconds);
        Assert.Empty(h.Launcher.Requests);
        h.Tick();
        Assert.Single(h.Launcher.Requests);
    }

    [Fact]
    public void Restart_RefusesSessionZeroProcesses()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.LaunchInfo[100] = new ProcessLaunchInfo(@"C:\Windows\svc.exe", "svc.exe", null, 0);
        h.AddRule(WatchdogAction.Restart);

        h.Tick(3);
        Assert.Empty(h.Actions.Calls);
        Assert.Empty(h.Launcher.Requests);
        Assert.Contains(h.AllEvents(), e => e.Kind == WatchdogEventKinds.Failed && e.Message.Contains("session 0"));
    }

    [Fact]
    public void Launch_StartsTheProgramOnTheDesktopOfTheTriggeringProcess()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.Sessions[100] = 3;
        h.AddRule(WatchdogAction.Launch, argument: "\"C:\\Tools\\report.exe\" --pid hog");

        h.Tick();
        var request = Assert.Single(h.Launcher.Requests);
        Assert.Equal(@"C:\Tools\report.exe", request.ExecutablePath);
        Assert.Equal("\"C:\\Tools\\report.exe\" --pid hog", request.CommandLine);
        Assert.Equal(3, request.SessionId);
    }

    [Fact]
    public void Launch_ForAServiceProcess_GoesToTheConsoleSession()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.Actions.Sessions[100] = 0;
        h.AddRule(WatchdogAction.Launch, argument: "notepad.exe");

        h.Tick();
        var request = Assert.Single(h.Launcher.Requests);
        Assert.Null(request.SessionId);
        Assert.Null(request.ExecutablePath); // not a full path: CreateProcess searches for it
    }

    [Fact]
    public void Notify_PostsANotificationEvent_LogOnlyDoesNot()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        var notify = h.AddRule(WatchdogAction.Notify, argument: "Hog is hogging");
        var log = h.AddRule(WatchdogAction.LogAlert);

        h.Tick();
        Assert.Empty(h.Actions.Calls);
        var events = h.AllEvents();
        Assert.True(events.Single(e => e.RuleId == notify.Id).Notify);
        Assert.Contains("Hog is hogging", events.Single(e => e.RuleId == notify.Id).Message);
        Assert.False(events.Single(e => e.RuleId == log.Id).Notify);
    }

    [Fact]
    public void ProtectedProcesses_AreNeverTouched()
    {
        var h = new Harness();
        h.AddProcess(4, name: "hog", cpu: 90);
        h.AddProcess(Environment.ProcessId, name: "hog", cpu: 90);
        h.AddRule(WatchdogAction.Terminate);

        h.Tick();
        Assert.Empty(h.Actions.Calls);
    }

    [Fact]
    public void DisabledRules_DoNothing()
    {
        var h = new Harness();
        h.AddProcess(100, cpu: 90);
        h.AddRule(WatchdogAction.Terminate).IsEnabled = false;

        h.Tick(5);
        Assert.Empty(h.Actions.Calls);
    }

    // ---------- Keep Running ----------

    private static Harness KeepRunningHarness(out ProcessLaunchInfo info)
    {
        var h = new Harness();
        h.ProcessRules.Add(new ProcessRule { ProcessName = "agent.exe", KeepRunning = true });
        h.AddProcess(100, name: "agent");
        info = new ProcessLaunchInfo(@"C:\apps\agent.exe", "\"C:\\apps\\agent.exe\" --tray", @"C:\apps\", 1);
        h.Actions.LaunchInfo[100] = info;
        return h;
    }

    [Fact]
    public void KeepRunning_RelaunchesAfterTheBackoff_WithTheLastCommandLine()
    {
        var h = KeepRunningHarness(out var info);
        h.Tick();

        h.RemoveProcess(100);
        h.Tick(2); // exit seen at t, back-off 2 s
        Assert.Empty(h.Launcher.Requests);
        h.Tick();
        Assert.Equal(LaunchRequest.From(info), Assert.Single(h.Launcher.Requests));
        Assert.Contains(h.AllEvents(), e => e.Kind == WatchdogEventKinds.Relaunched);
    }

    [Fact]
    public void KeepRunning_DoesNothingWhileAnotherInstanceIsRunning()
    {
        var h = KeepRunningHarness(out _);
        h.AddProcess(101, name: "agent");
        h.Tick();

        h.RemoveProcess(100);
        h.Tick(10);
        Assert.Empty(h.Launcher.Requests);
    }

    [Fact]
    public void KeepRunning_DoesNotRelaunchWhatItNeverSawRunning()
    {
        var h = new Harness();
        h.ProcessRules.Add(new ProcessRule { ProcessName = "agent.exe", KeepRunning = true });
        h.Tick(10);
        Assert.Empty(h.Launcher.Requests);
    }

    [Fact]
    public void KeepRunning_StopsWhenTheRuleFlagIsCleared()
    {
        var h = KeepRunningHarness(out _);
        h.Tick();
        h.ProcessRules[0].KeepRunning = false;
        h.RemoveProcess(100);
        h.Tick(10);
        Assert.Empty(h.Launcher.Requests);
    }

    [Fact]
    public void KeepRunning_GivesUpOnACrashLoop_AndNotifies()
    {
        var h = KeepRunningHarness(out _);
        h.Tick();
        h.RemoveProcess(100);

        // Every relaunched instance dies at once (it never shows up in the process list).
        for (var i = 0; i < 120; i++)
        {
            h.Tick();
            foreach (var pid in h.Actions.Running.ToList()) h.Actions.Running.Remove(pid);
        }

        Assert.Equal(KeepRunningService.MaxRelaunchesPerWindow, h.Launcher.Requests.Count);
        var gaveUp = Assert.Single(h.AllEvents(), e => e.Kind == WatchdogEventKinds.Failed);
        Assert.True(gaveUp.Notify);
        Assert.Contains("Stopped relaunching", gaveUp.Message);
    }

    [Fact]
    public void KeepRunning_StartsAgainAfterGivingUp_WhenTheUserStartsTheProcess()
    {
        var h = KeepRunningHarness(out _);
        h.Tick();
        h.RemoveProcess(100);
        for (var i = 0; i < 120; i++)
        {
            h.Tick();
            foreach (var pid in h.Actions.Running.ToList()) h.Actions.Running.Remove(pid);
        }
        var before = h.Launcher.Requests.Count;

        h.AddProcess(200, name: "agent");
        h.Actions.LaunchInfo[200] = h.Actions.LaunchInfo[100];
        h.Tick();
        h.RemoveProcess(200);
        h.Tick(4);
        Assert.Equal(before + 1, h.Launcher.Requests.Count);
    }

    [Fact]
    public void KeepRunning_BackoffDoublesUpToTheCap()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), KeepRunningService.Backoff(0));
        Assert.Equal(TimeSpan.FromSeconds(4), KeepRunningService.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(8), KeepRunningService.Backoff(2));
        Assert.Equal(KeepRunningService.MaxBackoff, KeepRunningService.Backoff(3));
        Assert.Equal(KeepRunningService.MaxBackoff, KeepRunningService.Backoff(50));
    }

    [Fact]
    public void KeepRunning_SecondRelaunchWaitsLonger()
    {
        var h = KeepRunningHarness(out _);
        h.Tick();
        h.RemoveProcess(100);
        h.Tick(3);
        Assert.Single(h.Launcher.Requests); // first relaunch after 2 s

        // The relaunched instance dies before the monitor lists it.
        h.Actions.Running.Clear();
        h.Tick(4);
        Assert.Single(h.Launcher.Requests);
        h.Tick();
        Assert.Equal(2, h.Launcher.Requests.Count); // 4 s back-off
    }

    [Fact]
    public void KeepRunning_DoesNotDoubleLaunchWhenTheWatchdogRestartsTheProcess()
    {
        var h = KeepRunningHarness(out _);
        h.Tick();

        var rule = h.AddRule(WatchdogAction.Restart);
        rule.TargetProcessName = "agent.exe";
        h.Processes[0].CpuPercent = 90;
        h.Tick(); // terminate + launch
        Assert.Single(h.Launcher.Requests);

        h.Rules.Clear();
        h.RemoveProcess(100);
        h.Tick(5);
        // A slow monitor only lists the restarted instance now, after Keep Running's own back-off would have expired.
        h.AddProcess(5000, name: "agent");
        h.Tick(15);
        Assert.Single(h.Launcher.Requests);
    }

    [Fact]
    public void KeepRunning_RetriesAFailedLaunch()
    {
        var h = KeepRunningHarness(out _);
        h.Tick();
        h.Launcher.Failure = new InvalidOperationException("No user is signed in");
        h.RemoveProcess(100);
        h.Tick(3);
        Assert.Single(h.Launcher.Requests);

        h.Launcher.Failure = null;
        h.Tick(5);
        Assert.Equal(2, h.Launcher.Requests.Count);
    }

    // ---------- Event log and IPC ----------

    [Fact]
    public void EventLog_ReturnsOnlyNewerEvents_AndEverythingAfterAnEngineRestart()
    {
        var log = new WatchdogEventLog(new ManualClock());
        log.Add(WatchdogEventKinds.Fired, "a", "1");
        log.Add(WatchdogEventKinds.Fired, "b", "2");

        var fresh = log.GetSince(null);
        Assert.Equal(2, fresh.Latest);
        Assert.Empty(fresh.Events);

        Assert.Equal(new[] { "2" }, log.GetSince(1).Events.Select(e => e.Message));
        Assert.Equal(2, log.GetSince(99).Events.Count);
    }

    [Fact]
    public void EventLog_KeepsOnlyTheNewestEvents()
    {
        var log = new WatchdogEventLog(new ManualClock());
        for (var i = 0; i < WatchdogEventLog.Capacity + 10; i++) log.Add(WatchdogEventKinds.Fired, "t", i.ToString());

        var all = log.GetSince(0).Events;
        Assert.Equal(WatchdogEventLog.Capacity, all.Count);
        Assert.Equal("10", all[0].Message);
    }

    [Fact]
    public void Dispatcher_AnswersGetLogs_AndAStandardUserMayAsk()
    {
        var log = new WatchdogEventLog(new ManualClock());
        log.Add(WatchdogEventKinds.Fired, "Watchdog: hog.exe", "Terminated hog", notify: true);

        var store = new Mock<IRuleStore>();
        store.Setup(s => s.GetSettings()).Returns(new AppSettings { Id = 1 });
        var monitor = new Mock<IProcessMonitor>();
        var smartTrim = new Mock<ISmartTrimService>();
        var power = new Mock<IPowerManager>();
        var proBalance = new Mock<IProBalanceEngine>();
        var applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, monitor.Object, smartTrim.Object, power.Object, proBalance.Object);
        var dispatcher = new IpcDispatcher(NullLogger<IpcDispatcher>.Instance, store.Object, applier, proBalance.Object, power.Object,
            smartTrim.Object, new Mock<ICpuLimiter>().Object, new Mock<IProcessController>().Object, monitor.Object, log);

        var request = new CommandMessage { Type = MessageType.GetLogs, Payload = JsonSerializer.Serialize(new WatchdogEventQuery { After = 0 }) };
        var reply = dispatcher.Dispatch(request, new StandardUser());

        Assert.Equal(MessageType.LogList, reply.Type);
        var list = JsonSerializer.Deserialize<WatchdogEventList>(reply.Payload)!;
        Assert.Equal(1, list.Latest);
        Assert.True(Assert.Single(list.Events).Notify);
    }

    [Fact]
    public void EngineContainer_WiresTheWatchdogKeepRunningAndDispatcherToOneEventLog()
    {
        using var host = ProGuard.Engine.Program.CreateHostBuilder(Array.Empty<string>()).Build();
        var services = host.Services;
        var log = (WatchdogEventLog)services.GetService(typeof(WatchdogEventLog))!;
        var watchdog = (WatchdogService)services.GetService(typeof(WatchdogService))!;
        Assert.NotNull(services.GetService(typeof(KeepRunningService)));
        Assert.Same(log, watchdog.Events);

        log.Add(WatchdogEventKinds.Fired, "t", "m");
        var dispatcher = (IpcDispatcher)services.GetService(typeof(IpcDispatcher))!;
        var reply = dispatcher.Dispatch(new CommandMessage { Type = MessageType.GetLogs, Payload = "{\"After\":0}" });
        Assert.Single(JsonSerializer.Deserialize<WatchdogEventList>(reply.Payload)!.Events);
    }

    private sealed class StandardUser : IIpcCaller
    {
        public string Name => @"TEST\standard";
        public bool IsAdministrator => false;
        public bool CanOpenProcess(int pid, ProcessAccessFlags access) => false;
    }

    [Fact]
    public async Task NotificationListener_SkipsTheBacklog_ThenNotifiesNewNotifyEvents()
    {
        var log = new WatchdogEventLog(new ManualClock());
        log.Add(WatchdogEventKinds.Fired, "old", "from before the UI started", notify: true);
        var shown = new List<string>();
        var batches = new List<IReadOnlyList<WatchdogEventDto>>();
        var listener = new WatchdogNotificationListener((after, _) => Task.FromResult<WatchdogEventList?>(log.GetSince(after)), e => shown.Add(e.Title));
        listener.EventsReceived += (_, events) => batches.Add(events);

        await listener.PollOnceAsync(CancellationToken.None);
        Assert.Empty(shown);
        Assert.Single(batches); // the backlog still feeds the activity view

        log.Add(WatchdogEventKinds.Fired, "quiet", "logged only");
        log.Add(WatchdogEventKinds.Fired, "loud", "notify me", notify: true);
        await listener.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new[] { "loud" }, shown);
        Assert.Equal("loud", listener.LatestEvent?.Title);

        await listener.PollOnceAsync(CancellationToken.None);
        Assert.Single(shown);
    }

    [Fact]
    public async Task NotificationListener_KeepsItsPlaceWhileTheEngineIsUnreachable()
    {
        var log = new WatchdogEventLog(new ManualClock());
        var reachable = true;
        var shown = new List<string>();
        var listener = new WatchdogNotificationListener(
            (after, _) => Task.FromResult(reachable ? log.GetSince(after) : null), e => shown.Add(e.Title));

        await listener.PollOnceAsync(CancellationToken.None);
        reachable = false;
        log.Add(WatchdogEventKinds.Fired, "while away", "x", notify: true);
        await listener.PollOnceAsync(CancellationToken.None);
        reachable = true;
        await listener.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new[] { "while away" }, shown);
    }

    // ---------- Arguments and UI ----------

    [Theory]
    [InlineData("Idle", ProcessPriorityClass.Idle)]
    [InlineData("below normal", ProcessPriorityClass.BelowNormal)]
    [InlineData("Realtime", ProcessPriorityClass.RealTime)]
    [InlineData("16384", ProcessPriorityClass.BelowNormal)]
    public void Arguments_ParsePriorities(string text, ProcessPriorityClass expected)
    {
        Assert.True(WatchdogArguments.TryParsePriority(text, out var priority));
        Assert.Equal(expected, priority);
    }

    [Theory]
    [InlineData("VeryLow", 0)]
    [InlineData("very low", 0)]
    [InlineData("High", 3)]
    [InlineData("2", 2)]
    public void Arguments_ParseIoPriorities(string text, int expected)
    {
        Assert.True(WatchdogArguments.TryParseIoPriority(text, out var io));
        Assert.Equal(expected, io);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("Critical")]
    [InlineData("")]
    public void Arguments_RejectBadIoPriorities(string text) => Assert.False(WatchdogArguments.TryParseIoPriority(text, out _));

    [Fact]
    public void Arguments_ParseAffinityAndCommandLines()
    {
        Assert.True(WatchdogArguments.TryParseAffinity("0xF0", out var mask));
        Assert.Equal(0xF0, mask);
        Assert.False(WatchdogArguments.TryParseAffinity("0", out _));

        Assert.True(WatchdogArguments.TrySplitCommandLine("\"C:\\Program Files\\App\\app.exe\" -a -b", out var program, out var args));
        Assert.Equal(@"C:\Program Files\App\app.exe", program);
        Assert.Equal("-a -b", args);
        Assert.True(WatchdogArguments.TrySplitCommandLine("notepad.exe", out program, out args));
        Assert.Equal("notepad.exe", program);
        Assert.Equal("", args);
    }

    [Fact]
    public void ViewModel_BuildsRulesWithTheNewFields()
    {
        var vm = new WatchdogViewModel
        {
            NewProcessName = "hog",
            NewCondition = WatchdogViewModel.ConditionOptions[1],
            NewMemoryMetric = WatchdogViewModel.MemoryMetricOptions[1],
            NewThreshold = 2048,
            NewDuration = 20,
            NewAction = WatchdogViewModel.ActionOptions[2],
            NewActionArgument = "BelowNormal",
            NewActionDuration = 30,
            NewCooldown = 0
        };

        Assert.Null(vm.TryBuildRule(out var rule));
        Assert.Equal("hog.exe", rule.TargetProcessName);
        Assert.Equal(WatchdogCondition.RamAbove, rule.ConditionType);
        Assert.Equal(WatchdogMemoryMetric.PrivateBytes, rule.MemoryMetric);
        Assert.Equal(WatchdogAction.SetPriority, rule.ActionType);
        Assert.Equal("BelowNormal", rule.ActionArgument);
        Assert.Equal(30, rule.ActionDurationSeconds);
        Assert.Equal(0, rule.CooldownSeconds);
    }

    [Fact]
    public void ViewModel_RejectsInvalidArguments()
    {
        var vm = new WatchdogViewModel { NewProcessName = "hog", NewAction = "Set Affinity", NewActionArgument = "lots" };
        Assert.NotNull(vm.TryBuildRule(out _));

        vm.NewAction = "Launch Program";
        vm.NewActionArgument = "";
        Assert.NotNull(vm.TryBuildRule(out _));

        vm.NewAction = "Trim Working Set";
        vm.NewTrimContinuously = true;
        Assert.Null(vm.TryBuildRule(out var trim));
        Assert.Equal(WatchdogArguments.ContinuousTrim, trim.ActionArgument);
    }

    [Fact]
    public async Task ViewModel_KeepRunning_SetsAndClearsTheProcessRuleFlag()
    {
        var name = $"keeprun{Guid.NewGuid():N}.exe";
        var vm = new WatchdogViewModel();
        Assert.True((await vm.SetKeepRunningAsync(name, true)).Success);
        vm.LoadKeepRunning();
        Assert.Contains(name, vm.KeepRunningProcesses);

        Assert.True((await vm.SetKeepRunningAsync(name, false)).Success);
        vm.LoadKeepRunning();
        Assert.DoesNotContain(name, vm.KeepRunningProcesses);
    }

    // ---------- Real processes and the launcher ----------

    [Fact]
    public void LaunchInfo_ReadsTheCommandLineOfARunningProcess()
    {
        using var child = StartIdleProcess();
        try
        {
            var info = new Win32ProcessActions().GetLaunchInfo(child.Id);
            Assert.NotNull(info);
            Assert.EndsWith("PING.EXE", info!.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("-n 121 127.0.0.1", info.CommandLine);
            Assert.False(string.IsNullOrEmpty(info.WorkingDirectory));
            Kernel32.ProcessIdToSessionId((uint)Environment.ProcessId, out var ownSession);
            Assert.Equal((int)ownSession, info.SessionId);
        }
        finally { KillQuietly(child); }
    }

    [Fact]
    public void Win32Actions_ChangePriorityAndIoPriority()
    {
        using var child = StartIdleProcess();
        try
        {
            var actions = new Win32ProcessActions();
            actions.SetPriority(child.Id, ProcessPriorityClass.BelowNormal);
            Assert.Equal(ProcessPriorityClass.BelowNormal, actions.GetPriority(child.Id));

            actions.SetIoPriority(child.Id, WatchdogArguments.IoVeryLow);
            Assert.Equal(WatchdogArguments.IoVeryLow, actions.GetIoPriority(child.Id));

            Assert.NotNull(actions.GetMemory(child.Id));
            Assert.NotNull(actions.GetStartTimeUtc(child.Id));
            Assert.True(actions.IsRunning(child.Id));
        }
        finally { KillQuietly(child); }
    }

    [Fact]
    public void Launcher_ResolvesTheSession()
    {
        Assert.Equal(3u, UserSessionLauncher.ResolveSession(3, 1));
        Assert.Equal(1u, UserSessionLauncher.ResolveSession(null, 1));
        Assert.Throws<InvalidOperationException>(() => UserSessionLauncher.ResolveSession(0, 1));
        Assert.Throws<InvalidOperationException>(() => UserSessionLauncher.ResolveSession(null, 0xFFFFFFFF));
    }

    [SystemOnlyFact]
    public void Launcher_AsSystem_StartsTheProgramInTheConsoleUsersSession()
    {
        var console = Kernel32.WTSGetActiveConsoleSessionId();
        var launcher = new UserSessionLauncher(NullLogger<UserSessionLauncher>.Instance);
        var ping = System.IO.Path.Combine(Environment.SystemDirectory, "PING.EXE");
        var pid = launcher.Launch(new LaunchRequest(ping, $"\"{ping}\" -n 30 127.0.0.1"));
        try
        {
            Assert.True(Kernel32.ProcessIdToSessionId((uint)pid, out var session));
            Assert.Equal(console, session);
            Assert.NotEqual(0u, session);
        }
        finally
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(); } catch { }
        }
    }

    private static Process StartIdleProcess()
    {
        var psi = new ProcessStartInfo("ping.exe", "-n 121 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false };
        var process = Process.Start(psi)!;
        Thread.Sleep(200); // let it finish initialising its PEB before anything reads it
        return process;
    }

    private static void KillQuietly(Process process)
    {
        try { process.Kill(); process.WaitForExit(5000); } catch { }
    }
}

/// <summary>Runs only when the tests run as LocalSystem, the only account WTSQueryUserToken works for.</summary>
public sealed class SystemOnlyFactAttribute : FactAttribute
{
    public SystemOnlyFactAttribute()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) Skip = "Needs to run as LocalSystem (WTSQueryUserToken requires SeTcbPrivilege)";
    }
}
