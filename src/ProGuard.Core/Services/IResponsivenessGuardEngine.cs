namespace ProGuard.Core.Services;

public interface IResponsivenessGuardEngine
{
    bool IsEngaged { get; }
    int ActiveInterventions { get; }
    void SetEnabled(bool enabled);
    void SetSensitivity(int level);
    void ExcludeProcess(string name);
    void RemoveExclusion(string name);
}
