using ProGuard.Core.Models;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary><see cref="ProcessRule.MemoryPriority"/> via SetProcessInformation(ProcessMemoryPriority).</summary>
public sealed class MemoryPriorityApplier : ProcessSettingApplier<int>
{
    public MemoryPriorityApplier(IProcessTuningNative native) : base(native) { }

    public override string Setting => "Memory priority";

    protected override bool TryGetDesired(ProcessRule rule, out int value, out string? error) =>
        TryGetOption(rule.MemoryPriority, ProcessRuleOptions.MemoryPriorities, out value, out error, ProcessRuleOptions.MinMemoryPriority);

    protected override bool TryRead(int pid, out int value) => Native.TryGetMemoryPriority(pid, out value);
    protected override void Write(int pid, int value) => Native.SetMemoryPriority(pid, value);
    protected override string Describe(int value) => ProcessRuleOptions.Label(ProcessRuleOptions.MemoryPriorities, value);
}
