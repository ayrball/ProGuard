using ProGuard.Core.Models;

namespace ProGuard.Core.Services;

/// <summary>
/// Responsiveness Guard's optional restraint actions and its idle pause. All off by default.
/// </summary>
public sealed record ResponsivenessGuardRestraintOptions
{
    /// <summary>Also restrict a restrained process to <see cref="RestrictedAffinity"/> (intersected with its own affinity).</summary>
    public bool RestrictAffinity { get; init; }
    /// <summary>Logical CPUs a restrained process may use, as a mask; 0 = none chosen, so nothing is restricted.</summary>
    public long RestrictedAffinity { get; init; }
    /// <summary>Restrict affinity by removing one random CPU from the process's own mask (takes precedence over <see cref="RestrictedAffinity"/>).</summary>
    public bool DropOneRandomCore { get; init; }
    /// <summary>Lower the I/O priority to Low during restraint.</summary>
    public bool LowerIoPriority { get; init; }
    /// <summary>Turn on efficiency mode (EcoQoS) during restraint.</summary>
    public bool EfficiencyModeWhileRestrained { get; init; }
    /// <summary>Leave the priority class alone and restrain only through the actions above.</summary>
    public bool KeepPriorityClass { get; init; }
    /// <summary>Stop restraining (and lift current restraints) while the user has been idle for <see cref="PauseAfterIdleMs"/>.</summary>
    public bool PauseWhenIdle { get; init; }
    public int PauseAfterIdleMs { get; init; } = 30000;

    public static ResponsivenessGuardRestraintOptions FromSettings(AppSettings s) => new()
    {
        RestrictAffinity = s.ResponsivenessGuardRestrainByAffinity,
        RestrictedAffinity = s.ResponsivenessGuardRestraintAffinity,
        DropOneRandomCore = s.ResponsivenessGuardDropOneRandomCore,
        LowerIoPriority = s.ResponsivenessGuardLowerIoPriority,
        EfficiencyModeWhileRestrained = s.ResponsivenessGuardUseEfficiencyMode,
        KeepPriorityClass = s.ResponsivenessGuardDoNotLowerPriorityClass,
        PauseWhenIdle = s.ResponsivenessGuardDisableWhenSysIdle,
        PauseAfterIdleMs = s.ResponsivenessGuardDisableIfSysIdleMs,
    };
}
