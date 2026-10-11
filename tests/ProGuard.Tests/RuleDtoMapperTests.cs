using System.Linq;
using ProGuard.Core.Models;
using ProGuard.Engine.IPC;
using ProGuard.Shared.Contracts;

namespace ProGuard.Tests;

public class RuleDtoMapperTests
{
    [Fact]
    public void RoundTrip_KeepsEveryFieldTheDtoCarries()
    {
        var rule = new ProcessRule
        {
            ProcessName = "game.exe",
            IoPriority = 1,
            MemoryPriority = 3,
            EfficiencyMode = false,
            DisablePriorityBoost = true,
            CpuSetIds = "256,257",
            GpuPriority = 3,
            GpuPreference = 2,
            ReapplyIfChanged = true,
            CpuLimitThresholdPercent = 40,
            CpuLimitOverThresholdSeconds = 5,
            CpuLimitCoresToRemove = 2,
            CpuLimitDurationSeconds = 30,
            MaxInstances = 2,
            MaxInstancesAllSessions = true,
            InstanceBalancerMode = InstanceBalancerMode.FixedCoresPerInstance,
            InstanceBalancerCoresPerInstance = 4,
            InstanceBalancerAvoidSmt = true,
            InstanceBalancerReservedCores = 1,
            InstanceBalancerMinCpuPercent = 3,
            StartupBoostPriorityClass = 0x80,
            StartupBoostSeconds = 20,
        };

        var copy = new ProcessRule();
        RuleDtoMapper.CopyTo(RuleDtoMapper.ToDto(rule), copy);

        // Every property RuleDto declares (other than its own Id/Type) must survive the trip.
        var carried = typeof(RuleDto).GetProperties().Select(p => p.Name)
            .Where(n => n is not ("Id" or "Type" or "Name"));
        foreach (var name in carried)
        {
            var property = typeof(ProcessRule).GetProperty(name);
            Assert.True(property != null, $"RuleDto.{name} has no ProcessRule counterpart");
            Assert.Equal(property!.GetValue(rule), property.GetValue(copy));
        }
        Assert.Equal(rule.ProcessName, copy.ProcessName);
    }

    [Fact]
    public void UnknownBalancerMode_IsStoredAsOff()
    {
        var copy = new ProcessRule();
        RuleDtoMapper.CopyTo(new RuleDto { Name = "x.exe", InstanceBalancerMode = 99 }, copy);
        Assert.Null(copy.InstanceBalancerMode);
    }
}
