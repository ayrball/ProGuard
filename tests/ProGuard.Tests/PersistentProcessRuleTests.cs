using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>
/// In-memory stand-in for the Windows calls: each setter records the call and stores the value so a later read sees
/// it, unless the pid is marked unreadable or the setting is marked to fail.
/// </summary>
internal sealed class FakeProcessTuning : IProcessTuningNative
{
    public Dictionary<int, int> Io { get; } = new();
    public Dictionary<int, int> Memory { get; } = new();
    public Dictionary<int, (bool Controlled, bool Enabled)> Eco { get; } = new();
    public Dictionary<int, bool> BoostDisabled { get; } = new();
    public Dictionary<int, IReadOnlyList<uint>> CpuSets { get; } = new();
    public Dictionary<int, int> Gpu { get; } = new();
    public List<CpuSetInfo> SystemCpuSets { get; } = new()
    {
        new(256, 0, 0, 0, 0, 0, 1, false, false),
        new(257, 0, 1, 0, 0, 0, 1, false, false),
        new(258, 0, 2, 1, 0, 0, 0, false, false),
    };

    public Dictionary<int, string> ImagePaths { get; } = new();
    public Dictionary<int, string> Owners { get; } = new();
    public string? ConsoleUser { get; set; }
    public Dictionary<(string Sid, string Path), string> Registry { get; } = new();

    public HashSet<int> Unreadable { get; } = new();
    /// <summary>Setting name (method name without "Set"/"Write") -> exception to throw from its setter.</summary>
    public Dictionary<string, Exception> Failures { get; } = new();
    public List<string> Writes { get; } = new();

    private void Write(string what, int pid)
    {
        if (Failures.TryGetValue(what, out var ex)) throw ex;
        Writes.Add($"{what}:{pid}");
    }

    private bool Read<T>(Dictionary<int, T> store, int pid, out T value, T fallback)
    {
        value = fallback;
        if (Unreadable.Contains(pid)) return false;
        if (store.TryGetValue(pid, out var stored)) value = stored;
        return true;
    }

    public bool TryGetIoPriority(int pid, out int priority) => Read(Io, pid, out priority, 2);
    public void SetIoPriority(int pid, int priority) { Write("IoPriority", pid); Io[pid] = priority; }
    public bool TryGetMemoryPriority(int pid, out int priority) => Read(Memory, pid, out priority, 5);
    public void SetMemoryPriority(int pid, int priority) { Write("MemoryPriority", pid); Memory[pid] = priority; }

    public bool TryGetEfficiencyMode(int pid, out bool controlled, out bool enabled)
    {
        var ok = Read(Eco, pid, out var state, (false, false));
        (controlled, enabled) = state;
        return ok;
    }

    public void SetEfficiencyMode(int pid, bool enabled) { Write("EfficiencyMode", pid); Eco[pid] = (true, enabled); }
    public bool TryGetPriorityBoostDisabled(int pid, out bool disabled) => Read(BoostDisabled, pid, out disabled, false);
    public void SetPriorityBoostDisabled(int pid, bool disabled) { Write("PriorityBoostDisabled", pid); BoostDisabled[pid] = disabled; }
    public bool TryGetDefaultCpuSets(int pid, out IReadOnlyList<uint> ids) => Read(CpuSets, pid, out ids, Array.Empty<uint>());
    public void SetDefaultCpuSets(int pid, IReadOnlyList<uint> ids) { Write("DefaultCpuSets", pid); CpuSets[pid] = ids.ToArray(); }
    public IReadOnlyList<CpuSetInfo> GetSystemCpuSets() => SystemCpuSets;
    public bool TryGetGpuPriority(int pid, out int priority) => Read(Gpu, pid, out priority, 2);
    public void SetGpuPriority(int pid, int priority) { Write("GpuPriority", pid); Gpu[pid] = priority; }

    public string? GetImagePath(int pid) => ImagePaths.GetValueOrDefault(pid);
    public string? GetProcessOwnerSid(int pid) => Owners.GetValueOrDefault(pid);
    public string? GetConsoleUserSid() => ConsoleUser;
    public string? ReadUserGpuPreference(string sid, string exePath) => Registry.GetValueOrDefault((sid, exePath));

    public void WriteUserGpuPreference(string sid, string exePath, string value)
    {
        Write("UserGpuPreference", 0);
        Registry[(sid, exePath)] = value;
    }
}

