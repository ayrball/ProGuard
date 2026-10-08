using ProGuard.Core.Models;

namespace ProGuard.Core.Services;

/// <summary>
/// Interface for MemoryTrim - periodic working set trimming of high-memory processes.
/// </summary>
public interface IMemoryTrimService
{
    /// <summary>Trim all processes above the configured memory threshold.</summary>
    void TrimMemory();

    /// <summary>Trim a specific process by PID.</summary>
    void TrimProcess(int pid);

    /// <summary>Memory threshold in bytes. Processes above this get trimmed.</summary>
    long ThresholdBytes { get; set; }
}
