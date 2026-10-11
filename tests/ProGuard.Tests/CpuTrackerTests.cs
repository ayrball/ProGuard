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