/// <summary>Keeps every log line so tests can check what was logged and how often.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
    }
}

public class PersistentProcessRuleApplierTests
{
    private const int Pid = 4242;
    private static readonly ProcessTarget Target = new(Pid, "game", @"C:\Games\game.exe");
    private readonly FakeProcessTuning _native = new();

    // === Shared behaviour (exercised through the I/O priority applier) ===

    [Fact]
    public void UnconfiguredSetting_IsNotTouched()
    {
        var result = new IoPriorityApplier(_native).Apply(new ProcessRule(), Target, onlyIfChanged: false);

        Assert.Equal(RuleApplyOutcome.NotConfigured, result.Outcome);
        Assert.Empty(_native.Writes);
    }

    [Fact]
    public void NativeFailure_IsReturnedNotThrown()
    {
        _native.Failures["IoPriority"] = new Win32Exception(5, "Access is denied");

        var result = new IoPriorityApplier(_native).Apply(new ProcessRule { IoPriority = 1 }, Target, onlyIfChanged: false);

        Assert.Equal(RuleApplyOutcome.Failed, result.Outcome);
        Assert.Contains("Access is denied", result.Detail);
    }

    [Fact]
    public void OnlyIfChanged_LeavesAMatchingValueAlone()
    {
        _native.Io[Pid] = 1;

        var result = new IoPriorityApplier(_native).Apply(new ProcessRule { IoPriority = 1 }, Target, onlyIfChanged: true);

        Assert.Equal(RuleApplyOutcome.Unchanged, result.Outcome);
        Assert.Empty(_native.Writes);
    }

    [Fact]
    public void OnlyIfChanged_PutsBackAChangedValue_AndSaysWhatItReplaced()
    {
        _native.Io[Pid] = 2;

        var result = new IoPriorityApplier(_native).Apply(new ProcessRule { IoPriority = 0 }, Target, onlyIfChanged: true);

        Assert.Equal(RuleApplyOutcome.Applied, result.Outcome);
        Assert.Equal("Very Low (was Normal)", result.Detail);
        Assert.Equal(0, _native.Io[Pid]);
    }

    [Fact]
    public void OnlyIfChanged_SkipsWhenTheCurrentValueCantBeRead()
    {
        _native.Unreadable.Add(Pid);

        var result = new IoPriorityApplier(_native).Apply(new ProcessRule { IoPriority = 0 }, Target, onlyIfChanged: true);

        Assert.Equal(RuleApplyOutcome.Skipped, result.Outcome);
        Assert.Empty(_native.Writes);
    }

