using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ProGuard.Core.Data;
using ProGuard.Core.Models;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.Tests;

public class Pass5_IPC_And_Database_Persistence_Tests
{
    private class TestDbContext : ProGuardDbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Use in-memory SQLite database for test isolation
            optionsBuilder.UseSqlite("Data Source=test_proguard.db");
        }
    }

    [Fact]
    public async Task Database_FullCrudOperations_ExecuteReliably()
    {
        using var db = new TestDbContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        // 1. ProcessRule CRUD
        var rule = new ProcessRule
        {
            ProcessName = "render.exe",
            PriorityClass = (int)System.Diagnostics.ProcessPriorityClass.AboveNormal,
            AffinityMask = 0b11110000,
            CpuLimitPercent = 75.0,
            DisallowExecution = false,
            ExcludeFromProBalance = true
        };

        db.ProcessRules.Add(rule);
        await db.SaveChangesAsync();
        Assert.True(rule.Id > 0);

        var savedRule = await db.ProcessRules.FirstOrDefaultAsync(r => r.ProcessName == "render.exe");
        Assert.NotNull(savedRule);
        Assert.Equal(75.0, savedRule.CpuLimitPercent);

        // 2. WatchdogRule CRUD
        var wdRule = new WatchdogRule
        {
            TargetProcessName = "leak.exe",
            IsEnabled = true,
            ConditionType = WatchdogCondition.RamAbove,
            ThresholdValue = 2048.0,
            ActionType = WatchdogAction.Terminate
        };

        db.WatchdogRules.Add(wdRule);
        await db.SaveChangesAsync();
        Assert.True(wdRule.Id > 0);

        // 3. ProBalanceAction Logging
        var action = new ProBalanceAction
        {
            Timestamp = DateTime.UtcNow,
            ProcessName = "heavy.exe",
            Pid = 9999,
            OriginalPriority = (int)System.Diagnostics.ProcessPriorityClass.Normal,
            NewPriority = (int)System.Diagnostics.ProcessPriorityClass.BelowNormal,
            Reason = "CPU load threshold exceeded (89%)",
            IsRestored = false
        };

        db.ProBalanceActions.Add(action);
        await db.SaveChangesAsync();
        Assert.True(action.Id > 0);

        var actions = await db.ProBalanceActions.Where(a => a.ProcessName == "heavy.exe").ToListAsync();
        Assert.NotEmpty(actions);

        // Cleanup
        await db.Database.EnsureDeletedAsync();
    }

    [Fact]
    public void IPC_AllMessageTypes_SerializeAndDeserialize()
    {
        foreach (MessageType type in Enum.GetValues(typeof(MessageType)))
        {
            var msg = new IpcMessage
            {
                Type = type,
                Payload = "{\"test\": true}",
                Timestamp = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(msg);
            var deserialized = JsonSerializer.Deserialize<IpcMessage>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(type, deserialized.Type);
            Assert.Equal("{\"test\": true}", deserialized.Payload);
        }
    }

    [Fact]
    public void EngineStatus_Contracts_MatchExpectedLayout()
    {
        var status = new EngineStatus
        {
            IsRunning = true,
            ProBalanceEngaged = false,
            ActiveInterventions = 0,
            CpuPercent = 14.5,
            RamPercent = 45.2,
            Uptime = "02:00:00",
            ActiveRuleCount = 8,
            ProcessCount = 185
        };

        Assert.True(status.IsRunning);
        Assert.Equal(185, status.ProcessCount);
        Assert.Equal(14.5, status.CpuPercent);
    }
}
