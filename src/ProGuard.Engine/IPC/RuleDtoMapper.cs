using ProGuard.Core.Models;
using ProGuard.Shared.Contracts;

namespace ProGuard.Engine.IPC;

/// <summary>Converts between the stored <see cref="ProcessRule"/> and the <see cref="RuleDto"/> sent over IPC.</summary>
public static class RuleDtoMapper
{
    public const string ProcessRuleType = "Process";

    public static RuleDto ToDto(ProcessRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.ProcessName,
        Type = ProcessRuleType,
        IoPriority = rule.IoPriority,
        MemoryPriority = rule.MemoryPriority,
        EfficiencyMode = rule.EfficiencyMode,
        DisablePriorityBoost = rule.DisablePriorityBoost,
        CpuSetIds = rule.CpuSetIds,
        GpuPriority = rule.GpuPriority,
        GpuPreference = rule.GpuPreference,
        ReapplyIfChanged = rule.ReapplyIfChanged,
        CpuLimitThresholdPercent = rule.CpuLimitThresholdPercent,
        CpuLimitOverThresholdSeconds = rule.CpuLimitOverThresholdSeconds,
        CpuLimitCoresToRemove = rule.CpuLimitCoresToRemove,
        CpuLimitDurationSeconds = rule.CpuLimitDurationSeconds,
        MaxInstances = rule.MaxInstances,
        MaxInstancesAllSessions = rule.MaxInstancesAllSessions,
        InstanceSpreadMode = (int?)rule.InstanceSpreadMode,
        InstanceSpreadCoresPerInstance = rule.InstanceSpreadCoresPerInstance,
        InstanceSpreadAvoidSmt = rule.InstanceSpreadAvoidSmt,
        InstanceSpreadReservedCores = rule.InstanceSpreadReservedCores,
        InstanceSpreadMinCpuPercent = rule.InstanceSpreadMinCpuPercent,
        StartupBoostPriorityClass = rule.StartupBoostPriorityClass,
        StartupBoostSeconds = rule.StartupBoostSeconds,
    };

    /// <summary>Copies the fields a <see cref="RuleDto"/> carries onto <paramref name="rule"/>; the others are left as they are.</summary>
    public static void CopyTo(RuleDto dto, ProcessRule rule)
    {
        rule.ProcessName = dto.Name;
        rule.IoPriority = dto.IoPriority;
        rule.MemoryPriority = dto.MemoryPriority;
        rule.EfficiencyMode = dto.EfficiencyMode;
        rule.DisablePriorityBoost = dto.DisablePriorityBoost;
        rule.CpuSetIds = dto.CpuSetIds;
        rule.GpuPriority = dto.GpuPriority;
        rule.GpuPreference = dto.GpuPreference;
        rule.ReapplyIfChanged = dto.ReapplyIfChanged;
        rule.CpuLimitThresholdPercent = dto.CpuLimitThresholdPercent;
        rule.CpuLimitOverThresholdSeconds = dto.CpuLimitOverThresholdSeconds;
        rule.CpuLimitCoresToRemove = dto.CpuLimitCoresToRemove;
        rule.CpuLimitDurationSeconds = dto.CpuLimitDurationSeconds;
        rule.MaxInstances = dto.MaxInstances;
        rule.MaxInstancesAllSessions = dto.MaxInstancesAllSessions;
        rule.InstanceSpreadMode = dto.InstanceSpreadMode is int mode && Enum.IsDefined(typeof(InstanceSpreadMode), mode)
            ? (InstanceSpreadMode)mode : null;
        rule.InstanceSpreadCoresPerInstance = dto.InstanceSpreadCoresPerInstance;
        rule.InstanceSpreadAvoidSmt = dto.InstanceSpreadAvoidSmt;
        rule.InstanceSpreadReservedCores = dto.InstanceSpreadReservedCores;
        rule.InstanceSpreadMinCpuPercent = dto.InstanceSpreadMinCpuPercent;
        rule.StartupBoostPriorityClass = dto.StartupBoostPriorityClass;
        rule.StartupBoostSeconds = dto.StartupBoostSeconds;
    }
}