    // === Per-setting values and validation ===

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, false)] // Critical is the memory manager's
    [InlineData(-1, false)]
    public void IoPriority_AcceptsVeryLowToHigh(int value, bool valid)
    {
        var result = new IoPriorityApplier(_native).Apply(new ProcessRule { IoPriority = value }, Target, false);

        Assert.Equal(valid ? RuleApplyOutcome.Applied : RuleApplyOutcome.Failed, result.Outcome);
        Assert.Equal(valid, _native.Io.TryGetValue(Pid, out var set) && set == value);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(6, false)]
    public void MemoryPriority_AcceptsVeryLowToNormal(int value, bool valid)
    {
        var result = new MemoryPriorityApplier(_native).Apply(new ProcessRule { MemoryPriority = value }, Target, false);

        Assert.Equal(valid ? RuleApplyOutcome.Applied : RuleApplyOutcome.Failed, result.Outcome);
        Assert.Equal(valid, _native.Memory.ContainsKey(Pid));
    }

    [Fact]
    public void EfficiencyMode_On_TurnsEcoQoSOn()
    {
        var result = new EfficiencyModeApplier(_native).Apply(new ProcessRule { EfficiencyMode = true }, Target, false);

        Assert.Equal(RuleApplyOutcome.Applied, result.Outcome);
        Assert.Equal((true, true), _native.Eco[Pid]);
    }

    [Fact]
    public void EfficiencyMode_Off_ExplicitlyClearsIt()
    {
        _native.Eco[Pid] = (true, true);

        var result = new EfficiencyModeApplier(_native).Apply(new ProcessRule { EfficiencyMode = false }, Target, false);

        Assert.Equal(RuleApplyOutcome.Applied, result.Outcome);
        Assert.Equal((true, false), _native.Eco[Pid]); // control bit kept: opted out, not handed back to Windows
    }

    [Fact]
    public void EfficiencyMode_Off_IsReappliedWhenWindowsIsDecidingAgain()
    {
        _native.Eco[Pid] = (false, false); // not controlled: Windows may throttle it

        var result = new EfficiencyModeApplier(_native).Apply(new ProcessRule { EfficiencyMode = false }, Target, onlyIfChanged: true);

        Assert.Equal(RuleApplyOutcome.Applied, result.Outcome);
        Assert.Equal("off (was not set)", result.Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PriorityBoost_SetsTheRequestedState(bool disable)
    {
        _native.BoostDisabled[Pid] = !disable;

        var result = new PriorityBoostApplier(_native).Apply(new ProcessRule { DisablePriorityBoost = disable }, Target, onlyIfChanged: true);

        Assert.Equal(RuleApplyOutcome.Applied, result.Outcome);
        Assert.Equal(disable, _native.BoostDisabled[Pid]);
    }

    [Fact]
    public void CpuSets_AreAppliedFromTheStoredIdList()
    {
        var result = new CpuSetsApplier(_native).Apply(new ProcessRule { CpuSetIds = "257, 256" }, Target, false);

        Assert.Equal(RuleApplyOutcome.Applied, result.Outcome);
        Assert.Equal(new uint[] { 257, 256 }, _native.CpuSets[Pid]);
        Assert.Equal("256,257", result.Detail);
    }

    [Fact]
    public void CpuSets_EmptyListClearsThem()
    {
        _native.CpuSets[Pid] = new uint[] { 256 };

        var result = new CpuSetsApplier(_native).Apply(new ProcessRule { CpuSetIds = "" }, Target, false);

        Assert.Equal(RuleApplyOutcome.Applied, result.Outcome);
        Assert.Empty(_native.CpuSets[Pid]);
    }

    [Theory]
    [InlineData("256,999", "999")]
    [InlineData("first", "not a list")]
    public void CpuSets_RejectsBadIdsWithoutCallingWindows(string ids, string expectedError)
    {
        var result = new CpuSetsApplier(_native).Apply(new ProcessRule { CpuSetIds = ids }, Target, false);

        Assert.Equal(RuleApplyOutcome.Failed, result.Outcome);
        Assert.Contains(expectedError, result.Detail);
        Assert.Empty(_native.Writes);
    }

    [Fact]
    public void CpuSets_OrderDoesNotCountAsAChange()
    {
        _native.CpuSets[Pid] = new uint[] { 257, 256 };

        var result = new CpuSetsApplier(_native).Apply(new ProcessRule { CpuSetIds = "256,257" }, Target, onlyIfChanged: true);

        Assert.Equal(RuleApplyOutcome.Unchanged, result.Outcome);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(5, false)] // Realtime is not offered
    public void GpuPriority_AcceptsIdleToHigh(int value, bool valid)
    {
        var result = new GpuPriorityApplier(_native).Apply(new ProcessRule { GpuPriority = value }, Target, false);

        Assert.Equal(valid ? RuleApplyOutcome.Applied : RuleApplyOutcome.Failed, result.Outcome);
        Assert.Equal(valid, _native.Gpu.ContainsKey(Pid));
    }

    // === GPU adapter preference (registry) ===

    [Fact]
    public void GpuPreference_IsWrittenUnderTheProcessOwnersHive()
    {
        _native.Owners[Pid] = "S-1-5-21-1-2-3-1001";
        _native.ConsoleUser = "S-1-5-21-1-2-3-1002";

        var result = new GpuPreferenceApplier(_native).Apply(new ProcessRule { GpuPreference = 2 }, Target, false);

        Assert.Equal(RuleApplyOutcome.Applied, result.Outcome);
        Assert.Equal("GpuPreference=2;", _native.Registry[("S-1-5-21-1-2-3-1001", @"C:\Games\game.exe")]);
        Assert.DoesNotContain(_native.Registry.Keys, k => k.Sid == "S-1-5-21-1-2-3-1002");
    }

    [Fact]
    public void GpuPreference_FallsBackToTheConsoleUser_AndReadsThePathItself()
    {
        _native.ConsoleUser = "S-1-5-21-1-2-3-1002";
        _native.ImagePaths[Pid] = @"D:\Apps\render.exe";

        var result = new GpuPreferenceApplier(_native).Apply(new ProcessRule { GpuPreference = 1 }, Target with { FilePath = null }, false);

        Assert.Equal(RuleApplyOutcome.Applied, result.Outcome);
        Assert.Equal("GpuPreference=1;", _native.Registry[("S-1-5-21-1-2-3-1002", @"D:\Apps\render.exe")]);
    }

    [Fact]
    public void GpuPreference_KeepsOtherDirectXSettings_AndSkipsAnIdenticalWrite()
    {
        _native.Owners[Pid] = "S-1-5-21-9";
        var key = ("S-1-5-21-9", @"C:\Games\game.exe");
        _native.Registry[key] = "SwapEffectUpgradeEnable=1;GpuPreference=0;";
        var applier = new GpuPreferenceApplier(_native);

        Assert.Equal(RuleApplyOutcome.Applied, applier.Apply(new ProcessRule { GpuPreference = 2 }, Target, false).Outcome);
        Assert.Equal("GpuPreference=2;SwapEffectUpgradeEnable=1;", _native.Registry[key]);

        _native.Writes.Clear();
        Assert.Equal(RuleApplyOutcome.Unchanged, applier.Apply(new ProcessRule { GpuPreference = 2 }, Target, false).Outcome);
        Assert.Empty(_native.Writes);
    }

    [Fact]
    public void GpuPreference_FailsCleanlyWithoutAUserOrAHive()
    {
        var applier = new GpuPreferenceApplier(_native);
        Assert.Equal(RuleApplyOutcome.Failed, applier.Apply(new ProcessRule { GpuPreference = 2 }, Target, false).Outcome);

        _native.Owners[Pid] = "S-1-5-21-9";
        _native.Failures["UserGpuPreference"] = new InvalidOperationException("the registry hive of S-1-5-21-9 is not loaded");
        var result = applier.Apply(new ProcessRule { GpuPreference = 2 }, Target, false);
        Assert.Equal(RuleApplyOutcome.Failed, result.Outcome);
        Assert.Contains("not loaded", result.Detail);

        Assert.Equal(RuleApplyOutcome.Failed, applier.Apply(new ProcessRule { GpuPreference = 3 }, Target, false).Outcome);
    }

    [Theory]
    [InlineData(null, false, 0)]
    [InlineData("GpuPreference=2;", true, 2)]
    [InlineData("SwapEffectUpgradeEnable=1; gpupreference = 1 ;", true, 1)]
    [InlineData("SwapEffectUpgradeEnable=1;", false, 0)]
    public void GpuPreference_ParsesTheRegistryString(string? value, bool found, int expected)
    {
        Assert.Equal(found, GpuPreferenceApplier.TryParse(value, out var preference));
        Assert.Equal(expected, preference);
    }
}

public class PersistentRuleServiceTests
{
    private readonly FakeProcessTuning _native = new();
    private readonly ListLogger<PersistentRuleService> _log = new();
    private readonly Mock<IProcessMonitor> _monitor = new();
    private readonly Mock<IRuleStore> _rules = new();
    private readonly List<ProcessInfo> _processes = new();

    public PersistentRuleServiceTests()
    {
        _monitor.Setup(m => m.GetAllProcesses()).Returns(() => _processes);
        _monitor.Setup(m => m.PollingInterval).Returns(TimeSpan.FromSeconds(1));
    }

    private PersistentRuleService Create() => new(_log, _native, _monitor.Object, _rules.Object);

    private sealed class ThrowingApplier : IProcessRuleApplier
    {
        public string Setting => "Broken";
        public RuleApplyResult Apply(ProcessRule rule, ProcessTarget target, bool onlyIfChanged) => throw new InvalidOperationException("bug");
    }

    [Fact]
    public void Apply_RunsEveryConfiguredSetting_AndLogsEachOne()
    {
        var rule = new ProcessRule { IoPriority = 1, MemoryPriority = 3, EfficiencyMode = true, DisablePriorityBoost = true, CpuSetIds = "256", GpuPriority = 3 };

        var results = Create().Apply(rule, new ProcessTarget(10, "game"));

        Assert.Equal(6, results.Count); // GPU preference isn't configured
        Assert.All(results, r => Assert.Equal(RuleApplyOutcome.Applied, r.Outcome));
        Assert.Equal(6, _log.Entries.Count(e => e.Level == LogLevel.Information && e.Message.StartsWith("Applied")));
        Assert.Contains(_log.Entries, e => e.Message == "Applied I/O priority Low to game (10)");
    }

    [Fact]
    public void Apply_OneBrokenApplierDoesNotStopTheOthers()
    {
        var service = new PersistentRuleService(_log, new IProcessRuleApplier[] { new ThrowingApplier(), new IoPriorityApplier(_native) }, _monitor.Object, _rules.Object);

        var results = service.Apply(new ProcessRule { IoPriority = 0 }, new ProcessTarget(10, "game"));

        Assert.Equal(RuleApplyOutcome.Failed, results[0].Outcome);
        Assert.Equal(RuleApplyOutcome.Applied, results[1].Outcome);
        Assert.Equal(0, _native.Io[10]);
    }

    [Fact]
    public void Reapply_RestoresChangedSettings_OnlyForRulesThatAskForIt()
    {
        _processes.Add(new ProcessInfo { Pid = 1, Name = "watched" });
        _processes.Add(new ProcessInfo { Pid = 2, Name = "unwatched" });
        _processes.Add(new ProcessInfo { Pid = 3, Name = "banned" });
        _processes.Add(new ProcessInfo { Pid = 4, Name = "norule" });
        _rules.Setup(r => r.GetProcessRule("watched")).Returns(new ProcessRule { IoPriority = 0, EfficiencyMode = true, ReapplyIfChanged = true });
        _rules.Setup(r => r.GetProcessRule("unwatched")).Returns(new ProcessRule { IoPriority = 0 });
        _rules.Setup(r => r.GetProcessRule("banned")).Returns(new ProcessRule { IoPriority = 0, ReapplyIfChanged = true, DisallowExecution = true });
        var service = Create();

        Assert.Equal(2, service.ReapplyChanged());
        Assert.Equal(new[] { "IoPriority:1", "EfficiencyMode:1" }, _native.Writes);

        // Nothing changed since: nothing to do, nothing logged.
        var logged = _log.Entries.Count;
        Assert.Equal(0, service.ReapplyChanged());
        Assert.Equal(logged, _log.Entries.Count);

        // Something else lowers it again: put back and logged as a re-apply.
        _native.Io[1] = 2;
        Assert.Equal(1, service.ReapplyChanged());
        Assert.Equal(0, _native.Io[1]);
        Assert.Contains(_log.Entries, e => e.Message == "Re-applied I/O priority Very Low (was Normal) to watched (1): it had been changed");
    }

    [Fact]
    public void Reapply_LogsARepeatedFailureOnce_AndAgainAfterItRecovers()
    {
        _processes.Add(new ProcessInfo { Pid = 1, Name = "watched" });
        _rules.Setup(r => r.GetProcessRule("watched")).Returns(new ProcessRule { IoPriority = 0, ReapplyIfChanged = true });
        _native.Failures["IoPriority"] = new Win32Exception(5, "Access is denied");
        var service = Create();

        for (var i = 0; i < 3; i++) service.ReapplyChanged();
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);

        _native.Failures.Clear();
        service.ReapplyChanged();
        _native.Io[1] = 2;
        _native.Failures["IoPriority"] = new Win32Exception(5, "Access is denied");
        service.ReapplyChanged();
        Assert.Equal(2, _log.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public void Governor_AppliesTheNewSettingsWhenAProcessStarts()
    {
        var self = Environment.ProcessId;
        _rules.Setup(r => r.GetProcessRule("testhost")).Returns(new ProcessRule { ProcessName = "testhost.exe", IoPriority = 1, GpuPriority = 1 });
        var governor = new ProcessGovernor(NullLogger<ProcessGovernor>.Instance, _monitor.Object, Mock.Of<IProBalanceEngine>(), Mock.Of<ICpuLimiter>(),
            Mock.Of<ISmartTrimService>(), Mock.Of<IPowerManager>(), Mock.Of<IWatchdogService>(), _rules.Object, persistentRules: Create());

        _monitor.Raise(m => m.ProcessStarted += null, _monitor.Object, new ProcessInfo { Pid = self, Name = "testhost" });

        Assert.Equal(1, _native.Io[self]);
        Assert.Equal(1, _native.Gpu[self]);
        governor.Stop();
    }

    [Fact]
    public void Governor_DoesNotApplySettingsToADisallowedProcess()
    {
        _rules.Setup(r => r.GetProcessRule("ghost")).Returns(new ProcessRule { IoPriority = 1, DisallowExecution = true });
        var governor = new ProcessGovernor(NullLogger<ProcessGovernor>.Instance, _monitor.Object, Mock.Of<IProBalanceEngine>(), Mock.Of<ICpuLimiter>(),
            Mock.Of<ISmartTrimService>(), Mock.Of<IPowerManager>(), Mock.Of<IWatchdogService>(), _rules.Object, persistentRules: Create());

        _monitor.Raise(m => m.ProcessStarted += null, _monitor.Object, new ProcessInfo { Pid = int.MaxValue - 1, Name = "ghost" });

        Assert.Empty(_native.Writes);
        governor.Stop();
    }
}

public class PersistentRuleIpcAndUiTests
{
    private sealed class Caller : IIpcCaller
    {
        public string Name => IsAdministrator ? @"TEST\admin" : @"TEST\user";
        public bool IsAdministrator { get; init; }
        public bool CanOpenProcess(int pid, ProcessAccessFlags access) => false;
    }

    private readonly Mock<IRuleStore> _store = new();
    private readonly IpcDispatcher _dispatcher;
    private ProcessRule? _saved;

    public PersistentRuleIpcAndUiTests()
    {
        var monitor = new Mock<IProcessMonitor>();
        monitor.Setup(m => m.GetAllProcesses()).Returns(Array.Empty<ProcessInfo>());
        _store.Setup(s => s.GetSettings()).Returns(new AppSettings { Id = 1 });
        _store.Setup(s => s.SaveProcessRule(It.IsAny<ProcessRule>())).Callback<ProcessRule>(r => _saved = r);
        var applier = new SettingsApplier(NullLogger<SettingsApplier>.Instance, monitor.Object, Mock.Of<ISmartTrimService>(), Mock.Of<IPowerManager>(), Mock.Of<IProBalanceEngine>());
        _dispatcher = new IpcDispatcher(NullLogger<IpcDispatcher>.Instance, _store.Object, applier, Mock.Of<IProBalanceEngine>(), Mock.Of<IPowerManager>(),
            Mock.Of<ISmartTrimService>(), Mock.Of<ICpuLimiter>(), Mock.Of<IProcessController>(), monitor.Object);
    }

    private CommandResult SetRule(RuleDto dto, bool admin = true)
    {
        var reply = _dispatcher.Dispatch(new CommandMessage { Type = MessageType.SetRule, Payload = JsonSerializer.Serialize(dto) }, new Caller { IsAdministrator = admin });
        return JsonSerializer.Deserialize<CommandResult>(reply.Payload)!;
    }

    [Fact]
    public void GetRules_ReturnsTheNewFields_ToAnyUser()
    {
        _store.Setup(s => s.GetProcessRules()).Returns(new[]
        {
            new ProcessRule { Id = 7, ProcessName = "game.exe", IoPriority = 3, MemoryPriority = 5, EfficiencyMode = false, DisablePriorityBoost = true,
                CpuSetIds = "256,257", GpuPriority = 4, GpuPreference = 2, ReapplyIfChanged = true }
        });

        var reply = _dispatcher.Dispatch(new CommandMessage { Type = MessageType.GetRules }, new Caller());

        Assert.Equal(MessageType.RuleList, reply.Type);
        var dto = Assert.Single(JsonSerializer.Deserialize<List<RuleDto>>(reply.Payload)!);
        Assert.Equal((7, "game.exe", "Process"), (dto.Id, dto.Name, dto.Type));
        Assert.Equal((3, 5, false, true), (dto.IoPriority, dto.MemoryPriority, dto.EfficiencyMode, dto.DisablePriorityBoost));
        Assert.Equal(("256,257", 4, 2, true), (dto.CpuSetIds, dto.GpuPriority, dto.GpuPreference, dto.ReapplyIfChanged));
    }

    [Fact]
    public void SetRule_UpdatesTheNewFields_AndKeepsTheRestOfTheRule()
    {
        _store.Setup(s => s.GetProcessRule("game.exe")).Returns(new ProcessRule { Id = 7, ProcessName = "game.exe", PriorityClass = 128, CpuLimitPercent = 50 });

        var result = SetRule(new RuleDto { Name = "game.exe", IoPriority = 0, EfficiencyMode = true, CpuSetIds = "256", ReapplyIfChanged = true });

        Assert.True(result.Success, result.Error);
        Assert.NotNull(_saved);
        Assert.Equal((7, 128, 50.0), (_saved!.Id, _saved.PriorityClass, _saved.CpuLimitPercent));
        Assert.Equal((0, true, "256", true), (_saved.IoPriority, _saved.EfficiencyMode, _saved.CpuSetIds, _saved.ReapplyIfChanged));
    }

    [Theory]
    [InlineData(9, null, null, null)]
    [InlineData(null, 0, null, null)]
    [InlineData(null, null, 5, null)]
    [InlineData(null, null, null, "x")]
    public void SetRule_RejectsOutOfRangeValues(int? io, int? memory, int? gpu, string? cpuSets)
    {
        var result = SetRule(new RuleDto { Name = "game.exe", IoPriority = io, MemoryPriority = memory, GpuPriority = gpu, CpuSetIds = cpuSets });

        Assert.False(result.Success);
        Assert.Null(_saved);
    }

    [Fact]
    public void SetRule_IsForAdministratorsOnly()
    {
        // A rule applies to every user's processes, so a standard user may not create one.
        var result = SetRule(new RuleDto { Name = "game.exe", IoPriority = 0 }, admin: false);

        Assert.False(result.Success);
        Assert.Contains("Access denied", result.Error);
        Assert.Null(_saved);
    }

    [Fact]
    public void RulesGrid_SummarisesTheNewFields()
    {
        var text = RulesViewModel.DescribeDefaults(new ProcessRule
        {
            IoPriority = 1, MemoryPriority = 2, EfficiencyMode = false, DisablePriorityBoost = true,
            CpuSetIds = "256", GpuPriority = 3, GpuPreference = 2, ReapplyIfChanged = true
        });

        Assert.Equal("I/O Low · Memory Low · Eco off · No boost · CPU Sets 256 · GPU Above Normal · High performance · re-apply", text);
        Assert.Equal("", RulesViewModel.DescribeDefaults(new ProcessRule { ReapplyIfChanged = true }));
    }

    [Fact]
    public void CpuSetIds_ParseAndFormat()
    {
        Assert.True(CpuSets.TryParseIds("257, 256;256", out var ids));
        Assert.Equal(new uint[] { 257, 256 }, ids);
        Assert.Equal("256,257", CpuSets.FormatIds(ids));
        Assert.True(CpuSets.TryParseIds("", out ids));
        Assert.Empty(ids);
        Assert.False(CpuSets.TryParseIds("-1", out _));
        Assert.False(CpuSets.TryParseIds(null, out _));
    }

    [Fact]
    public void CpuSetInformation_IsParsedRecordByRecord()
    {
        // Two SYSTEM_CPU_SET_INFORMATION records; the second is larger than today's 32 bytes, as a future Windows may make it.
        var buffer = new byte[32 + 40];
        void Record(int offset, int size, uint id, byte lp, byte core, byte effClass, byte flags)
        {
            BitConverter.GetBytes(size).CopyTo(buffer, offset);
            BitConverter.GetBytes(0).CopyTo(buffer, offset + 4);
            BitConverter.GetBytes(id).CopyTo(buffer, offset + 8);
            buffer[offset + 14] = lp;
            buffer[offset + 15] = core;
            buffer[offset + 18] = effClass;
            buffer[offset + 19] = flags;
        }
        Record(0, 32, 256, 0, 0, 1, 0);
        Record(32, 40, 257, 1, 4, 0, 0x1);

        var sets = CpuSets.Parse(buffer);

        Assert.Equal(2, sets.Count);
        Assert.Equal((256u, (byte)0, (byte)0, (byte)1, false), (sets[0].Id, sets[0].LogicalProcessorIndex, sets[0].CoreIndex, sets[0].EfficiencyClass, sets[0].Parked));
        Assert.Equal((257u, (byte)1, (byte)4, (byte)0, true), (sets[1].Id, sets[1].LogicalProcessorIndex, sets[1].CoreIndex, sets[1].EfficiencyClass, sets[1].Parked));
    }
}
