namespace ProGuard.Core.Services;

public interface ICpuLimiter
{
    void AddLimit(int pid, double percent);
    void RemoveLimit(int pid);

    /// <summary>The hard caps in force right now: process id to percent of the whole machine's CPU.</summary>
    IReadOnlyDictionary<int, double> GetActiveLimits();
}
