using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

/// <summary>
/// The CPU limiter's hard cap, measured on a real busy-loop child. These measure how much CPU the child gets, so they
/// run alone (<see cref="RunsAlone"/>): alongside the rest of the suite on a 2-CPU CI runner the child got a few
/// percent of a CPU with no cap at all, and the child itself takes a whole CPU from the tests around it.
/// </summary>
[Collection(RunsAlone.Name)]
public class CpuLimiterRealProcessTests
{
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
        using var spinner = Review_Regression_Tests.StartSpinner();
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
        finally { Review_Regression_Tests.KillQuietly(spinner); }
    }

    [Fact]
    public void CpuLimiter_RemoveLimit_LiftsTheCap()
    {
        using var spinner = Review_Regression_Tests.StartSpinner();
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
        finally { Review_Regression_Tests.KillQuietly(spinner); }
    }

    [Fact]
    public async Task CpuLimiter_ReLimitingAndStopping_DoesNotLeakOrLeaveProcessCapped()
    {
        using var spinner = Review_Regression_Tests.StartSpinner();
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
        finally { Review_Regression_Tests.KillQuietly(spinner); }
    }

    // ---------- helpers ----------

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
}
