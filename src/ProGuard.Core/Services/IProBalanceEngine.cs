namespace ProGuard.Core.Services;

public interface IProBalanceEngine
{
    bool IsEngaged { get; }
    int ActiveInterventions { get; }

    /// <summary>ProBalance is switched on (it may still be paused, see <see cref="IsPausedForIdle"/>).</summary>
    bool IsEnabled { get; }

    /// <summary>Paused because the user is idle (DisableProBalanceWhenSysIdle); no restraints are held meanwhile.</summary>
    bool IsPausedForIdle { get; }

    /// <summary>Restraints started since the engine started.</summary>
    int TotalRestraints { get; }

    /// <summary>Processes held at a lowered priority right now.</summary>
    IReadOnlyCollection<int> RestrainedPids { get; }

    void SetEnabled(bool enabled);
    void SetSensitivity(int level);
    /// <summary>Optional restraint actions and the idle pause. Restraints already in place keep what they applied.</summary>
    void ConfigureRestraint(ProBalanceRestraintOptions options);
    void ExcludeProcess(string name);
    void RemoveExclusion(string name);

    /// <summary>
    /// Ends the restraint on <paramref name="pid"/> now, restoring its original priority. Used when the process
    /// comes to the foreground so other services see its real priority. Returns false if it wasn't restrained.
    /// </summary>
    bool ReleaseRestraint(int pid);
}
