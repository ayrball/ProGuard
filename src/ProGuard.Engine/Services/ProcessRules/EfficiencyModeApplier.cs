using ProGuard.Core.Models;

namespace ProGuard.Engine.Services.ProcessRules;

public enum EfficiencyModeState { NotSet, On, Off }

/// <summary>
/// <see cref="ProcessRule.EfficiencyMode"/> via SetProcessInformation(ProcessPowerThrottling, EXECUTION_SPEED).
/// "Off" sets the control bit with the state bit clear, which opts the process out of EcoQoS (on hybrid CPUs it then
/// prefers P-cores); merely clearing the control bit would hand the decision back to Windows.
/// </summary>
public sealed class EfficiencyModeApplier : ProcessSettingApplier<EfficiencyModeState>
{
    public EfficiencyModeApplier(IProcessTuningNative native) : base(native) { }

    public override string Setting => "Efficiency mode";

    protected override bool TryGetDesired(ProcessRule rule, out EfficiencyModeState value, out string? error)
    {
        error = null;
        value = rule.EfficiencyMode == true ? EfficiencyModeState.On : EfficiencyModeState.Off;
        return rule.EfficiencyMode.HasValue;
    }

    protected override bool TryRead(int pid, out EfficiencyModeState value)
    {
        var ok = Native.TryGetEfficiencyMode(pid, out var controlled, out var enabled);
        value = !controlled ? EfficiencyModeState.NotSet : enabled ? EfficiencyModeState.On : EfficiencyModeState.Off;
        return ok;
    }

    protected override void Write(int pid, EfficiencyModeState value) => Native.SetEfficiencyMode(pid, value == EfficiencyModeState.On);

    protected override string Describe(EfficiencyModeState value) => value switch
    {
        EfficiencyModeState.On => "on",
        EfficiencyModeState.Off => "off",
        _ => "not set",
    };
}
