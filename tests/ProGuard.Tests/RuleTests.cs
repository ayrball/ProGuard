using System.Text.Json;
using ProGuard.Core.Models;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

public class RuleTests
{
    [Fact]
    public void ProcessRule_DefaultsAndProperties_SetCorrectly()
    {
        var rule = new ProcessRule
        {
            ProcessName = "obs64.exe",
            PriorityClass = (int)System.Diagnostics.ProcessPriorityClass.High,
            AffinityMask = 0b1111,
            CpuLimitPercent = 50.0,
            MaxInstances = 2,
            ExcludeFromProBalance = true,
            DisallowExecution = false
        };

        Assert.Equal("obs64.exe", rule.ProcessName);
        Assert.Equal((int)System.Diagnostics.ProcessPriorityClass.High, rule.PriorityClass);
        Assert.Equal(0b1111, rule.AffinityMask);
        Assert.Equal(50.0, rule.CpuLimitPercent);
        Assert.True(rule.ExcludeFromProBalance);
        Assert.False(rule.DisallowExecution);
    }

    [Fact]
    public void WatchdogRule_EnumsAndConditions_BehaveExpectedly()
    {
        var rule = new WatchdogRule
        {
            TargetProcessName = "leak.exe",
            IsEnabled = true,
            ConditionType = WatchdogCondition.RamAbove,
            ThresholdValue = 1024.0, // 1GB
            DurationSeconds = 30,
            ActionType = WatchdogAction.Terminate
        };

        Assert.True(rule.IsEnabled);
        Assert.Equal(WatchdogCondition.RamAbove, rule.ConditionType);
        Assert.Equal(WatchdogAction.Terminate, rule.ActionType);
    }

    [Fact]
    public void IpcMessage_Serialization_RoundTripsAccurately()
    {
        var command = new CommandMessage
        {
            Type = MessageType.Command,
            CommandType = "SetPriority",
            TargetPid = 1234,
            Argument = "High"
        };

        var json = JsonSerializer.Serialize(command);
        var deserialized = JsonSerializer.Deserialize<CommandMessage>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageType.Command, deserialized.Type);
        Assert.Equal("SetPriority", deserialized.CommandType);
        Assert.Equal(1234, deserialized.TargetPid);
        Assert.Equal("High", deserialized.Argument);
    }

    [Fact]
    public void ProcessSnapshot_DtoMapping_Works()
    {
        var snapshot = new ProcessSnapshot
        {
            Pid = 456,
            Name = "explorer.exe",
            CpuPercent = 3.5,
            WorkingSetBytes = 1024 * 1024 * 80,
            PriorityClass = (int)System.Diagnostics.ProcessPriorityClass.Normal,
            AffinityMask = 255,
            IsForeground = true,
            Status = "Running"
        };

        Assert.Equal(456, snapshot.Pid);
        Assert.Equal("explorer.exe", snapshot.Name);
        Assert.Equal(3.5, snapshot.CpuPercent);
        Assert.True(snapshot.IsForeground);
    }

    [Fact]
    public void TestSymbols()
    {
        Assert.True(Enum.IsDefined(typeof(Wpf.Ui.Controls.SymbolRegular), "DeveloperBoard24"));
    }
}
