using System.Diagnostics;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>In-memory affinity masks and start times for the CPU Limiter, so no real process is touched.</summary>
internal sealed class FakeAffinityAccess : IProcessAffinityAccess
{
    public Dictionary<int, ProcessAffinityState> Processes { get; } = new();
    public List<(int Pid, long Mask)> Sets { get; } = new();

    public ProcessAffinityState? Query(int pid) => Processes.TryGetValue(pid, out var s) ? s : null;

    public bool TrySetAffinityMask(int pid, DateTime startTimeUtc, long mask)
    {
        if (!Processes.TryGetValue(pid, out var s) || s.StartTimeUtc != startTimeUtc) return false;
        Processes[pid] = s with { Mask = mask };
        Sets.Add((pid, mask));
        return true;
    }

    public long MaskOf(int pid) => Processes[pid].Mask;
}

/// <summary>
/// Drives a <see cref="CpuAffinityLimiter"/> on a fake clock. <see cref="At"/> moves the clock to an absolute
/// time and runs one evaluation, the way the 1 s poll would.
/// </summary>
internal sealed class CpuAffinityLimiterHarness : IDisposable
{
    public long Now { get; private set; }
    public List<ProcessInfo> Processes { get; } = new();
    public Dictionary<string, ProcessRule> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ProBalanceAction> Logged { get; } = new();
    public FakeAffinityAccess Access { get; } = new();
    public Mock<IProcessMonitor> Monitor { get; } = new();
    public CpuAffinityLimiter Limiter { get; }

    public CpuAffinityLimiterHarness()
    {
        Monitor.Setup(m => m.GetAllProcesses()).Returns(() => Processes.ToList());

        var store = new Mock<IRuleStore>();
        store.Setup(s => s.GetProcessRule(It.IsAny<string>()))
            .Returns((string name) => Rules.TryGetValue(name, out var r) ? r : null);
        store.Setup(s => s.LogProBalanceActionAsync(It.IsAny<ProBalanceAction>()))
            .Callback((ProBalanceAction a) => Logged.Add(a))
            .Returns(Task.CompletedTask);

        Limiter = new CpuAffinityLimiter(NullLogger<CpuAffinityLimiter>.Instance, Monitor.Object, store.Object, Access, () => Now);
    }

    /// <summary>Threshold 80 %, 5 s over it, remove 2 cores for 30 s.</summary>
    public ProcessRule AddRule(string name = "hog.exe", double threshold = 80, int over = 5, int cores = 2, int duration = 30)
    {
        var rule = new ProcessRule
        {
            ProcessName = name,
            CpuLimitThresholdPercent = threshold,
            CpuLimitOverThresholdSeconds = over,
            CpuLimitCoresToRemove = cores,
            CpuLimitDurationSeconds = duration
        };
        Rules[name] = rule;
        return rule;
    }

    public ProcessInfo Add(int pid, double cpu, long mask = 0xFF, string name = "hog.exe")
    {
        var info = new ProcessInfo { Pid = pid, Name = name, CpuPercent = cpu };
        Processes.Add(info);
        Access.Processes[pid] = new ProcessAffinityState(mask, new DateTime(2026, 1, 1, 0, 0, pid % 60, DateTimeKind.Utc));
        return info;
    }

    public void Exit(int pid)
    {
        Processes.RemoveAll(p => p.Pid == pid);
        Access.Processes.Remove(pid);
    }

    public void At(long ms)
    {
        Now = ms;
        Limiter.Evaluate(null);
    }

    public void Dispose() => Limiter.Dispose();
}

