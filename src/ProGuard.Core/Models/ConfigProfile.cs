using System.ComponentModel.DataAnnotations;

namespace ProGuard.Core.Models;

/// <summary>
/// A named copy of the process rules, Watchdog rules and settings, stored as the config file's JSON
/// (ProGuard.Shared.Contracts.ConfigDocument), so a profile and an export are the same thing and an old profile is
/// read the way an old file is.
/// </summary>
public class ConfigProfile
{
    [Key]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime SavedUtc { get; set; }
    public string Document { get; set; } = string.Empty;

    // Kept alongside the document so the list doesn't have to read every one.
    public int ProcessRuleCount { get; set; }
    public int WatchdogRuleCount { get; set; }
}
