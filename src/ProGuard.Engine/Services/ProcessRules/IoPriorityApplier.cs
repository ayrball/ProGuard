using ProGuard.Core.Models;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary><see cref="ProcessRule.IoPriority"/> via NtSetInformationProcess(ProcessIoPriority = 33).</summary>
public sealed class IoPriorityApplier : ProcessSettingApplier<int>
{
    public IoPriorityApplier(IProcessTuningNative native) : base(native) { }

    public override string Setting => "I/O priority";

    protected override bool TryGetDesired(ProcessRule rule, out int value, out string? error) =>
        TryGetOption(rule.IoPriority, ProcessRuleOptions.IoPriorities, out value, out error);

    protected override bool TryRead(int pid, out int value) => Native.TryGetIoPriority(pid, out value);
    protected override void Write(int pid, int value) => Native.SetIoPriority(pid, value);
    protected override string Describe(int value) => ProcessRuleOptions.Label(ProcessRuleOptions.IoPriorities, value);
}
