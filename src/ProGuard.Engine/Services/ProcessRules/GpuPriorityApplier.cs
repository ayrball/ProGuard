using ProGuard.Core.Models;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary><see cref="ProcessRule.GpuPriority"/> via D3DKMTSetProcessSchedulingPriorityClass.</summary>
public sealed class GpuPriorityApplier : ProcessSettingApplier<int>
{
    public GpuPriorityApplier(IProcessTuningNative native) : base(native) { }

    public override string Setting => "GPU priority";

    protected override bool TryGetDesired(ProcessRule rule, out int value, out string? error) =>
        TryGetOption(rule.GpuPriority, ProcessRuleOptions.GpuPriorities, out value, out error);

    protected override bool TryRead(int pid, out int value) => Native.TryGetGpuPriority(pid, out value);
    protected override void Write(int pid, int value) => Native.SetGpuPriority(pid, value);
    protected override string Describe(int value) => ProcessRuleOptions.Label(ProcessRuleOptions.GpuPriorities, value);
}
