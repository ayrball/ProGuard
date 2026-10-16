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

    /// <summary>
    /// Replaces every process rule, every Watchdog rule and the settings in one transaction (a config profile switch or
    /// import): either all of it is stored or none of it. The rules get new ids.
    /// </summary>
    void ReplaceConfiguration(IReadOnlyList<ProcessRule> processRules, IReadOnlyList<WatchdogRule> watchdogRules, AppSettings settings);

    // Logging
    Task LogProBalanceActionAsync(ProBalanceAction action);

    /// <summary>The newest <paramref name="count"/> logged actions (restraints, restores, CPU Limiter, power plan changes), newest first.</summary>
    IReadOnlyList<ProBalanceAction> GetRecentActions(int count);

    /// <summary>Deletes every logged action.</summary>
    void ClearProBalanceActions();
}
