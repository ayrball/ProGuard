using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Models;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

public class StartupBoostTests
{
    private readonly FakeRuleProcessAccess _access = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly StartupBoostService _boost;

    public StartupBoostTests()
    {
        _boost = new StartupBoostService(NullLogger<StartupBoostService>.Instance, _access, _clock);
    }

    private ProcessInfo Process(int pid, ProcessPriorityClass priority, double startedSecondsAgo = 0)
    {
        _access.Priority[pid] = priority;
        _access.Start[pid] = _clock.Now.UtcDateTime.AddSeconds(-startedSecondsAgo);
        return new ProcessInfo { Pid = pid, Name = "app" };
    }

    private static ProcessRule Rule(ProcessPriorityClass? boost, int? seconds) =>
        new() { ProcessName = "app.exe", StartupBoostPriorityClass = (int?)boost, StartupBoostSeconds = seconds };

    [Fact]
    public void BoostsThenRestoresWhenTheTimeIsUp()
    {
        var p = Process(1, ProcessPriorityClass.BelowNormal);

        Assert.True(_boost.TryBoost(p, Rule(ProcessPriorityClass.High, 5)));
        Assert.Equal(ProcessPriorityClass.High, _access.Priority[1]);

        _clock.Advance(TimeSpan.FromSeconds(4.9));
        _boost.Tick();
        Assert.Equal(ProcessPriorityClass.High, _access.Priority[1]);

        _clock.Advance(TimeSpan.FromSeconds(0.1));
        _boost.Tick();
        Assert.Equal(ProcessPriorityClass.BelowNormal, _access.Priority[1]);
        Assert.Equal(0, _boost.ActiveBoostCount);
    }

    [Fact]
    public void PriorityChangedDuringTheBoost_IsLeftAlone()
    {
        var p = Process(1, ProcessPriorityClass.Normal);
        _boost.TryBoost(p, Rule(ProcessPriorityClass.High, 5));

        _access.Priority[1] = ProcessPriorityClass.AboveNormal; // the user or another tool
        _clock.Advance(TimeSpan.FromSeconds(6));
        _boost.Tick();

        Assert.Equal(ProcessPriorityClass.AboveNormal, _access.Priority[1]);
        Assert.Equal(0, _boost.ActiveBoostCount);
    }

    [Fact]
    public void TheWindowStartsWhenTheProcessStarted()
    {
        var p = Process(1, ProcessPriorityClass.Idle, startedSecondsAgo: 3);
        _boost.TryBoost(p, Rule(ProcessPriorityClass.AboveNormal, 5));

        _clock.Advance(TimeSpan.FromSeconds(2));
        _boost.Tick();

        Assert.Equal(ProcessPriorityClass.Idle, _access.Priority[1]);
    }

    [Fact]
    public void ProcessesPastTheirWindow_AreNotBoosted()
    {
        // e.g. already running when the engine started
        var p = Process(1, ProcessPriorityClass.Normal, startedSecondsAgo: 60);

        Assert.False(_boost.TryBoost(p, Rule(ProcessPriorityClass.High, 5)));
        Assert.Equal(ProcessPriorityClass.Normal, _access.Priority[1]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(5000, 3600)]
    [InlineData(null, StartupBoostService.DefaultSeconds)]
    public void DurationIsClampedTo1To3600Seconds(int? seconds, int effective)
    {
        var p = Process(1, ProcessPriorityClass.BelowNormal);
        _boost.TryBoost(p, Rule(ProcessPriorityClass.High, seconds));

        _clock.Advance(TimeSpan.FromSeconds(effective) - TimeSpan.FromMilliseconds(1));
        _boost.Tick();
        Assert.Equal(ProcessPriorityClass.High, _access.Priority[1]);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        _boost.Tick();
        Assert.Equal(ProcessPriorityClass.BelowNormal, _access.Priority[1]);
    }

    [Fact]
    public void NoBoostPriority_DoesNothing()
    {
        var p = Process(1, ProcessPriorityClass.Normal);
        Assert.False(_boost.TryBoost(p, Rule(null, 5)));
        Assert.Equal(0, _boost.ActiveBoostCount);
    }

    [Fact]
    public void AlreadyAtTheBoostPriority_NothingToRestore()
    {
        var p = Process(1, ProcessPriorityClass.High);
        Assert.False(_boost.TryBoost(p, Rule(ProcessPriorityClass.High, 5)));
        Assert.Equal(0, _boost.ActiveBoostCount);
    }

    [Fact]
    public void ExitedProcess_IsForgotten()
    {
        var p = Process(1, ProcessPriorityClass.Normal);
        _boost.TryBoost(p, Rule(ProcessPriorityClass.High, 5));

        _boost.OnProcessExited(1);

        Assert.Equal(0, _boost.ActiveBoostCount);
    }

    [Fact]
    public void Stop_EndsActiveBoostsEarly()
    {
        var p = Process(1, ProcessPriorityClass.BelowNormal);
        _boost.TryBoost(p, Rule(ProcessPriorityClass.High, 60));

        _boost.Stop();

        Assert.Equal(ProcessPriorityClass.BelowNormal, _access.Priority[1]);
    }

    [Fact]
    public void BoostsEndIndependently()
    {
        _boost.TryBoost(Process(1, ProcessPriorityClass.Normal), Rule(ProcessPriorityClass.High, 5));
        _clock.Advance(TimeSpan.FromSeconds(3));
        _boost.TryBoost(Process(2, ProcessPriorityClass.Idle), Rule(ProcessPriorityClass.AboveNormal, 5));

        _clock.Advance(TimeSpan.FromSeconds(2));
        _boost.Tick();
        Assert.Equal(ProcessPriorityClass.Normal, _access.Priority[1]);
        Assert.Equal(ProcessPriorityClass.AboveNormal, _access.Priority[2]);

        _clock.Advance(TimeSpan.FromSeconds(3));
        _boost.Tick();
        Assert.Equal(ProcessPriorityClass.Idle, _access.Priority[2]);
    }
}
