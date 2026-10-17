using System.Collections.Generic;
using System.Threading.Tasks;
using ProGuard.Core.Models;

namespace ProGuard.Core.Services;

/// <summary>
/// Interface for persistent storage of rules, settings, and action logs.
/// </summary>
public interface IRuleStore
{
    // Process Rules
    IEnumerable<ProcessRule> GetProcessRules();
    IEnumerable<ProcessRule> GetAllProcessRules();
    ProcessRule? GetProcessRule(string processName);
    void AddProcessRule(ProcessRule rule);
    void SaveProcessRule(ProcessRule rule);
    void UpdateProcessRule(ProcessRule rule);
    void DeleteProcessRule(int id);

    // Watchdog Rules
    IEnumerable<WatchdogRule> GetWatchdogRules();
    void AddWatchdogRule(WatchdogRule rule);
    void UpdateWatchdogRule(WatchdogRule rule);
    void DeleteWatchdogRule(int id);

    // Power Profiles
    IEnumerable<PowerProfile> GetPowerProfiles();
    void AddPowerProfile(PowerProfile profile);
    void UpdatePowerProfile(PowerProfile profile);
    void DeletePowerProfile(int id);

    // Settings
    AppSettings GetSettings();
    void UpdateSettings(AppSettings settings);

    // Logging
    Task LogProBalanceActionAsync(ProBalanceAction action);

    /// <summary>The newest <paramref name="count"/> logged actions (restraints, restores, CPU Limiter, power plan changes), newest first.</summary>
    IReadOnlyList<ProBalanceAction> GetRecentActions(int count);

    /// <summary>
    /// Logged actions with an Id below <paramref name="beforeId"/> and above <paramref name="afterId"/> (either may be
    /// null for no bound), newest first, at most <paramref name="count"/>. Ids only grow, so they order the log.
    /// </summary>
    IReadOnlyList<ProBalanceAction> GetActions(int? beforeId, int? afterId, int count);

    /// <summary>Deletes every logged action.</summary>
    void ClearProBalanceActions();
}
