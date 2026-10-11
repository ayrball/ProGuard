using ProGuard.Core.Models;

namespace ProGuard.Core.Services;

/// <summary>
/// ResponsivenessGuard's optional restraint actions and its idle pause (another process manager's <c>[OutOfControlProcessRestraint]</c>
/// keys of the same names). All off by default, as in another process manager.
/// </summary>
public sealed record ResponsivenessGuardRestraintOptions
{
    /// <summary>Also restrict a restrained process to <see cref="RestraintAffinity"/> (intersected with its own affinity).</summary>
    public bool RestrainByAffinity { get; init; }
    /// <summary>Logical CPUs a restrained process may use, as a mask; 0 = none chosen, so nothing is restricted.</summary>
    public long RestraintAffinity { get; init; }
    /// <summary>Restrict affinity by removing one random CPU from the process's own mask (takes precedence over <see cref="RestraintAffinity"/>).</summary>
    public bool ResponsivenessGuardDropOneRandomCore { get; init; }
    /// <summary>Lower the I/O priority to Low during restraint.</summary>
    public bool LowerIOPriorityDuringRestraint { get; init; }
    /// <summary>Turn on efficiency mode (EcoQoS) during restraint.</summary>
    public bool UseEfficiencyMode { get; init; }
    /// <summary>Leave the priority class alone and restrain only through the actions above.</summary>
    public bool DoNotLowerPriorityClass { get; init; }
    /// <summary>Stop restraining (and lift current restraints) while the user has been idle for <see cref="DisableResponsivenessGuardIfSysIdleThisManyMS"/>.</summary>
    public bool DisableResponsivenessGuardWhenSysIdle { get; init; }
    public int DisableResponsivenessGuardIfSysIdleThisManyMS { get; init; } = 30000;

    public static ResponsivenessGuardRestraintOptions FromSettings(AppSettings s) => new()
    {
        RestrainByAffinity = s.ResponsivenessGuardRestrainByAffinity,
        RestraintAffinity = s.ResponsivenessGuardRestraintAffinity,
        ResponsivenessGuardDropOneRandomCore = s.ResponsivenessGuardDropOneRandomCore,
        LowerIOPriorityDuringRestraint = s.ResponsivenessGuardLowerIoPriority,
        UseEfficiencyMode = s.ResponsivenessGuardUseEfficiencyMode,
        DoNotLowerPriorityClass = s.ResponsivenessGuardDoNotLowerPriorityClass,
        DisableResponsivenessGuardWhenSysIdle = s.ResponsivenessGuardDisableWhenSysIdle,
        DisableResponsivenessGuardIfSysIdleThisManyMS = s.ResponsivenessGuardDisableIfSysIdleMs,
    };
}
