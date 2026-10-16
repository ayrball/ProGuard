# Config profiles, export and import

A **profile** is a named copy of the process rules, the Watchdog rules and every engine setting. Profiles live in the
engine's database (table `ConfigProfiles`, added to existing databases by `SchemaUpgrader`), each one stored as the
same JSON document that **Export** writes to a file. So a saved profile and an exported file are read by the same code,
and an old profile is upgraded the way an old file is.

UI: System page, section **J · PROFILES & BACKUP**.

## Commands

| Command | Argument | Who |
| --- | --- | --- |
| `ListProfiles` | none | anyone |
| `SaveProfile` | name (replaces a profile of that name) | anyone |
| `RenameProfile` | `RenameProfileArgument` | anyone |
| `DeleteProfile` | profile id | anyone |
| `ExportConfig` | profile id, or nothing for what is in force; returns a `ConfigDocument` | anyone |
| `SwitchProfile` | `SwitchProfileArgument { Id, Preview }` | anyone, then checked rule by rule |
| `ImportConfig` | `ImportConfigArgument { Document, Preview }` | anyone, then checked rule by rule |

A profile changes nothing until it is applied, and its contents are readable by anyone anyway, so the bookkeeping
commands are open. Applying one is what is checked: every rule a switch or import would add, change or remove goes
through `RuleAuthorization` as the caller, and the settings through `IpcAuthorization.CheckSettingsChange`
(`AdministratorOnlySettings`). One refusal stops the whole switch. Rules that stay exactly as they are aren't checked,
so a standard user can switch between profiles that both keep an administrator's rule.

## Switching and importing

With `Preview`, the engine only reports what would happen (`ConfigPreviewDto`): the changes, the machine-specific
values left out, invalid rules or settings (`Problems`) and refusals. Without it, the engine:

1. saves what is in force as **Before switch** (or **Before import**), so switching to that profile undoes it;
2. replaces all process rules, Watchdog rules and settings in one database transaction
   (`IRuleStore.ReplaceConfiguration`): all of it is stored or none of it;
3. puts the rules into effect on running processes as saving them does (`RuleService.ApplyReplaced`), and the settings
   through `SettingsApplier`.

Switch and import run under the dispatcher's settings lock, like `SaveSettings`.

## Machine-specific values

Some values only work on the machine they were saved on. `ConfigDocuments.FitToMachine` leaves them out and lists each
one; the preview shows them under "ONLY FOR THE MACHINE IT WAS SAVED ON".

| Value | Left out when | Instead |
| --- | --- | --- |
| A rule's CPU Set IDs | an ID doesn't exist here, or the CPU Set layout (id:core:efficiency class) differs from the saving machine's | the rule doesn't change CPU Sets |
| A rule's affinity mask | it names a logical processor this machine lacks | the rule doesn't change the cores |
| Watchdog "set affinity" mask | as above | the rule is imported turned off |
| ProBalance restraint cores | as above | no cores picked |
| Power plan GUIDs (rule, IdleSaver, Performance Mode, load switcher, plan at start) | the plan isn't installed | the default plan |

## File format

```json
{
  "Format": "ProGuard.Config",
  "SchemaVersion": 1,
  "Name": "Gaming",
  "SavedUtc": "2026-10-16T12:00:00Z",
  "Machine": { "LogicalProcessors": 16, "CpuSetLayout": "256:0:1,…", "PowerPlans": [ … ] },
  "ProcessRules": [ … ProcessRuleDto … ],
  "WatchdogRules": [ … WatchdogRuleDto … ],
  "Settings": { … EngineSettingsDto … }
}
```

Enums are written by name and read by name or number. Rule ids are not kept. Unknown properties are ignored; missing
ones take the DTO defaults. A document is at most `ConfigDocuments.MaxDocumentLength` characters (the command pipe's line
limit is sized for it).

**Versioning.** `Parse` checks `Format` and `SchemaVersion` before reading anything else, so a file from a newer
version is refused with "made by a newer version of ProGuard" rather than half-read. When the format changes in a way
an older reader would get wrong, raise `ConfigDocument.CurrentSchemaVersion` and add the upgrade from the previous
version in `ConfigDocuments.Parse`. Adding a setting or a rule field needs no new version: older files simply lack it.
