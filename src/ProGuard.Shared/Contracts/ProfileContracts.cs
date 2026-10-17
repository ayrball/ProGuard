using System;
using System.Collections.Generic;

namespace ProGuard.Shared.Contracts;

/// <summary>
/// A whole configuration: the process rules, the Watchdog rules and the engine settings, as one document. It is what a
/// config profile stores and what <see cref="Messages.CommandTypes.ExportConfig"/> returns and
/// <see cref="Messages.CommandTypes.ImportConfig"/> reads. <see cref="Profiles.ConfigDocuments"/> reads and writes the
/// file form and checks <see cref="SchemaVersion"/>.
/// </summary>
public sealed class ConfigDocument
{
    /// <summary>What <see cref="Format"/> holds in every ProGuard config, so another JSON file isn't mistaken for one.</summary>
    public const string FormatName = "ProGuard.Config";

    /// <summary>
    /// The version this build writes. Raise it when the document changes in a way an older reader would get wrong, and
    /// teach <see cref="Profiles.ConfigDocuments.Parse"/> to upgrade the older versions.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    public string Format { get; set; } = FormatName;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>The profile's name, or "Current configuration" for an export of what is in force.</summary>
    public string Name { get; set; } = string.Empty;
    public DateTime SavedUtc { get; set; }

    /// <summary>The machine the document was saved on, for telling which values belong to that machine only. Null when unknown.</summary>
    public MachineDescription? Machine { get; set; }

    /// <summary>Rule ids are not kept: they are the database's keys and mean nothing anywhere else.</summary>
    public List<ProcessRuleDto> ProcessRules { get; set; } = new();
    public List<WatchdogRuleDto> WatchdogRules { get; set; } = new();
    public EngineSettingsDto Settings { get; set; } = new();
}

/// <summary>The parts of a machine that some settings only make sense on.</summary>
public sealed class MachineDescription
{
    /// <summary>Logical processors: an affinity mask naming a processor beyond these can't be applied.</summary>
    public int LogicalProcessors { get; set; }

    /// <summary>
    /// The CPU Sets, as "id:core:efficiency class" separated by commas. CPU Set IDs are only meaningful on a machine
    /// with this same layout.
    /// </summary>
    public string? CpuSetLayout { get; set; }

    /// <summary>The installed power plans, so a plan that isn't installed elsewhere can still be named.</summary>
    public List<PowerPlanDto> PowerPlans { get; set; } = new();
}

/// <summary>One saved profile, for <see cref="Messages.CommandTypes.ListProfiles"/>.</summary>
public sealed class ConfigProfileDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime SavedUtc { get; set; }
    public int ProcessRuleCount { get; set; }
    public int WatchdogRuleCount { get; set; }

    /// <summary>Saved by the engine itself just before a switch or import replaced the configuration ("Before switch").</summary>
    public bool Automatic { get; set; }
}

/// <summary>Argument of <see cref="Messages.CommandTypes.RenameProfile"/>.</summary>
public sealed class RenameProfileArgument
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>Argument of <see cref="Messages.CommandTypes.SwitchProfile"/>.</summary>
public sealed class SwitchProfileArgument
{
    public int Id { get; set; }

    /// <summary>Only report what switching would change; change nothing.</summary>
    public bool Preview { get; set; }
}

/// <summary>Argument of <see cref="Messages.CommandTypes.ImportConfig"/>.</summary>
public sealed class ImportConfigArgument
{
    /// <summary>The file's text, as <see cref="Profiles.ConfigDocuments.Serialize"/> wrote it.</summary>
    public string Document { get; set; } = string.Empty;

    /// <summary>Only report what importing would change; change nothing.</summary>
    public bool Preview { get; set; }
}

public enum ConfigChangeKind { Added, Changed, Removed }

/// <summary>One difference between the configuration in force and the one a switch or import would put in its place.</summary>
public sealed class ConfigChangeDto
{
    public ConfigChangeKind Kind { get; set; }

    /// <summary>"Process rule", "Watchdog rule" or "Setting".</summary>
    public string Area { get; set; } = string.Empty;

    /// <summary>The rule's program (and, for a Watchdog rule, what it does), or the setting's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What changes: the fields of a changed rule, or a setting's old and new value.</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// A value that only works on the machine it was saved on (CPU Set IDs, an affinity mask naming processors this machine
/// lacks, a power plan that isn't installed). It is left out rather than applied; this says so.
/// </summary>
public sealed class MachineSpecificValueDto
{
    /// <summary>"Process rule game.exe", "Watchdog rule chrome.exe" or "Settings".</summary>
    public string Where { get; set; } = string.Empty;
    public string Setting { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    /// <summary>What happens instead, e.g. "Left out: the rule doesn't change CPU Sets".</summary>
    public string Outcome { get; set; } = string.Empty;
}

/// <summary>
/// What a switch or import does (or, with Preview, would do): the changes, the machine-specific values left out, and
/// anything that stops it. Result of <see cref="Messages.CommandTypes.SwitchProfile"/> and
/// <see cref="Messages.CommandTypes.ImportConfig"/>.
/// </summary>
public sealed class ConfigPreviewDto
{
    public string Name { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public DateTime SavedUtc { get; set; }

    public List<ConfigChangeDto> Changes { get; set; } = new();
    public List<MachineSpecificValueDto> MachineSpecific { get; set; } = new();

    /// <summary>Rules or settings that aren't valid; nothing can be applied while there are any.</summary>
    public List<string> Problems { get; set; } = new();

    /// <summary>What the caller isn't allowed to do ("Access denied: …"); nothing can be applied while there are any.</summary>
    public List<string> Refusals { get; set; } = new();

    public bool CanApply => Problems.Count == 0 && Refusals.Count == 0;

    /// <summary>True once the configuration has been replaced; false for a preview.</summary>
    public bool Applied { get; set; }

    /// <summary>The profile the previous configuration was saved as ("Before switch"), when it was applied.</summary>
    public string? BackupName { get; set; }
}
