using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;
using Xunit.Abstractions;

namespace ProGuard.Tests;

/// <summary>
/// The snapshot against this machine's real processes, with the real process monitor and Windows readers. It keeps the
/// "cheap with hundreds of processes" requirement honest and prints the timings. It runs alone (<see cref="RunsAlone"/>):
/// on a 2-CPU CI runner, sharing the CPUs with the rest of the suite made steady-state builds take half a second.
/// </summary>
[Collection(RunsAlone.Name)]
public class SnapshotPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public SnapshotPerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RealSnapshot_IsCheapToBuild()
    {
        using var monitor = new ProcessMonitor(NullLogger<ProcessMonitor>.Instance);
        var started = Stopwatch.StartNew();
        await monitor.StartAsync(CancellationToken.None);
        var firstPoll = started.Elapsed;
        await Task.Delay(1500);

        var refresh = typeof(ProcessMonitor).GetMethod("RefreshProcesses", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var polls = Enumerable.Range(0, 5).Select(_ =>
        {
            var sw = Stopwatch.StartNew();
            refresh.Invoke(monitor, null);
            return sw.Elapsed.TotalMilliseconds;
        }).ToList();

        var store = new Mock<IRuleStore>();
        store.Setup(s => s.GetProcessRules()).Returns(new[] { new ProcessRule { ProcessName = "chrome.exe" } });
        var power = new Mock<IPowerManager>();
        power.Setup(p => p.GetAvailablePlans()).Returns(ProGuard.Core.Helpers.PowerPlans.Enumerate().Select(p => (p.Id, p.Name)).ToList());
        power.Setup(p => p.GetActivePlan()).Returns(ProGuard.Core.Native.Powrprof.Balanced);
        var now = 0L;
        var builder = new SnapshotBuilder(monitor, new Mock<IProBalanceEngine>().Object, new CpuLimiter(NullLogger<CpuLimiter>.Instance),
            store.Object, power.Object, new Win32ProcessDetailsReader(), clockMs: () => now);

        var builds = new double[6];
        for (var i = 0; i < builds.Length; i++)
        {
            now += 2000;
            builder.GetShared();
            builds[i] = builder.LastBuildDuration.TotalMilliseconds;
            await Task.Delay(300);
        }

        var count = builder.GetShared().Processes.Count;
        _output.WriteLine($"{count} processes");
        _output.WriteLine($"monitor first poll (incl. WMI watchers): {firstPoll.TotalMilliseconds:0.0} ms; later polls: {string.Join(", ", polls.Select(p => p.ToString("0.0")))} ms");
        _output.WriteLine($"snapshot builds: first {builds[0]:0.0} ms (looks up every account), then {string.Join(", ", builds.Skip(1).Select(b => b.ToString("0.0")))} ms");

        Assert.True(count > 20);
        Assert.True(builds.Skip(1).Max() < 250, "a steady-state build should take a few milliseconds");
        Assert.True(builds[0] < 1500);
        await monitor.StopAsync(CancellationToken.None);
    }
}
