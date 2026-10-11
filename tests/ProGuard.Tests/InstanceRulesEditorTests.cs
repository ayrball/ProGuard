using System.Diagnostics;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>The rule editor's instance limit, Instance Balancer and startup boost inputs, and their persistence.</summary>
public class InstanceRulesEditorTests
{
    [Fact]
    public void EditorInputs_MapOntoTheRule()
    {
        var vm = new RulesViewModel
        {
            NewMaxInstances = " 3 ",
            NewMaxInstancesAllSessions = true,
            NewBalancerMode = "Fixed cores per instance",
            NewBalancerCoresPerInstance = "2",
            NewBalancerAvoidSmt = true,
            NewBalancerReservedCores = "1",
            NewBalancerMinCpu = "5%",
            NewStartupBoostPriority = "High",
            NewStartupBoostSeconds = "30",
        };
        var rule = new ProcessRule { ProcessName = "app.exe" };

        vm.ApplyInstanceAndBoostFields(rule);

        Assert.Equal(3, rule.MaxInstances);
        Assert.True(rule.MaxInstancesAllSessions);
        Assert.Equal(InstanceBalancerMode.FixedCoresPerInstance, rule.InstanceBalancerMode);
        Assert.Equal(2, rule.InstanceBalancerCoresPerInstance);
        Assert.True(rule.InstanceBalancerAvoidSmt);
        Assert.Equal(1, rule.InstanceBalancerReservedCores);
        Assert.Equal(5.0, rule.InstanceBalancerMinCpuPercent);
        Assert.Equal((int)ProcessPriorityClass.High, rule.StartupBoostPriorityClass);
        Assert.Equal(30, rule.StartupBoostSeconds);
    }

    [Fact]
    public void EditorDefaults_LeaveEverythingOff()
    {
        var rule = new ProcessRule { ProcessName = "app.exe" };

        new RulesViewModel().ApplyInstanceAndBoostFields(rule);

        Assert.Null(rule.MaxInstances);
        Assert.Null(rule.InstanceBalancerMode);
        Assert.Null(rule.StartupBoostPriorityClass);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("99999", 3600)]
    public void BoostSeconds_AreClamped(string input, int expected)
    {
        var rule = new ProcessRule { ProcessName = "app.exe" };

        new RulesViewModel { NewStartupBoostSeconds = input }.ApplyInstanceAndBoostFields(rule);

        Assert.Equal(expected, rule.StartupBoostSeconds);
    }

    [Fact]
    public void NewFields_RoundTripThroughTheDatabase()
    {
        using (var db = new ProGuardDbContext())
        {
            db.Database.EnsureCreated();
            db.ProcessRules.Add(new ProcessRule
            {
                ProcessName = "roundtrip_instances.exe",
                MaxInstances = 2,
                MaxInstancesAllSessions = true,
                InstanceBalancerMode = InstanceBalancerMode.EqualCoresPerInstance,
                InstanceBalancerAvoidSmt = true,
                InstanceBalancerMinCpuPercent = 7.5,
                StartupBoostPriorityClass = (int)ProcessPriorityClass.AboveNormal,
                StartupBoostSeconds = 45,
            });
            db.SaveChanges();
        }

        using (var db = new ProGuardDbContext())
        {
            var rule = db.ProcessRules.Single(r => r.ProcessName == "roundtrip_instances.exe");
            Assert.Equal(2, rule.MaxInstances);
            Assert.True(rule.MaxInstancesAllSessions);
            Assert.Equal(InstanceBalancerMode.EqualCoresPerInstance, rule.InstanceBalancerMode);
            Assert.True(rule.InstanceBalancerAvoidSmt);
            Assert.Equal(7.5, rule.InstanceBalancerMinCpuPercent);
            Assert.Equal((int)ProcessPriorityClass.AboveNormal, rule.StartupBoostPriorityClass);
            Assert.Equal(45, rule.StartupBoostSeconds);
            db.ProcessRules.Remove(rule);
            db.SaveChanges();
        }
    }
}
