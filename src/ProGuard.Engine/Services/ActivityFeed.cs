using System;
using System.Collections.Generic;
using System.Linq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Shared.Contracts;
using ActivityKind = ProGuard.Shared.Contracts.ActivityKind; // System.Diagnostics has one too

namespace ProGuard.Engine.Services;

/// <summary>
/// The activity feed the UI shows, merged from the three places the engine records what happened: the action log in
/// the database (ProBalance restraints and restores, CPU Limiter, power plan changes), the Watchdog event log, and the
/// in-memory log of what users did through the command pipe.
/// </summary>
public sealed class ActivityFeed
{
    public const int DefaultCount = 200;
    public const int MaxCount = 1000;

    private readonly IRuleStore _ruleStore;
    private readonly WatchdogEventLog? _watchdog;
    private readonly ActivityLog _manual;

    public ActivityFeed(IRuleStore ruleStore, ActivityLog manual, WatchdogEventLog? watchdog = null)
    {
        _ruleStore = ruleStore;
        _manual = manual;
        _watchdog = watchdog;
    }

    /// <summary>The newest <paramref name="count"/> entries from every source together, newest first.</summary>
    public List<ActivityEntry> GetRecent(int count)
    {
        count = Math.Clamp(count, 1, MaxCount);

        // Each source contributes at most its own newest `count`, which is enough for the newest `count` overall.
        var entries = new List<ActivityEntry>();
        entries.AddRange(_manual.GetRecent(count));
        if (_watchdog != null) entries.AddRange(_watchdog.GetRecent(count).Select(FromWatchdog));
        var actions = _ruleStore.GetRecentActions(count);
        if (actions != null) entries.AddRange(actions.Select(FromAction));

        // Stable: equal timestamps keep the order above (users' own actions first).
        return entries.OrderByDescending(e => e.TimestampUtc).Take(count).ToList();
    }

    /// <summary>An action-log row: restraint, restore, CPU Limiter constraint or power plan change.</summary>
    internal static ActivityEntry FromAction(ProBalanceAction action)
    {
        var reason = action.Reason ?? string.Empty;
        var entry = new ActivityEntry
        {
            TimestampUtc = AsUtc(action.Timestamp),
            ProcessName = action.ProcessName ?? string.Empty,
            Pid = action.Pid,
        };

        if (action.ProcessName == ProBalanceAction.PowerPlanEntryName)
        {
            entry.Kind = ActivityKind.Power;
            entry.ProcessName = string.Empty; // the page's process column would only repeat "Power plan"
            entry.Message = reason;
        }
        else if (reason.StartsWith("Constraining CPU affinity", StringComparison.Ordinal))
        {
            entry.Kind = ActivityKind.Rule; // the CPU Limiter acting on a rule
            entry.Message = reason;
        }
        else if (action.IsRestored)
        {
            entry.Kind = ActivityKind.Restored;
            entry.Message = reason;
        }
        else
        {
            entry.Kind = ActivityKind.Restrained;
            entry.Message = "Restrained: " + reason;
        }
        return entry;
    }

    /// <summary>
    /// A Watchdog or Keep Running event. A watchdog action that failed stays under Watchdog (rather than System) so it
    /// shows next to the rule that caused it; a timed change being put back is Restored.
    /// </summary>
    internal static ActivityEntry FromWatchdog(WatchdogEventDto e) => new()
    {
        TimestampUtc = AsUtc(e.TimestampUtc),
        Kind = e.Kind == WatchdogEventKinds.Restored ? ActivityKind.Restored : ActivityKind.Watchdog,
        ProcessName = e.ProcessName ?? string.Empty,
        Pid = e.Pid ?? 0,
        Message = string.IsNullOrWhiteSpace(e.Message) ? e.Title : e.Message,
    };

    // Rows read back from SQLite come without a kind; the engine writes UTC.
    private static DateTime AsUtc(DateTime time) => time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time.ToUniversalTime();
}