public class CpuAffinityLimiterTests
{
    [Theory]
    [InlineData(0xFFL, 2, 0x3FL, 2)]
    [InlineData(0b1010L, 1, 0b0010L, 1)]       // removes the highest set bit, not bit 7 of a full byte
    [InlineData(0x3L, 5, 0x1L, 1)]             // never below one core
    [InlineData(0x1L, 1, 0x1L, 0)]             // already a single core
    [InlineData(0L, 1, 0L, 0)]
    [InlineData(unchecked((long)0x8000_0000_0000_0003UL), 1, 0x3L, 1)] // bit 63 (64-core group)
    public void RemoveTopCores_ClearsHighestSetBitsAndKeepsOne(long mask, int cores, long expected, int expectedRemoved)
    {
        Assert.Equal(expected, CpuAffinityLimiter.RemoveTopCores(mask, cores, out var removed));
        Assert.Equal(expectedRemoved, removed);
    }

    [Fact]
    public void Constrains_OnlyAfterUsageStaysOverThresholdForTheWholeWindow()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule();
        h.Add(100, cpu: 90);

        h.At(0);
        h.At(4_999);
        Assert.Equal(0xFF, h.Access.MaskOf(100));

        h.At(5_000);
        Assert.Equal(0x3F, h.Access.MaskOf(100));
    }

    [Fact]
    public void ASampleBelowTheThreshold_RestartsTheWindow()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule();
        var p = h.Add(100, cpu: 90);

        h.At(0);
        h.At(3_000);
        p.CpuPercent = 79.9;
        h.At(4_000);
        p.CpuPercent = 90;
        h.At(5_000);  // window restarts here
        h.At(9_999);
        Assert.Equal(0xFF, h.Access.MaskOf(100));

        h.At(10_000);
        Assert.Equal(0x3F, h.Access.MaskOf(100));
    }

    [Fact]
    public void ThresholdIsInclusive_AndZeroSecondsAppliesOnFirstSample()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule(threshold: 50, over: 0);
        h.Add(100, cpu: 50);

        h.At(0);
        Assert.Equal(0x3F, h.Access.MaskOf(100));
    }

    [Fact]
    public void Apply_LogsProcessLassoStyleMessage()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule(cores: 2, duration: 30);
        h.Add(100, cpu: 90);

        h.At(0);
        h.At(5_000);

        var entry = Assert.Single(h.Logged);
        Assert.False(entry.IsRestored);
        Assert.Equal(100, entry.Pid);
        Assert.Equal("hog.exe", entry.ProcessName);
        Assert.StartsWith("Constraining CPU affinity by 2 CPUs for 30000 ms. New CPU affinity is 0x3F, original was 0xFF", entry.Reason);
        Assert.Equal(1, h.Limiter.ActiveConstraints);
    }

    [Fact]
    public void RestoresOriginalAffinity_WhenTheDurationEnds()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule(duration: 30);
        h.Add(100, cpu: 90, mask: 0x0F);

        h.At(0);
        h.At(5_000);
        Assert.Equal(0x03, h.Access.MaskOf(100));

        h.At(34_999);
        Assert.Equal(0x03, h.Access.MaskOf(100));

        h.At(35_000);
        Assert.Equal(0x0F, h.Access.MaskOf(100));
        Assert.Equal(0, h.Limiter.ActiveConstraints);
        var restored = h.Logged.Last();
        Assert.True(restored.IsRestored);
        Assert.Contains("0xF", restored.Reason);
    }

    [Fact]
    public void AfterRestore_StillBusyProcessIsConstrainedAgainAfterAFreshWindow()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule(over: 5, duration: 10);
        h.Add(100, cpu: 90);

        h.At(0);
        h.At(5_000);   // constrained until 15 000
        h.At(15_000);  // restored, and a new window starts on the same tick
        Assert.Equal(0xFF, h.Access.MaskOf(100));

        h.At(19_999);
        Assert.Equal(0xFF, h.Access.MaskOf(100));
        h.At(20_000);
        Assert.Equal(0x3F, h.Access.MaskOf(100));
    }

    [Fact]
    public void AffinityChangedBySomeoneElse_IsLeftAlone()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule(duration: 30);
        h.Add(100, cpu: 90);

        h.At(0);
        h.At(5_000);
        // The user pins it to two cores while it is constrained (and it calms down, so no new window opens).
        h.Access.Processes[100] = h.Access.Processes[100] with { Mask = 0x05 };
        h.Processes[0].CpuPercent = 10;
        h.At(6_000);
        Assert.Equal(0, h.Limiter.ActiveConstraints);

        h.At(35_000);
        Assert.Equal(0x05, h.Access.MaskOf(100));
        Assert.DoesNotContain(h.Logged, a => a.IsRestored);
    }

    [Fact]
    public void AffinityChangedBySomeoneElse_BecomesTheOriginalForTheNextLimit()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule(cores: 1, duration: 30);
        h.Add(100, cpu: 90);

        h.At(0);
        h.At(5_000);
        h.Access.Processes[100] = h.Access.Processes[100] with { Mask = 0x0F };
        h.At(6_000);   // still busy: a new window starts from the user's mask
        h.At(11_000);
        Assert.Equal(0x07, h.Access.MaskOf(100));

        h.At(41_000);
        Assert.Equal(0x0F, h.Access.MaskOf(100));
    }

    [Fact]
    public void ProcessExit_DropsTheConstraintWithoutTouchingThePid()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule();
        h.Add(100, cpu: 90);

        h.At(0);
        h.At(5_000);
        var setsBefore = h.Access.Sets.Count;

        h.Exit(100);
        h.At(6_000);
        Assert.Equal(0, h.Limiter.ActiveConstraints);

        h.At(60_000);
        Assert.Equal(setsBefore, h.Access.Sets.Count);
        Assert.DoesNotContain(h.Logged, a => a.IsRestored);
    }

    [Fact]
    public void ProcessExitedEvent_DropsTheConstraint()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule();
        h.Add(100, cpu: 90);
        h.At(0);
        h.At(5_000);

        h.Monitor.Raise(m => m.ProcessExited += null, h.Monitor.Object, 100);

        Assert.Equal(0, h.Limiter.ActiveConstraints);
    }

    [Fact]
    public void RecycledPid_IsNotRestoredToTheOldMask()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule(threshold: 80);
        h.Add(100, cpu: 90);
        h.At(0);
        h.At(5_000);

        // Same PID and mask, but a different process (different creation time) that isn't busy.
        h.Exit(100);
        var newcomer = h.Add(100, cpu: 1, mask: 0x3F);
        h.Access.Processes[100] = h.Access.Processes[100] with { StartTimeUtc = DateTime.UtcNow };
        h.At(40_000);

        Assert.Equal(0x3F, h.Access.MaskOf(newcomer.Pid));
        Assert.Equal(0, h.Limiter.ActiveConstraints);
        Assert.DoesNotContain(h.Logged, a => a.IsRestored);
    }

    [Fact]
    public void ProcessesWithoutAnAffinityRule_AreIgnored()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.Rules["other.exe"] = new ProcessRule { ProcessName = "other.exe", CpuLimitPercent = 50 }; // hard cap only
        h.Add(100, cpu: 99);
        h.Add(101, cpu: 99, name: "other.exe");

        h.At(0);
        h.At(60_000);

        Assert.Empty(h.Access.Sets);
    }

    [Fact]
    public void SingleCoreProcesses_AndUnreadableAffinity_AreLeftAlone()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule(over: 0);
        h.Add(100, cpu: 90, mask: 0x10);
        // A process whose threads span several processor groups: the access layer can't read a mask.
        h.Processes.Add(new ProcessInfo { Pid = 101, Name = "hog.exe", CpuPercent = 90 });

        h.At(0);
        h.At(1_000);

        Assert.Empty(h.Access.Sets);
        Assert.Empty(h.Logged);
    }

    [Fact]
    public void RemovingTheRule_RestoresEarly()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule();
        h.Add(100, cpu: 90);
        h.At(0);
        h.At(5_000);

        h.Rules.Clear();
        h.At(6_000);

        Assert.Equal(0xFF, h.Access.MaskOf(100));
        Assert.Contains("rule removed", h.Logged.Last().Reason);
    }

    [Fact]
    public async Task Stop_RestoresEveryConstrainedProcess()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.AddRule(over: 0);
        h.Add(100, cpu: 90);
        h.Add(101, cpu: 90, mask: 0x0F);
        h.At(0);
        Assert.Equal(2, h.Limiter.ActiveConstraints);

        await h.Limiter.StopAsync(CancellationToken.None);

        Assert.Equal(0xFF, h.Access.MaskOf(100));
        Assert.Equal(0x0F, h.Access.MaskOf(101));
        Assert.Equal(0, h.Limiter.ActiveConstraints);
    }

    [Fact]
    public void NullRuleFields_UseTheDefaults()
    {
        using var h = new CpuAffinityLimiterHarness();
        h.Rules["hog.exe"] = new ProcessRule { ProcessName = "hog.exe", CpuLimitThresholdPercent = 80 };
        h.Add(100, cpu: 90);

        h.At(0);
        h.At(CpuAffinityLimitDefaults.OverThresholdSeconds * 1000L);
        Assert.Equal(0x7F, h.Access.MaskOf(100)); // one core

        h.At((CpuAffinityLimitDefaults.OverThresholdSeconds + CpuAffinityLimitDefaults.DurationSeconds) * 1000L);
        Assert.Equal(0xFF, h.Access.MaskOf(100));
    }

    // ---------- Win32 access, against a real idle process ----------

    [Fact]
    public void Win32Access_SetsAndReadsAffinity_AndRefusesARecycledPid()
    {
        if (Environment.ProcessorCount < 2) return;

        // A process that does next to nothing, so Process Lasso leaves it alone; its priority isn't assumed.
        using var p = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;
        try
        {
            var access = new Win32ProcessAffinityAccess();
            var state = access.Query(p.Id);
            Assert.NotNull(state);
            Assert.NotEqual(0, state!.Value.Mask);

            var reduced = CpuAffinityLimiter.RemoveTopCores(state.Value.Mask, 1, out var removed);
            Assert.Equal(1, removed);

            Assert.False(access.TrySetAffinityMask(p.Id, state.Value.StartTimeUtc.AddSeconds(-1), reduced));
            Assert.Equal(state.Value.Mask, access.Query(p.Id)!.Value.Mask);

            Assert.True(access.TrySetAffinityMask(p.Id, state.Value.StartTimeUtc, reduced));
            Assert.Equal(reduced, access.Query(p.Id)!.Value.Mask);

            Assert.True(access.TrySetAffinityMask(p.Id, state.Value.StartTimeUtc, state.Value.Mask));
            Assert.Equal(state.Value.Mask, access.Query(p.Id)!.Value.Mask);
        }
        finally
        {
            try { p.Kill(); } catch { }
        }
    }

    // ---------- Rule editor and persistence ----------

    /// <summary>Its own database file: EnsureCreated racing other test classes on the shared one is flaky.</summary>
    private sealed class PrivateDbContext(string path) : ProGuardDbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseSqlite($"Data Source={path};Pooling=False");
    }

    [Fact]
    public void AffinityLimitFields_PersistInTheDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proguard-affinity-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new PrivateDbContext(path))
            {
                db.Database.EnsureCreated();
                db.ProcessRules.Add(new ProcessRule
                {
                    ProcessName = "render.exe",
                    CpuLimitThresholdPercent = 70,
                    CpuLimitOverThresholdSeconds = 3,
                    CpuLimitCoresToRemove = 4,
                    CpuLimitDurationSeconds = 45
                });
                db.SaveChanges();
            }

            using (var db = new PrivateDbContext(path))
            {
                var saved = db.ProcessRules.Single(r => r.ProcessName == "render.exe");
                Assert.Equal(70, saved.CpuLimitThresholdPercent);
                Assert.Equal(3, saved.CpuLimitOverThresholdSeconds);
                Assert.Equal(4, saved.CpuLimitCoresToRemove);
                Assert.Equal(45, saved.CpuLimitDurationSeconds);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
