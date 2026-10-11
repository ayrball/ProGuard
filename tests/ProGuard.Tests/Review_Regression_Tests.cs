using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Data;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

/// <summary>
/// Regression tests for bugs found in the code review. Tests that need a live process spawn a
/// throw-away busy-loop child and only ever act on that child's PID.
/// </summary>
public class Review_Regression_Tests
{
    // ---------- helpers ----------

    private static Process StartSpinner()
    {
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"while($true){}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        return Process.Start(psi)!;
    }

    private static void KillQuietly(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { }
        p.Dispose();
    }

    /// <summary>CPU seconds consumed by <paramref name="p"/> during <paramref name="window"/> of wall time.</summary>
    private static double CpuSecondsOver(Process p, TimeSpan window)
    {
        p.Refresh();
        var before = p.TotalProcessorTime;
        Thread.Sleep(window);
        p.Refresh();
        return (p.TotalProcessorTime - before).TotalSeconds;
    }

    /// <summary>Collects log lines so a failing assertion can show what the component itself reported.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add($"{logLevel}: {formatter(state, exception)}{(exception != null ? " | " + exception.Message : "")}");
        }
        public override string ToString() { lock (Lines) return string.Join(" || ", Lines); }
    }

    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(500);
    private const int WindowSlices = 2; // a "window" is two slices = 1s of wall time

    /// <summary>CPU seconds used in each consecutive half-second slice.</summary>
    private static double[] Profile(Process p, int slices)
    {
        var result = new double[slices];
        for (int i = 0; i < slices; i++) result[i] = CpuSecondsOver(p, Slice);
        return result;
    }

    private static IEnumerable<double> Windows(double[] slices) =>
        Enumerable.Range(0, slices.Length - WindowSlices + 1).Select(i => slices.Skip(i).Take(WindowSlices).Sum());

    private static string Show(double[] slices) => "[" + string.Join(" ", slices.Select(s => s.ToString("F2"))) + "]";

    private class TempDbContext : ProGuardDbContext
    {
        private readonly string _path;
        public TempDbContext(string path) { _path = path; }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlite($"Data Source={_path}");
    }

    private sealed class RuleStoreFixture : IDisposable
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"proguard_test_{Guid.NewGuid():N}.db");
        public ServiceProvider Provider { get; }
        public RuleStore Store { get; }

        public RuleStoreFixture()
        {
            var services = new ServiceCollection();
            services.AddScoped<ProGuardDbContext>(_ => new TempDbContext(_dbPath));
            Provider = services.BuildServiceProvider();
            Store = new RuleStore(NullLogger<RuleStore>.Instance, Provider);
        }

        /// <summary>A fresh context on the same database, for asserting what actually hit the disk.</summary>
        public ProGuardDbContext NewContext() => new TempDbContext(_dbPath);

        public void Dispose()
        {
            Provider.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_dbPath); } catch { }
        }
    }

    // ---------- CpuLimiter ----------

    // Windows' hard CPU cap is enforced by the scheduler and takes a variable ~1-2s to converge after a job is
    // configured. These tests therefore assert the *property* ("the cap bites", "removal lets it run free")
    // over a few seconds of 1s windows instead of asserting how quickly it converges.
    // A free-running single-threaded spinner uses ~1.0 CPU-second per 1s window; a 1% cap on a many-core
    // machine allows far less than that.
    private const double FreeRunningWindow = 0.7; // generous lower bound for "running free"
    private const double CappedWindow = 0.5;      // an effective 1% cap is well below this

    [Fact]
    public void CpuLimiter_AddLimit_ActuallyThrottlesTheProcess()
    {
        using var spinner = StartSpinner();
        try
        {
            Thread.Sleep(1500); // let powershell finish starting up and begin spinning
            var before = Profile(spinner, 4);
            Assert.True(Windows(before).Max() > FreeRunningWindow, $"precondition: spinner should be busy before limiting {Show(before)}");

            var log = new ListLogger<CpuLimiter>();
            using var limiter = new CpuLimiter(log);
            limiter.AddLimit(spinner.Id, 1); // 1% of the whole machine

            var after = Profile(spinner, 8);
            Assert.True(Windows(after).Min() < CappedWindow,
                $"limit had no effect. Per-0.5s CPU before {Show(before)}, after AddLimit {Show(after)}. Limiter log: {log}");
        }
        finally { KillQuietly(spinner); }
    }

    [Fact]
    public void CpuLimiter_RemoveLimit_LiftsTheCap()
    {
        using var spinner = StartSpinner();
        try
        {
            Thread.Sleep(1500);
            var log = new ListLogger<CpuLimiter>();
            using var limiter = new CpuLimiter(log);
            limiter.AddLimit(spinner.Id, 1);

            var limited = Profile(spinner, 8);
            Assert.True(Windows(limited).Min() < CappedWindow, $"precondition: cap should bite {Show(limited)}. Limiter log: {log}");

            limiter.RemoveLimit(spinner.Id);

            var freed = Profile(spinner, 8);
            Assert.True(Windows(freed).Max() > FreeRunningWindow,
                $"RemoveLimit did not lift the cap. While limited {Show(limited)}, after removal {Show(freed)}. Limiter log: {log}");
        }
        finally { KillQuietly(spinner); }
    }

    [Fact]
    public async Task CpuLimiter_ReLimitingAndStopping_DoesNotLeakOrLeaveProcessCapped()
    {
        using var spinner = StartSpinner();
        try
        {
            Thread.Sleep(1500);
            var log = new ListLogger<CpuLimiter>();
            using var limiter = new CpuLimiter(log);

            limiter.AddLimit(spinner.Id, 1);
            limiter.AddLimit(spinner.Id, 2); // retunes the existing job rather than making a second one
            Assert.DoesNotContain("Failed", log.ToString());

            await limiter.StopAsync(CancellationToken.None); // engine shutdown must not leave a cap behind
            var freed = Profile(spinner, 8);
            Assert.True(Windows(freed).Max() > FreeRunningWindow,
                $"process was left capped after the limiter stopped {Show(freed)}. Limiter log: {log}");
        }
        finally { KillQuietly(spinner); }
    }

    // ---------- Name normalisation (UI stores "chrome.exe", Process.ProcessName is "chrome") ----------

    [Theory]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("Chrome.EXE", "chrome")]
    [InlineData("chrome", "chrome")]
    [InlineData("  chrome.exe ", "chrome")]
    [InlineData("System", "system")]
    [InlineData("", "")]
    public void ProcessNames_Normalize_StripsExeAndCase(string input, string expected)
        => Assert.Equal(expected, ProcessExtensions.NormalizeName(input));

    [Fact]
    public void RuleStore_RuleSavedWithExe_IsFoundByBareProcessName()
    {
        using var fx = new RuleStoreFixture();
        fx.Store.SaveProcessRule(new ProcessRule { ProcessName = "chrome.exe", CpuLimitPercent = 50 });

        Assert.NotNull(fx.Store.GetProcessRule("chrome"));
        Assert.NotNull(fx.Store.GetProcessRule("chrome.exe"));
        Assert.NotNull(fx.Store.GetProcessRule("CHROME"));
    }

    // ---------- RuleStore persistence ----------

    [Fact]
    public void RuleStore_SavingExistingRuleAsNewObject_UpdatesInsteadOfThrowing()
    {
        using var fx = new RuleStoreFixture();
        fx.Store.SaveProcessRule(new ProcessRule { ProcessName = "game.exe", CpuLimitPercent = 10 });

        // A fresh object (Id == 0) for the same process, as the UI/IPC layer would produce.
        var ex = Record.Exception(() =>
            fx.Store.SaveProcessRule(new ProcessRule { ProcessName = "game.exe", CpuLimitPercent = 75 }));

        Assert.Null(ex);
        Assert.Equal(75, fx.Store.GetProcessRule("game")!.CpuLimitPercent);

        using var db = fx.NewContext();
        var rows = db.ProcessRules.Where(r => r.ProcessName == "game.exe").ToList();
        Assert.Single(rows);
        Assert.Equal(75, rows[0].CpuLimitPercent);
    }

    [Fact]
    public void RuleStore_UpdateSettings_WithNewSettingsObject_Persists()
    {
        using var fx = new RuleStoreFixture();
        var ex = Record.Exception(() => fx.Store.UpdateSettings(new AppSettings { ProBalanceSensitivity = 9 }));

        Assert.Null(ex);
        Assert.Equal(9, fx.Store.GetSettings().ProBalanceSensitivity);

        // survives a reload
        var reloaded = new RuleStore(NullLogger<RuleStore>.Instance, fx.Provider);
        Assert.Equal(9, reloaded.GetSettings().ProBalanceSensitivity);
    }

    [Fact]
    public async Task RuleStore_ConcurrentWritersAndReaders_DoNotCorruptOrThrow()
    {
        using var fx = new RuleStoreFixture();
        var errors = new List<Exception>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { foreach (var r in fx.Store.GetWatchdogRules()) _ = r.Id; foreach (var p in fx.Store.GetPowerProfiles()) _ = p.Id; }
                catch (Exception e) { lock (errors) errors.Add(e); return; }
            }
        });

        var writer = Task.Run(() =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var rule = new WatchdogRule { TargetProcessName = $"p{i++}.exe", IsEnabled = true };
                    fx.Store.AddWatchdogRule(rule);
                    fx.Store.DeleteWatchdogRule(rule.Id);
                    var profile = new PowerProfile { ProcessName = $"pp{i}.exe" };
                    fx.Store.AddPowerProfile(profile);
                    fx.Store.DeletePowerProfile(profile.Id);
                }
                catch (Exception e) { lock (errors) errors.Add(e); return; }
            }
        });

        await Task.WhenAll(reader, writer);
        Assert.True(errors.Count == 0, errors.FirstOrDefault()?.ToString());
    }

    // ---------- WatchdogService ----------

    [Fact]
    public void Watchdog_RuleWithExeSuffix_MatchesProcessNameWithoutExe()
    {
        using var spinner = StartSpinner();
        try
        {
            var info = new ProcessInfo { Pid = spinner.Id, Name = "powershell", CpuPercent = 99 };
            var monitor = new Mock<IProcessMonitor>();
            monitor.Setup(m => m.GetAllProcesses()).Returns(new[] { info });

            var store = new Mock<IRuleStore>();
            store.Setup(s => s.GetWatchdogRules()).Returns(new[]
            {
                new WatchdogRule
                {
                    TargetProcessName = "powershell.exe", IsEnabled = true,
                    ConditionType = WatchdogCondition.CpuAbove, ThresholdValue = 10,
                    ActionType = WatchdogAction.SetPriority
                }
            });

            var wd = new WatchdogService(NullLogger<WatchdogService>.Instance, monitor.Object, store.Object);
            wd.EvaluateRules();

            spinner.Refresh();
            Assert.Equal(ProcessPriorityClass.Idle, spinner.PriorityClass);
        }
        finally { KillQuietly(spinner); }
    }

    [Fact]
    public void Watchdog_EvaluateRules_SwallowsStoreFailures()
    {
        // The evaluation runs on a timer thread; an escaped exception there would take down the whole service.
        var monitor = new Mock<IProcessMonitor>();
        var store = new Mock<IRuleStore>();
        store.Setup(s => s.GetWatchdogRules()).Throws(new InvalidOperationException("Collection was modified"));

        var wd = new WatchdogService(NullLogger<WatchdogService>.Instance, monitor.Object, store.Object);
        Assert.Null(Record.Exception(() => wd.EvaluateRules()));
    }

    // ---------- ProBalance ----------

    /// <summary>Spins up a hog and a ProBalance whose monitor reports it at 50% CPU on a machine pinned at 99%.</summary>
    private static (ProBalanceEngine Engine, Process Victim, Action MarkExited) MakeEngagedProBalance()
    {
        var victim = StartSpinner();
        Thread.Sleep(1000);
        bool exited = false;

        var monitor = new Mock<IProcessMonitor>();
        monitor.Setup(m => m.GetAllProcesses()).Returns(() => exited
            ? Array.Empty<ProcessInfo>()
            : new[] { new ProcessInfo { Pid = victim.Id, Name = "powershell", CpuPercent = 50, IsForeground = false } });
        monitor.Setup(m => m.GetProcess(It.IsAny<int>())).Returns((int pid) =>
            !exited && pid == victim.Id ? new ProcessInfo { Pid = pid, Name = "powershell" } : null);

        var store = new Mock<IRuleStore>();
        var engine = new ProBalanceEngine(NullLogger<ProBalanceEngine>.Instance, monitor.Object, store.Object, () => 99.0)
        {
            TimeOverQuotaBeforeRestraint = 0 // restrain on the first over-quota sample
        };
        return (engine, victim, () => exited = true);
    }

    [Fact]
    public void ProBalance_DisablingWhileEngaged_RestoresRestrainedProcesses()
    {
        var (engine, victim, _) = MakeEngagedProBalance();
        using var __ = engine;
        try
        {
            victim.Refresh();
            var original = victim.PriorityClass;

            engine.Evaluate(null);
            Assert.True(engine.IsEngaged, "precondition: hog should have been restrained");
            victim.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, victim.PriorityClass);

            engine.SetEnabled(false);

            victim.Refresh();
            Assert.Equal(original, victim.PriorityClass);
            Assert.False(engine.IsEngaged);
        }
        finally { KillQuietly(victim); }
    }

    [Fact]
    public void ProBalance_ExclusionWithExeSuffix_ProtectsProcessNameWithoutExe()
    {
        var (engine, victim, _) = MakeEngagedProBalance();
        using var __ = engine;
        try
        {
            engine.ExcludeProcess("powershell.exe");
            engine.Evaluate(null);
            Assert.False(engine.IsEngaged);
        }
        finally { KillQuietly(victim); }
    }

    [Theory]
    [InlineData("dwm")]
    [InlineData("audiodg")]
    [InlineData("csrss")]
    [InlineData("ProGuard.Engine")]
    public void ProBalance_NeverRestrainsProtectedSystemProcesses(string reportedName)
    {
        // A harmless stand-in process is reported under a protected name; it must be left alone.
        var victim = StartSpinner();
        try
        {
            Thread.Sleep(500);
            var monitor = new Mock<IProcessMonitor>();
            monitor.Setup(m => m.GetAllProcesses()).Returns(new[]
            {
                new ProcessInfo { Pid = victim.Id, Name = reportedName, CpuPercent = 90 }
            });
            using var engine = new ProBalanceEngine(NullLogger<ProBalanceEngine>.Instance,
                monitor.Object, new Mock<IRuleStore>().Object, () => 99.0)
            {
                TimeOverQuotaBeforeRestraint = 0
            };

            engine.Evaluate(null);

            Assert.False(engine.IsEngaged);
            victim.Refresh();
            Assert.Equal(ProcessPriorityClass.Normal, victim.PriorityClass);
        }
        finally { KillQuietly(victim); }
    }

    [Fact]
    public void ProBalance_ForgetsRestrainedProcessesThatExited()
    {
        var (engine, victim, markExited) = MakeEngagedProBalance();
        using var __ = engine;
        try
        {
            engine.Evaluate(null);
            Assert.Equal(1, engine.ActiveInterventions);

            KillQuietly(victim);
            markExited(); // the monitor no longer reports the process

            engine.Evaluate(null);
            Assert.Equal(0, engine.ActiveInterventions);
        }
        finally { KillQuietly(victim); }
    }
}
