# What ProGuard changes on a system

This page is for antivirus and EDR analysts, administrators and users who want to know exactly what ProGuard touches.
It lists everything ProGuard writes or changes outside its own window, with the source file that does it. Unless a
section says otherwise, it describes the MSI install (`installer/Package.wxs`). The portable zip installs nothing: its
engine runs as an ordinary process for as long as it is open.

ProGuard makes no network connections, installs no driver, injects no code into other processes and hooks nothing.
It changes other processes only through the documented Win32 and Native API calls listed below.

## Contents

- [The service](#the-service)
- [Installer custom actions](#installer-custom-actions)
- [Files and folders](#files-and-folders)
- [Registry](#registry)
- [Scheduled tasks](#scheduled-tasks)
- [Named pipes and other kernel objects](#named-pipes-and-other-kernel-objects)
- [Privileges](#privileges)
- [Processes ProGuard starts](#processes-proguard-starts)
- [What ProGuard does to other processes](#what-proguard-does-to-other-processes)
- [Power plan, core parking and timer resolution](#power-plan-core-parking-and-timer-resolution)
- [What uninstall removes](#what-uninstall-removes)

## The service

| | |
| --- | --- |
| Name | `ProGuardEngine` |
| Display name | ProGuard Engine |
| Executable | `C:\Program Files\ProGuard\ProGuard.Engine.exe` |
| Account | LocalSystem |
| Start type | Automatic |
| Recovery | Restart after 5 s, then 10 s, then 60 s for every later failure. The failure count resets after a day (86400 s) without one. |

The recovery actions are set by `ProGuard.Engine.exe --configure-recovery` through `ChangeServiceConfig2`
(`src/ProGuard.Engine/ServiceRecovery.cs`). The engine needs LocalSystem to change processes in every session, to
start the session agent in each user's session, and to write the machine-wide settings below. Every request a user
sends it is checked against that user's own rights first (`src/ProGuard.Engine/IPC/IpcAuthorization.cs`,
`RuleAuthorization.cs`).

## Installer custom actions

The MSI starts no shell, script host or system tool (no `cmd.exe`, PowerShell, `reg.exe`, `sc.exe` or `schtasks.exe`).
Its custom actions all run `ProGuard.Engine.exe` itself, as LocalSystem:

| Custom action | Command | When | What it does |
| --- | --- | --- | --- |
| `ConfigureEngineRecovery` | `--configure-recovery` | Install, repair (after `InstallServices`) | Sets the service's recovery actions (above). A failure rolls the install back. |
| `StopProGuardClients` | `--stop-clients` | Uninstall, repair, and the old version's removal during an upgrade (after `StopServices`) | Ends `ProGuard.UI.exe` and `ProGuard.SessionAgent.exe` processes whose image is inside the install folder, in every session. Each window is asked to quit through its quit event first; whatever is still running 5 s later is terminated (`ClientShutdown.cs`). |
| `CleanupUninstall` | `--cleanup-uninstall` | Uninstall only, never an upgrade (before `RemoveFiles`) | See [What uninstall removes](#what-uninstall-removes) (`UninstallCleanup.cs`). |

Besides those, the MSI uses WiX's own `RemoveFoldersEx` action (from `Wix4UtilCA_X64`) to delete the leftover
`Program Files\ProGuard\Engine` folder of a script install, and `%ProgramData%\ProGuard` on uninstall with
`REMOVE_DATA=1`.

## Files and folders

| Path | What | Who writes it |
| --- | --- | --- |
| `C:\Program Files\ProGuard\` | `ProGuard.Engine.exe`, `ProGuard.UI.exe`, `ProGuard.SessionAgent.exe`, native libraries, `appsettings.json`, license files | The MSI |
| Start menu (all users) | `ProGuard` shortcut to `ProGuard.UI.exe` | The MSI |
| `%ProgramData%\ProGuard\` | The engine's data folder. Created with a protected DACL: SYSTEM and Administrators full control, nothing for other users, owner Administrators (`O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)`). The engine reapplies this on every start (`src/ProGuard.Engine/Security/DataDirectoryGuard.cs`). | The MSI, the engine |
| `%ProgramData%\ProGuard\proguard.db` | SQLite database: rules, Watchdog rules, settings, activity log | The engine |
| `%ProgramData%\ProGuard\logs\engine.log` | Text log, rolled over to `engine.1.log` at 10 MB | The engine |
| `%ProgramData%\ProGuard\power-recovery.json` | The power plan to restore if the engine dies while a feature holds another plan | The engine |
| `%ProgramData%\ProGuard\core-parking-recovery.json` | Core parking values to put back after a crash | The engine |
| `%ProgramData%\ProGuard\cpu-tuning-originals.json` | Each power plan's processor values before ProGuard first changed them, for Revert | The engine |
| `%ProgramData%\ProGuard\cpu-cap-recovery.json` | Hard CPU caps in force, so one a crash left on a process is lifted at the next start | The engine |
| `%ProgramData%\ProGuard\gpu-preferences.json` | The graphics preferences rules wrote to users' registries, so they can be taken out again | The engine |
| `%ProgramData%\ProGuard.<n>\` | Only if `%ProgramData%\ProGuard` can't be made safe (someone else owns it or holds a file open): a fresh folder beside it, with the same DACL | The engine |
| `%LocalAppData%\ProGuard\ui-errors.log` | The window's unhandled errors | The UI, as the user |
| `%TEMP%\ProGuard-task-<guid>.xml` | Deleted at once: the definition of the sign-in task, handed to `schtasks.exe` (see [Scheduled tasks](#scheduled-tasks)) | The UI, elevated |
| `*.proguard.json` | Configuration profiles, only where the user exports one | The UI, as the user |

## Registry

### Image File Execution Options (launch-time priorities)

`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<image>.exe\PerfOptions`, 64-bit view
(`src/ProGuard.Engine/Services/ProcessRules/LaunchPriorityRegistry.cs`).

Written only for a rule an administrator saved with "Set at launch" on. Windows then starts that program with the
rule's priorities. ProGuard writes nothing but these DWORD values, and never anything on the image key itself (no
`Debugger`, no `GlobalFlag`):

| Value | Meaning |
| --- | --- |
| `CpuPriorityClass` | 1 Idle, 2 Normal, 3 High, 5 Below Normal, 6 Above Normal |
| `IoPriority` | 0 Very Low to 3 High |
| `PagePriority` | 1 Very Low to 5 Normal (memory priority) |
| `ProGuardManaged` | Always 1. A marker Windows ignores, so ProGuard finds and removes only the keys it wrote |

A key is removed when the rule goes, when the option is turned off, or when the rule is renamed. Empty `PerfOptions`
and image keys are deleted too.

ProGuard refuses launch priorities for security products and core Windows programs, and never writes this key for
them, even if a stored rule asks (`src/ProGuard.Shared/Security/ProtectedProcessNames.cs`):

- Microsoft Defender, Defender for Endpoint and Windows Security: `MsMpEng.exe`, `MpDefenderCoreService.exe`,
  `NisSrv.exe`, `MpCmdRun.exe`, `MsSense.exe`, `SenseIR.exe`, `SenseCncProxy.exe`, `SenseNdr.exe`,
  `SecurityHealthService.exe`, `SecurityHealthSystray.exe`, `smartscreen.exe`.
- Common third-party antivirus and EDR products: Kaspersky, Bitdefender, ESET, AVG, Avast, McAfee and Trellix,
  Sophos, CrowdStrike Falcon, SentinelOne, Carbon Black, Malwarebytes, Norton and Symantec, Trend Micro, Cortex XDR,
  Cylance, Webroot, FireEye HX, Elastic, F-Secure and WithSecure, G DATA, Panda and Comodo. The exact process names are
  in `SecurityProductNames`.
- Every critical process: `csrss`, `lsass`, `lsaiso`, `wininit`, `winlogon`, `smss`, `services`, `svchost`, `dwm`,
  `fontdrvhost`, `consent`, `logonui`, `userinit`, `sihost`, `securityhealthservice`, ProGuard's own processes, and the
  kernel's pseudo-processes.

### ProGuard's own settings

| Key | Values | Who writes it |
| --- | --- | --- |
| `HKLM\Software\ProGuard` | `StartAsAdministrator` (DWORD): the default for "Always start as administrator" | The MSI |
| `HKCU\Software\ProGuard` | `StartAsAdministrator` (DWORD), `Language` (string), `RememberPriorityChanges` (DWORD): each user's own choices | The UI, as that user |

### Start at sign-in

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `ProGuard.UI` =
`"C:\Program Files\ProGuard\ProGuard.UI.exe" --tray`. The MSI writes it for the installing user when
`START_AT_SIGNIN=1` (the default). The UI's "Start with Windows" switch writes or removes the same value for its user
(`src/ProGuard.UI/Services/StartupRegistration.cs`). An elevated window with "Always start as administrator" on
removes it in favour of a scheduled task (below).

### Graphics preferences

`HKU\<user SID>\Software\Microsoft\DirectX\UserGpuPreferences`, value `<full exe path>` = `GpuPreference=N;` (string),
the value Settings > Display > Graphics writes (`src/ProGuard.Engine/Services/ProcessRules/GpuPreferenceApplier.cs`).
Written only for a rule with a GPU preference, in the registry of the user the program runs as. Other entries in the
same string are kept. Removed again when no rule wants it, but only where the value still holds what ProGuard wrote.

### Timer resolution switch

`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel`, value `GlobalTimerResolutionRequests` (DWORD 1).
Written or deleted only when an administrator turns "apply to every program" on or off on the Timer resolution
settings, on Windows 11 and Server 2022 or later (`src/ProGuard.Engine/Services/TimerResolutionService.cs`). Windows
reads it at boot.

### Service and event log

Windows Installer creates the `ProGuardEngine` service key under `HKLM\SYSTEM\CurrentControlSet\Services`. Engine
warnings and errors also go to the Application event log: .NET's generic host adds its Event Log provider on Windows
(`Host.CreateDefaultBuilder` in `src/ProGuard.Engine/Program.cs`), with the application's name, `ProGuard.Engine`, as
the source. .NET's `EventLog` registers a missing source under
`HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application` the first time it writes.

## Scheduled tasks

`\ProGuard\Start at sign-in <user SID>`: one per user, created only by an elevated ProGuard window while "Always
start as administrator" is on (`src/ProGuard.UI/Services/SignInStartup.cs`). It runs
`"C:\Program Files\ProGuard\ProGuard.UI.exe" --tray` at that user's sign-in, as that user, with their highest rights
(`RunLevel` HighestAvailable, `LogonType` InteractiveToken), with no time limit and normal priority, so the window
starts elevated without a UAC prompt. It is created only for a copy that no one but administrators can replace. The UI
creates, reads and deletes it with `schtasks.exe`. Turning "Always start as administrator" off moves start-at-sign-in
back to the Run value.

## Named pipes and other kernel objects

| Name | Direction | Who may connect | What for |
| --- | --- | --- | --- |
| `\\.\pipe\ProGuardEngine` | Request and reply | Interactive users read and write; SYSTEM, Administrators and the engine's account full control; network logons denied | The window's commands. Each request is checked against the caller's own token. |
| `\\.\pipe\ProGuardEngine.Foreground` | Into the engine only | Authenticated users write; network logons denied | Each session agent reports its desktop's foreground process and the user's idle time. Accepts nothing else. |

Clients check that the pipe's server is the engine before they send anything (`src/ProGuard.Shared/Security/PipeServerCheck.cs`).

Each window also creates, in its session's `Local\` namespace, a mutex `ProGuard.UI` and the events
`ProGuard.UI.Show` and `ProGuard.UI.Quit` (`ProGuard.UI.Elevated…` for an elevated window): one window per session, a
second launch shows the first, and the installer asks it to quit. The session agent holds `Local\ProGuard.SessionAgent`,
one per session.

## Privileges

The engine runs as LocalSystem and enables these on its own token:

| Privilege | When | Why |
| --- | --- | --- |
| `SeIncreaseBasePriorityPrivilege` | At every start | Realtime priority class and High I/O priority (`src/ProGuard.Engine/EngineWorker.cs`) |
| `SeProfileSingleProcessPrivilege` | Before a Memory Trim purge | Purging the standby list (`src/ProGuard.Engine/Services/MemoryTrimService.cs`) |
| `SeIncreaseQuotaPrivilege` | Before a Memory Trim purge | Flushing the system file cache |

It also relies on `SeTcbPrivilege`, which LocalSystem holds, for `WTSQueryUserToken`, to start the session agent and
Watchdog Launch programs as the session's user. It never enables `SeDebugPrivilege`.

## Processes ProGuard starts

- `ProGuard.SessionAgent.exe` in every signed-in session, as that session's user (`CreateProcessAsUser` with the
  user's token), kept running while the service runs. It has no window; it reports the foreground process and idle
  time over the foreground pipe.
- `ProGuard.UI.exe` at sign-in (Run value or scheduled task, above), and again with a UAC prompt (`runas`) when the
  window restarts itself as administrator.
- `schtasks.exe`, from an elevated window, to manage the sign-in task.
- A program named in a Watchdog Launch action, or a program a Keep running rule restarts, in the session and as the
  user of the process that triggered it. Only administrators may create Launch actions.

## What ProGuard does to other processes

All of these are done by the engine, on a user's request from the window or because a rule or feature the user turned
on asks for it. Nothing is ended or suspended without a request or a rule. Session, security and ProGuard processes
(the critical list above) are never changed or ended by any rule, and standard users may act only on processes they
could open themselves.

| Action | API | Started by |
| --- | --- | --- |
| Priority class | `SetPriorityClass` | Manual; rules; Responsiveness Guard (lowers busy background processes for a while); Foreground Boost (raises the foreground program to Above Normal); startup boost |
| CPU affinity | `SetProcessAffinityMask` | Manual; rules; Watchdog; Instance Spread; CPU limiter (temporarily removes cores from a busy process) |
| CPU Sets | `SetProcessDefaultCpuSets` | Rules |
| I/O priority | `NtSetInformationProcess(ProcessIoPriority)` | Manual; rules; Watchdog |
| Memory priority | `SetProcessInformation(ProcessMemoryPriority)` | Manual; rules |
| Efficiency mode (EcoQoS) | `SetProcessInformation(ProcessPowerThrottling)` | Manual; rules; optionally Responsiveness Guard |
| Priority boost off | `SetProcessPriorityBoost` | Rules |
| GPU scheduling priority | `D3DKMTSetProcessSchedulingPriorityClass` | Rules |
| Hard CPU cap | A job object per process with `JobObjectCpuRateControlInformation` (`CreateJobObject`, `SetInformationJobObject`, `AssignProcessToJobObject`) | Manual; rules. Lifted when removed, and after a crash at the next start or uninstall. |
| Working-set trim | `EmptyWorkingSet` | Manual; Watchdog; Memory Trim |
| Standby list purge | `NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)` | Manual; Memory Trim |
| File cache flush | `SetSystemFileCacheSize(-1, -1, 0)` | Manual; Memory Trim |
| Suspend and resume | `NtSuspendProcess`, `NtResumeProcess` | Manual only |
| Terminate (one process or a tree) | `TerminateProcess` | Manual; a Disallow rule (ends the program when it starts); an instance limit; Watchdog Terminate or Restart |
| Read command line | `NtQueryInformationProcess` and `ReadProcessMemory` on the process's PEB | Restarting a process with the same arguments, and the process details |

## Power plan, core parking and timer resolution

| What | API | When |
| --- | --- | --- |
| Active power plan | `PowerSetActiveScheme` | Manual; Performance Mode; Away Mode (another plan while the user is away); the load-based plan switcher. The user's plan comes back when the feature lets go, and after a crash at the next start. ProGuard creates and deletes no plans. |
| Power mode (Windows 11 slider) | `PowerSetActiveOverlayScheme` | Manual; Performance Mode (Best performance) |
| Core parking during restraint | `PowerWriteACValueIndex`/`PowerWriteDCValueIndex`, `CPMINCORES` and `CPMINCORES1` set to 100 % in the active plan | While Responsiveness Guard restrains a process, if turned on. The plan's values come back afterwards, and after a crash at the next start. |
| Processor tuning per plan | `PowerWriteACValueIndex`/`PowerWriteDCValueIndex` for core parking (`CPMINCORES`, `CPMAXCORES`, `CPCONCURRENCY`, `CPDISTRIBUTION`, `CPHEADROOM`, `CPOVERUTIL`, `CPINCREASETIME`, `CPDECREASETIME`, `CPINCREASEPOL`, `CPDECREASEPOL`, `CPPERF`, `DISTRIBUTEUTIL`) and frequency (`PROCTHROTTLEMIN`, `PROCTHROTTLEMAX`, `PERFEPP`, `PERFBOOSTMODE`, `PERFINCTHRESHOLD`, `PERFDECTHRESHOLD`, and the `…1` variants for a hybrid CPU's second core class) | Only when an administrator changes them on the CPU tuning page. The original values are recorded and Revert puts them back. A hidden setting ProGuard changes is made visible in Power Options (`PowerWriteSettingAttributes`) and hidden again once no plan is tuned. |
| Timer resolution | `NtSetTimerResolution` | When set in the settings. The request belongs to the engine's process, so Windows drops it when the engine stops. See also the registry switch above. |

## What uninstall removes

Windows Installer removes the program files, the Start menu shortcut, the service, `HKLM\Software\ProGuard` and the
Run value it wrote for the uninstalling user. `%ProgramData%\ProGuard` stays unless `REMOVE_DATA=1`.

Before the files go, `ProGuard.Engine.exe --stop-clients` ends this installation's windows and session agents, and
`ProGuard.Engine.exe --cleanup-uninstall` (uninstall only, not an upgrade) removes, each step on its own:

- every Image File Execution Options `PerfOptions` key marked `ProGuardManaged`;
- the graphics preferences rules wrote, in every user registry that is loaded, where they still hold what ProGuard wrote;
- the `ProGuard.UI` Run value of every loaded user registry, the uninstalling user's included, where it starts this
  installation's `ProGuard.UI.exe` (a portable copy's value elsewhere is left alone);
- every task in Task Scheduler's `\ProGuard\` folder, and the folder, through Task Scheduler's COM interface;
- any hard CPU cap a crashed engine left on a running process.

Left behind:

- Users who aren't signed in during the uninstall keep their Run value and graphics preferences: their registries
  aren't loaded, and ProGuard doesn't load them. Windows ignores a Run value that points at a missing file.
- `HKCU\Software\ProGuard` of every user, and `%LocalAppData%\ProGuard\ui-errors.log`.
- The `GlobalTimerResolutionRequests` switch, if an administrator turned it on.
- Power plan values changed on the CPU tuning page and not reverted; the active power plan as it is.
- The event log source registration.
