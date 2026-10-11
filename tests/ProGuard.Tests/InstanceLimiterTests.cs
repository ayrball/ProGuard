using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Models;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

public class InstanceLimiterTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeProcessMonitor _monitor = new();
    private readonly FakeRuleProcessAccess _access = new();
    private readonly InstanceLimiter _limiter;

    public InstanceLimiterTests()
    {
        _limiter = new InstanceLimiter(NullLogger<InstanceLimiter>.Instance, _monitor, _access);
    }

    private ProcessInfo Add(int pid, int session, int startSecond, string name = "app")
    {
        _access.Session[pid] = session;
        _access.Start[pid] = T0.AddSeconds(startSecond);
        return _monitor.Add(pid, name);
    }

    private static ProcessRule Rule(int? max, bool allSessions = false) =>
        new() { ProcessName = "app.exe", MaxInstances = max, MaxInstancesAllSessions = allSessions };

    [Fact]
    public void OverTheLimit_TerminatesTheNewInstance()
    {
        Add(10, session: 1, startSecond: 0);
        Add(11, session: 1, startSecond: 1);
        var started = Add(12, session: 1, startSecond: 2);

        Assert.True(_limiter.Enforce(started, Rule(2)));
        Assert.Equal(new[] { 12 }, _access.Terminated);
    }

    [Fact]
    public void AtTheLimit_TerminatesNothing()
    {
        Add(10, session: 1, startSecond: 0);
        var started = Add(11, session: 1, startSecond: 1);

        Assert.False(_limiter.Enforce(started, Rule(2)));
        Assert.Empty(_access.Terminated);
    }

    [Fact]
    public void TerminatesTheNewestByStartTime_NotTheOneJustReported()
    {
        // At engine start the monitor reports existing instances in no particular order.
        Add(10, session: 1, startSecond: 0);
        Add(12, session: 1, startSecond: 5);
        var reported = Add(11, session: 1, startSecond: 3);

        Assert.False(_limiter.Enforce(reported, Rule(2)));
        Assert.Equal(new[] { 12 }, _access.Terminated);
    }

    [Fact]
    public void TerminatesAsManyAsNeeded_NewestFirst()
    {
        Add(10, session: 1, startSecond: 0);
        Add(11, session: 1, startSecond: 1);
        Add(12, session: 1, startSecond: 2);
        var started = Add(13, session: 1, startSecond: 3);

        Assert.True(_limiter.Enforce(started, Rule(1)));
        Assert.Equal(new[] { 13, 12, 11 }, _access.Terminated);
    }

    [Fact]
    public void PerSession_InstancesInOtherSessionsDontCount()
    {
        Add(10, session: 1, startSecond: 0);
        Add(11, session: 2, startSecond: 1);
        var started = Add(12, session: 2, startSecond: 2);

        Assert.False(_limiter.Enforce(Add(13, session: 3, startSecond: 3), Rule(1)));
        Assert.True(_limiter.Enforce(started, Rule(1)));
        Assert.Equal(new[] { 12 }, _access.Terminated); // 10 (session 1) and 13 (session 3) are alone in theirs
    }

    [Fact]
    public void AllSessions_CountsEverySession()
    {
        Add(10, session: 1, startSecond: 0);
        Add(11, session: 2, startSecond: 1);
        var started = Add(12, session: 3, startSecond: 2);

        Assert.True(_limiter.Enforce(started, Rule(2, allSessions: true)));
        Assert.Equal(new[] { 12 }, _access.Terminated);
    }

    [Fact]
    public void AllSessions_TerminatesTheNewestEvenInAnotherSession()
    {
        Add(10, session: 1, startSecond: 0);
        Add(11, session: 2, startSecond: 9);
        var reported = Add(12, session: 1, startSecond: 5);

        Assert.False(_limiter.Enforce(reported, Rule(2, allSessions: true)));
        Assert.Equal(new[] { 11 }, _access.Terminated);
    }

    [Fact]
    public void TerminatedInstancesStillListed_AreNotCountedAgain()
    {
        Add(10, session: 1, startSecond: 0);
        var second = Add(11, session: 1, startSecond: 1);
        Assert.True(_limiter.Enforce(second, Rule(1)));

        // 11 is terminated but the monitor hasn't noticed yet when 12 starts.
        var third = Add(12, session: 1, startSecond: 2);
        Assert.True(_limiter.Enforce(third, Rule(1)));
        Assert.Equal(new[] { 11, 12 }, _access.Terminated);

        _monitor.Exit(11);
        _limiter.OnProcessExited(11);
        _monitor.Exit(12);
        _limiter.OnProcessExited(12);
        Assert.False(_limiter.Enforce(_monitor.GetProcess(10)!, Rule(1)));
    }

    [Fact]
    public void CriticalProcesses_AreNeverTerminated()
    {
        Add(10, session: 1, startSecond: 0);
        var started = Add(11, session: 1, startSecond: 1);
        _access.Critical.Add(11);

        Assert.False(_limiter.Enforce(started, Rule(1)));
        Assert.Empty(_access.Terminated);
    }

    [Fact]
    public void CriticalProcessNames_AreIgnoredEntirely()
    {
        Add(10, session: 1, startSecond: 0, name: "csrss");
        var started = Add(11, session: 1, startSecond: 1, name: "csrss");

        Assert.False(_limiter.Enforce(started, new ProcessRule { ProcessName = "csrss.exe", MaxInstances = 1 }));
        Assert.Empty(_access.Terminated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoLimit_TerminatesNothing(int? max)
    {
        Add(10, session: 1, startSecond: 0);
        var started = Add(11, session: 1, startSecond: 1);

        Assert.False(_limiter.Enforce(started, Rule(max)));
        Assert.Empty(_access.Terminated);
    }

    [Fact]
    public void OtherProcessNames_AreNotCounted()
    {
        Add(10, session: 1, startSecond: 0, name: "other");
        var started = Add(11, session: 1, startSecond: 1);

        Assert.False(_limiter.Enforce(started, Rule(1)));
    }
}
