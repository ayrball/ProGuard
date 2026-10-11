using System.Diagnostics;
using ProGuard.Core.Helpers;

namespace ProGuard.Tests;

public class CpuTrackerTests
{
    [Fact]
    public void GetCpuUsage_FirstCall_ReturnsZero()
    {
        var tracker = new CpuUsageTracker();
        using var current = Process.GetCurrentProcess();

        var usage = tracker.GetCpuUsage(current);

        Assert.Equal(0, usage);
    }

    [Fact]
    public void FirstReading_OfAJustStartedProcess_IsItsUsageSinceStart()
    {
        var tracker = new CpuUsageTracker();
        var started = DateTime.UtcNow.AddSeconds(-1);

        // One second old and it has used two seconds of CPU: two cores' worth.
        var usage = tracker.GetCpuUsage(42, TimeSpan.FromSeconds(2), started);

        Assert.InRange(usage, 2 * 100.0 / Environment.ProcessorCount * 0.9, 2 * 100.0 / Environment.ProcessorCount * 1.1);
    }

    [Fact]
    public void FirstReading_OfAnOlderProcess_IsStillZero()
    {
        var tracker = new CpuUsageTracker();
        var started = DateTime.UtcNow - CpuUsageTracker.LifetimeSeedWindow - TimeSpan.FromSeconds(1);

        Assert.Equal(0, tracker.GetCpuUsage(42, TimeSpan.FromSeconds(30), started));
        Assert.Equal(0, new CpuUsageTracker().GetCpuUsage(42, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void RemoveProcess_RemovesFromInternalSnapshot()
    {
        var tracker = new CpuUsageTracker();
        using var current = Process.GetCurrentProcess();

        tracker.GetCpuUsage(current);
        tracker.RemoveProcess(current.Id);

        // Next call should again return 0 because process snapshot was removed
        var usageAfterRemoval = tracker.GetCpuUsage(current);
        Assert.Equal(0, usageAfterRemoval);
    }
}
