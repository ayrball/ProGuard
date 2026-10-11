using ProGuard.Core.Models;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary><see cref="ProcessRule.DisablePriorityBoost"/> via SetProcessPriorityBoost.</summary>
public sealed class PriorityBoostApplier : ProcessSettingApplier<bool>
{
    public PriorityBoostApplier(IProcessTuningNative native) : base(native) { }

    public override string Setting => "Dynamic priority boost";

    protected override bool TryGetDesired(ProcessRule rule, out bool value, out string? error)
    {
        error = null;
        value = rule.DisablePriorityBoost == true;
        return rule.DisablePriorityBoost.HasValue;
    }

    protected override bool TryRead(int pid, out bool value) => Native.TryGetPriorityBoostDisabled(pid, out value);
    protected override void Write(int pid, bool value) => Native.SetPriorityBoostDisabled(pid, value);
    protected override string Describe(bool disabled) => disabled ? "disabled" : "enabled";
}
