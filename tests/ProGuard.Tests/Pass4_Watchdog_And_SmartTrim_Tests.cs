using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

public class Pass4_Watchdog_And_SmartTrim_Tests
{
    [Fact]
    public void Watchdog_CRUD_DelegatesToRuleStore()
    {
        var mockLogger = new Mock<ILogger<WatchdogService>>();
        var mockMonitor = new Mock<IProcessMonitor>();
        var mockRuleStore = new Mock<IRuleStore>();

        using var watchdog = new WatchdogService(mockLogger.Object, mockMonitor.Object, mockRuleStore.Object);

        var rule = new WatchdogRule
        {
            Id = 42,
            TargetProcessName = "miner.exe",
            IsEnabled = true,
            ConditionType = WatchdogCondition.CpuAbove,
            ThresholdValue = 90.0,
            ActionType = WatchdogAction.Terminate
        };

        watchdog.AddRule(rule);
        mockRuleStore.Verify(s => s.AddWatchdogRule(rule), Times.Once);

        watchdog.UpdateRule(rule);
        mockRuleStore.Verify(s => s.UpdateWatchdogRule(rule), Times.Once);

        watchdog.RemoveRule(42);
        mockRuleStore.Verify(s => s.DeleteWatchdogRule(42), Times.Once);
    }

    [Fact]
    public void SmartTrim_ConfigurableThreshold_AcceptsCustomValues()
    {
        var mockLogger = new Mock<ILogger<SmartTrimService>>();
        var mockMonitor = new Mock<IProcessMonitor>();

        using var smartTrim = new SmartTrimService(mockLogger.Object, mockMonitor.Object);

        // Default should be 500 MB
        Assert.Equal(1024L * 1024L * 500L, smartTrim.ThresholdBytes);

        // Configure to 1 GB
        smartTrim.ThresholdBytes = 1024L * 1024L * 1024L;
        Assert.Equal(1024L * 1024L * 1024L, smartTrim.ThresholdBytes);
    }

    [Fact]
    public void SmartTrim_TrimProcess_OnCurrentProcess_DoesNotThrow()
    {
        var mockLogger = new Mock<ILogger<SmartTrimService>>();
        var mockMonitor = new Mock<IProcessMonitor>();

        using var smartTrim = new SmartTrimService(mockLogger.Object, mockMonitor.Object);
        using var currentProc = Process.GetCurrentProcess();

        // Should cleanly call EmptyWorkingSet on current process without throwing
        smartTrim.TrimProcess(currentProc.Id);
    }
}
