using ProGuard.Core.Models;

namespace ProGuard.Core.Services;

/// <summary>
/// ProBalance's optional restraint actions and its idle pause (Process Lasso's <c>[OutOfControlProcessRestraint]</c>
/// keys of the same names). All off by default, as in Process Lasso.
/// </summary>
public sealed record ProBalanceRestraintOptions
{
    /// <summary>Also restrict a restrained process to <see cref="RestraintAffinity"/> (intersected with its own affinity).</summary>
    public bool RestrainByAffinity { get; init; }
    /// <summary>Logical CPUs a restrained process may use, as a mask; 0 = none chosen, so nothing is restricted.</summary>
    public long RestraintAffinity { get; init; }
    /// <summary>Restrict affinity by removing one random CPU from the process's own mask (takes precedence over <see cref="RestraintAffinity"/>).</summary>
    public bool ProBalanceDropOneRandomCore { get; init; }
    /// <summary>Lower the I/O priority to Low during restraint.</summary>
    public bool LowerIOPriorityDuringRestraint { get; init; }
    /// <summary>Turn on efficiency mode (EcoQoS) during restraint.</summary>
    public bool UseEfficiencyMode { get; init; }
    /// <summary>Leave the priority class alone and restrain only through the actions above.</summary>
    public bool DoNotLowerPriorityClass { get; init; }
    /// <summary>Stop restraining (and lift current restraints) while the user has been idle for <see cref="DisableProBalanceIfSysIdleThisManyMS"/>.</summary>
    public bool DisableProBalanceWhenSysIdle { get; init; }
    public int DisableProBalanceIfSysIdleThisManyMS { get; init; } = 30000;

    public static ProBalanceRestraintOptions FromSettings(AppSettings s) => new()
    {
        RestrainByAffinity = s.ProBalanceRestrainByAffinity,
        RestraintAffinity = s.ProBalanceRestraintAffinity,
        ProBalanceDropOneRandomCore = s.ProBalanceDropOneRandomCore,
        LowerIOPriorityDuringRestraint = s.ProBalanceLowerIoPriority,
        UseEfficiencyMode = s.ProBalanceUseEfficiencyMode,
        DoNotLowerPriorityClass = s.ProBalanceDoNotLowerPriorityClass,
        DisableProBalanceWhenSysIdle = s.ProBalanceDisableWhenSysIdle,
        DisableProBalanceIfSysIdleThisManyMS = s.ProBalanceDisableIfSysIdleMs,
    };
}
