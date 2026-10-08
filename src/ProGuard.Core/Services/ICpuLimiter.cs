namespace ProGuard.Core.Services;

public interface ICpuLimiter
{
    void AddLimit(int pid, double percent);
    void RemoveLimit(int pid);
}
