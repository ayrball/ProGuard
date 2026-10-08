using Microsoft.Extensions.Logging;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

public class ProBalanceTests
{
    [Theory]
    [InlineData(1, 93.0, 23.0, 73.0)]
    [InlineData(5, 65.0, 15.0, 45.0)]
    [InlineData(10, 30.0, 5.0, 10.0)]
    public void SetSensitivity_ScalesThresholdsAppropriately(int level, double expectedCpuLoad, double expectedProcCpu, double expectedRecovery)
    {
        var mockLogger = new Mock<ILogger<ProBalanceEngine>>();
        var mockMonitor = new Mock<IProcessMonitor>();
        var mockRuleStore = new Mock<IRuleStore>();

        using var engine = new ProBalanceEngine(mockLogger.Object, mockMonitor.Object, mockRuleStore.Object);

        engine.SetSensitivity(level);

        Assert.Equal(expectedCpuLoad, engine.CpuLoadThreshold);
        Assert.Equal(expectedProcCpu, engine.ProcessCpuThreshold);
        Assert.Equal(expectedRecovery, engine.CpuRecoveryThreshold);
    }

    [Fact]
    public void Engine_StartsNotEngaged()
    {
        var mockLogger = new Mock<ILogger<ProBalanceEngine>>();
        var mockMonitor = new Mock<IProcessMonitor>();
        var mockRuleStore = new Mock<IRuleStore>();

        using var engine = new ProBalanceEngine(mockLogger.Object, mockMonitor.Object, mockRuleStore.Object);

        Assert.False(engine.IsEngaged);
        Assert.Equal(0, engine.ActiveInterventions);
    }

    [Fact]
    public void ExcludeProcess_AddsToExclusionList()
    {
        var mockLogger = new Mock<ILogger<ProBalanceEngine>>();
        var mockMonitor = new Mock<IProcessMonitor>();
        var mockRuleStore = new Mock<IRuleStore>();

        using var engine = new ProBalanceEngine(mockLogger.Object, mockMonitor.Object, mockRuleStore.Object);

        engine.ExcludeProcess("game.exe");
        // Remove should succeed without error
        engine.RemoveExclusion("game.exe");
    }
}
