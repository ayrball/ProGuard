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

    /// <summary>
    /// SID of the account that created the profile, set by the engine. Standard users may overwrite, rename or delete
    /// only profiles they created; administrators may change any. Null for profiles from before this was recorded,
    /// which only administrators may change.
    /// </summary>
    public string? CreatorSid { get; set; }

    /// <summary>
    /// Who had saved each rule when the profile was saved, as engine-written JSON (never part of the document, so never
    /// exported or imported). Switching back to the profile restores a standard user's rule as theirs, rather than as an
    /// administrator's that acts on everyone's processes.
    /// </summary>
    public string? RuleAuthors { get; set; }
}
