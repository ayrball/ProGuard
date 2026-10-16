using ProGuard.Core.Helpers;

namespace ProGuard.Tests;

/// <summary>The PDH processor counters behind the dashboard: Task Manager's CPU %, per-core load and responsiveness.</summary>
public class ProcessorMetricsTests
{
    /// <summary>Rate counters need a second collection some time after the first; wait for one, but not forever.</summary>
    private static ProcessorSample CollectUntilCpuKnown(ProcessorCounters counters)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            Thread.Sleep(250);
            var sample = counters.Collect();
            if (sample.UtilityPercent != null && sample.PerCorePercent.Count > 0) return sample;
            Assert.True(DateTime.UtcNow < deadline, "PDH returned no CPU figures within 10 s");
        }
    }

    [Fact]
    public void Counters_ReadUtilityPerCoreLoadAndQueueLength()
    {
        using var counters = ProcessorCounters.TryOpen();
        Assert.NotNull(counters);

        var sample = CollectUntilCpuKnown(counters);

        Assert.InRange(sample.UtilityPercent!.Value, 0, 1000);   // uncapped: passes 100 % under turbo
        Assert.Equal(Environment.ProcessorCount, sample.PerCorePercent.Count);
        Assert.All(sample.PerCorePercent, p => Assert.InRange(p, 0, 100));
        Assert.NotNull(sample.QueueLength);
        Assert.True(sample.QueueLength >= 0);
        // Parking Status: one per logical CPU where Windows has the counter (a VM may not).
        Assert.True(sample.Parked.Count == 0 || sample.Parked.Count == Environment.ProcessorCount, $"{sample.Parked.Count} parking states for {Environment.ProcessorCount} CPUs");
    }

    [Fact]
    public void Counters_AfterDispose_ReadNothing()
    {
        var counters = ProcessorCounters.TryOpen();
        Assert.NotNull(counters);
        counters.Dispose();

        Assert.Same(ProcessorSample.Empty, counters.Collect());
        counters.Dispose();
    }

    [Theory]
    [InlineData("0,0", true, 0, 0)]
    [InlineData("0,15", true, 0, 15)]
    [InlineData("1,3", true, 1, 3)]
    [InlineData("0,_Total", false, 0, 0)]
    [InlineData("_Total", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    [InlineData(null, false, 0, 0)]
    public void CoreInstanceNames_AreGroupAndIndex(string? name, bool isCore, int group, int index)
    {
        Assert.Equal(isCore, ProcessorCounters.TryParseCoreInstance(name, out var g, out var i));
        if (!isCore) return;
        Assert.Equal(group, g);
        Assert.Equal(index, i);
    }

    [Theory]
    [InlineData(42.36, 42.4)]
    [InlineData(123.4, 100.0)]   // Task Manager caps the turbo overshoot
    [InlineData(0.0, 0.0)]
    public void TaskManagerCpuPercent_RoundsAndCapsUtility(double utility, double expected)
    {
        Assert.Equal(expected, SystemInfo.TaskManagerCpuPercent(utility));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TaskManagerCpuPercent_IsNullWithoutAReading(double? utility)
    {
        Assert.Null(SystemInfo.TaskManagerCpuPercent(utility));
    }

    [Fact]
    public void OverallCpuPercent_IsAPercentage_AndProcessorTimeStaysAvailable()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.InRange(SystemInfo.GetOverallCpuPercent(), 0, 100);
            Assert.InRange(SystemInfo.GetProcessorTimePercent(), 0, 100);
            Thread.Sleep(300);
        }
    }

    [Theory]
    [InlineData(0.0, 8, 100.0)]
    [InlineData(4.0, 8, 75.0)]
    [InlineData(8.0, 8, 50.0)]
    [InlineData(16.0, 8, 0.0)]    // two waiting per CPU: saturated
    [InlineData(50.0, 8, 0.0)]
    [InlineData(1.0, 1, 50.0)]
    [InlineData(-3.0, 4, 100.0)]
    public void Responsiveness_FallsLinearlyToZeroAtTwoWaitingThreadsPerCpu(double queue, int cpus, double expected)
    {
        Assert.Equal(expected, ResponsivenessMeter.FromQueueLength(queue, cpus), precision: 6);
    }

    [Fact]
    public void ResponsivenessMeter_AveragesTheLastFiveReadings()
    {
        var meter = new ResponsivenessMeter(logicalProcessors: 4);
        Assert.Null(meter.Percent);
        Assert.Null(meter.AverageQueueLength);

        meter.Add(8);   // saturated on its own
        Assert.Equal(0, meter.Percent);

        for (var i = 0; i < 4; i++) meter.Add(0);
        Assert.Equal(1.6, meter.AverageQueueLength!.Value, precision: 6);
        Assert.Equal(80, meter.Percent!.Value, precision: 6);

        meter.Add(0);   // the 8 drops out of the window
        Assert.Equal(0, meter.AverageQueueLength);
        Assert.Equal(100, meter.Percent);
    }
}
