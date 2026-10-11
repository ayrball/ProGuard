using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

/// <summary>
/// The Win32 side of the instance limit / Instance Balancer / startup boost, against real processes. Uses an idle
/// ping.exe as the stand-in, and sets its priority explicitly: Process Lasso runs on the dev machine and lowers
/// busy processes, so nothing here may assume a process starts at Normal.
/// </summary>
public sealed class InstanceRulesRealProcessTests : IDisposable
{
    private readonly List<Process> _spawned = new();

    private Process Spawn(ProcessPriorityClass priority = ProcessPriorityClass.BelowNormal)
    {
        var p = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        _spawned.Add(p);
        p.PriorityClass = priority;
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _spawned)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
            p.Dispose();
        }
    }

    [Fact]
    public void Win32Access_ReadsAndWritesARealProcess()
    {
        var access = new Win32RuleProcessAccess();
        var p = Spawn(ProcessPriorityClass.BelowNormal);

        Assert.Equal(Process.GetCurrentProcess().SessionId, access.GetSessionId(p.Id));
        Assert.NotNull(access.GetStartTimeUtc(p.Id));
        Assert.False(access.IsCritical(p.Id));
        Assert.True(access.IsCritical(4)); // System

        Assert.Equal(ProcessPriorityClass.BelowNormal, access.GetPriorityClass(p.Id));
        Assert.True(access.SetPriorityClass(p.Id, ProcessPriorityClass.Idle));
        Assert.Equal(ProcessPriorityClass.Idle, access.GetPriorityClass(p.Id));

        Assert.True(access.SetAffinityMask(p.Id, 1));
        Assert.Equal(1, access.GetAffinityMask(p.Id));

        Assert.True(access.Terminate(p.Id));
        Assert.True(p.WaitForExit(5000));
        Assert.Null(access.GetPriorityClass(p.Id));
    }

    [Fact]
    public void Governor_AppliesRulePriority_ThenBoosts_ThenRestoresTheRulePriority()
    {
        var p = Spawn(ProcessPriorityClass.Idle);
        var monitor = new FakeProcessMonitor();
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        var boost = new StartupBoostService(NullLogger<StartupBoostService>.Instance, new Win32RuleProcessAccess(), clock);
        var rule = new ProcessRule
        {
            ProcessName = "ping.exe",
            PriorityClass = (int)ProcessPriorityClass.BelowNormal,
            StartupBoostPriorityClass = (int)ProcessPriorityClass.AboveNormal,
            StartupBoostSeconds = 30,
        };
        var governor = NewGovernor(monitor, rule, startupBoost: boost);

        monitor.Start(p.Id, "PING");
        p.Refresh();
        Assert.Equal(ProcessPriorityClass.AboveNormal, p.PriorityClass);

        clock.Advance(TimeSpan.FromSeconds(31));
        boost.Tick();
        p.Refresh();
        Assert.Equal(ProcessPriorityClass.BelowNormal, p.PriorityClass);
        governor.Stop();
    }

    [Fact]
    public void Governor_TerminatesTheNewestInstanceOverTheLimit()
    {
        var older = Spawn();
        Thread.Sleep(50); // distinct start times
        var newer = Spawn();
        var monitor = new FakeProcessMonitor();
        var limiter = new InstanceLimiter(NullLogger<InstanceLimiter>.Instance, monitor, new Win32RuleProcessAccess());
        var rule = new ProcessRule { ProcessName = "ping.exe", MaxInstances = 1 };
        var governor = NewGovernor(monitor, rule, instanceLimiter: limiter);

        monitor.Start(older.Id, "PING");
        monitor.Start(newer.Id, "PING");

        Assert.True(newer.WaitForExit(5000));
        Assert.False(older.HasExited);
        governor.Stop();
    }

    private static ProcessGovernor NewGovernor(FakeProcessMonitor monitor, ProcessRule rule,
        InstanceLimiter? instanceLimiter = null, StartupBoostService? startupBoost = null)
    {
        var store = FakeRules.Store(new List<ProcessRule> { rule });
        return new ProcessGovernor(NullLogger<ProcessGovernor>.Instance, monitor,
            Mock.Of<IProBalanceEngine>(), Mock.Of<ICpuLimiter>(), Mock.Of<ISmartTrimService>(),
            Mock.Of<IPowerManager>(), Mock.Of<IWatchdogService>(), store.Object,
            instanceLimiter, startupBoost: startupBoost);
    }
}

public class InstanceRulesCompositionTests
{
    [Fact]
    public void EngineHost_ResolvesTheGovernorWithTheNewServices()
    {
        using var host = ProGuard.Engine.Program.CreateHostBuilder(Array.Empty<string>()).Build();
        var services = host.Services;

        var governor = services.GetService(typeof(ProcessGovernor));
        Assert.NotNull(governor);
        // Optional constructor parameters: make sure DI filled them rather than leaving the defaults.
        foreach (var field in new[] { "_instanceLimiter", "_instanceBalancer", "_startupBoost" })
            Assert.NotNull(typeof(ProcessGovernor).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(governor));
        Assert.NotNull(services.GetService(typeof(InstanceLimiter)));
        Assert.NotNull(services.GetService(typeof(InstanceBalancer)));
        Assert.NotNull(services.GetService(typeof(StartupBoostService)));
    }
}
