using ProGuard.Core.Models;

namespace ProGuard.Core.Services;

/// <summary>
/// Interface for the Watchdog IFTTT service.
/// </summary>
public interface IWatchdogService
{
    /// <summary>Add a new watchdog rule.</summary>
    void AddRule(WatchdogRule rule);

    /// <summary>Remove a watchdog rule by ID.</summary>
    void RemoveRule(int ruleId);

    /// <summary>Update an existing watchdog rule.</summary>
    void UpdateRule(WatchdogRule rule);
}
