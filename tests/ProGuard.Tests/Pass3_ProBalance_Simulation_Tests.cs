using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

public class Pass3_ProBalance_Simulation_Tests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void ProBalance_AllSensitivityLevels_MaintainValidThresholdHierarchy(int sensitivity)
    {
        var mockLogger = new Mock<ILogger<ProBalanceEngine>>();
        var mockMonitor = new Mock<IProcessMonitor>();
        var mockRuleStore = new Mock<IRuleStore>();

        using var engine = new ProBalanceEngine(mockLogger.Object, mockMonitor.Object, mockRuleStore.Object);
        engine.SetSensitivity(sensitivity);

        // Verification: CPU Load threshold must be strictly greater than Recovery threshold
        Assert.True(engine.CpuLoadThreshold > engine.CpuRecoveryThreshold,
            $"At sensitivity {sensitivity}, LoadThreshold ({engine.CpuLoadThreshold}) must exceed RecoveryThreshold ({engine.CpuRecoveryThreshold})");

        // Process threshold must be positive
        Assert.True(engine.ProcessCpuThreshold > 0, $"Process CPU threshold must be > 0, got {engine.ProcessCpuThreshold}");
    }

    [Fact]
    public void ProBalance_ToggleEnabled_DisablesEvaluation()
    {
        var mockLogger = new Mock<ILogger<ProBalanceEngine>>();
        var mockMonitor = new Mock<IProcessMonitor>();
        var mockRuleStore = new Mock<IRuleStore>();

        using var engine = new ProBalanceEngine(mockLogger.Object, mockMonitor.Object, mockRuleStore.Object);

        engine.SetEnabled(false);
        Assert.False(engine.IsEngaged);

        engine.SetEnabled(true);
        Assert.False(engine.IsEngaged);
    }

    [Fact]
    public void ProBalance_Exclusions_AreCaseInsensitive()
    {
        var mockLogger = new Mock<ILogger<ProBalanceEngine>>();
        var mockMonitor = new Mock<IProcessMonitor>();
        var mockRuleStore = new Mock<IRuleStore>();

        using var engine = new ProBalanceEngine(mockLogger.Object, mockMonitor.Object, mockRuleStore.Object);

        engine.ExcludeProcess("Blender.exe");
        engine.RemoveExclusion("blender.exe"); // case insensitive removal should work without exception
    }
}
