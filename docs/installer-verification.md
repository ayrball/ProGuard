# Installer verification

The MSI from `installer/build.ps1`, installed, upgraded and uninstalled on the dev machine (Windows 11 Pro 26200) on
2026-10-16 with `msiexec /qn /l*v` from one elevated script per run. The UI checks ran unelevated: the installed
`ProGuard.UI.exe` on a separate desktop (`tools/volt-uia`), read through UI Automation, with no mouse or keyboard
input. The dev engine, UI and session agent that were running were stopped first. `%ProgramData%\ProGuard` was backed
up first and restored after the run that deleted it. Process Lasso wasn't installed.

## Run 1: found a bug

Install failed with 1603: `Error 1939. Service 'ProGuard Engine' (ProGuardEngine) could not be configured`. Windows
Installer's `MsiServiceConfigFailureActions` gets access denied (error 5) setting restart actions, which is the problem
behind WiX's WIX1149 warning. Rollback left nothing behind. Fix: the recovery actions are now set by the same
`sc.exe failure` command as `install-service.ps1`, run between InstallServices and StartServices, with a failure
rolling back.

## Run 2: install 0.0.1, upgrade to 0.0.2, uninstall, script migration, REMOVE_DATA

| Check | Result |
| --- | --- |
| Install 0.0.1 with a fake `HKLM\SOFTWARE\ProcessLasso\Install_Dir` | exit 0; log has `PROCESS_LASSO_FOUND` = 1; install went ahead; fake key deleted after |
| Service | `ProGuardEngine`, "ProGuard Engine", Running, Automatic, LocalSystem, `"C:\Program Files\ProGuard\ProGuard.Engine.exe"`, description as in install-service.ps1 |
| `sc qfailure` | reset 86400 s; RESTART 5000 / 10000 / 60000 ms |
| Files | engine, UI, session agent, 6 native DLLs, appsettings.json, LICENSE.txt, NOTICE.txt; no `*.runtimeconfig.json` |
| Start menu | `ProGuard.lnk` (all users) → `C:\Program Files\ProGuard\ProGuard.UI.exe` |
| Session agent | started by the service from `C:\Program Files\ProGuard` in session 1; foreground reporter connected 130 ms later |
| UI, unelevated | **ENGINE ONLINE · STANDARD RIGHTS · UP 4m 11s** |
| ARP | one "ProGuard" entry, 0.0.1, publisher ayrball |
| Upgrade to 0.0.2 (UI left running) | exit 0; Process Lasso not detected; engine PID changed and service Running; one ARP entry, 0.0.2; engine exe version 0.0.2; running UI closed; `proguard.db` same file (creation time unchanged); recovery actions kept; session agent restarted |
| UI after upgrade | **ENGINE ONLINE · STANDARD RIGHTS · UP 0m 37s** |
| Uninstall (UI left running) | exit 0; service, install folder, shortcut and ARP entry gone; no ProGuard process left; `%ProgramData%\ProGuard` kept |
| `install-service.ps1`, then the MSI over it with `START_AT_SIGNIN=0` | exit 0; service now the MSI's path; `Program Files\ProGuard\Engine` removed; service Running |
| `install-service.ps1` while the MSI is installed | refuses, exit 1; service untouched |
| Uninstall with `REMOVE_DATA=1` | exit 0; `%ProgramData%\ProGuard` deleted (restored from the backup afterwards) |

Two installer bugs came out of this run and were fixed:

- After the MSI replaced a script install, uninstalling left an empty `C:\Program Files\ProGuard`. Windows Installer
  only removes folders it created, and this one already existed. The package now has a `RemoveFolder` for it.
- The process-stop action's PowerShell command exited 1 when one of the two process names wasn't running. That was
  harmless (the result is ignored), but noisy in the log. It now ends with `exit 0`.

## Run 3: the fixes (0.0.3)

`install-service.ps1` first, with the empty `C:\Program Files\ProGuard` from run 2 still there, then the MSI
(default options), then uninstall: 15 of 15 checks passed. The script service was replaced, `Engine\` was removed,
recovery was 5000/10000/60000, the session agent ran from Program Files, the MSI wrote the Run value, and on uninstall
everything went (including the folder that existed before), the process stop returned 0, there was no ARP entry and
the data was kept.

## Start at sign-in: the installer is right, but something else changes the value

The logs show the MSI doing what it should with `HKCU\...\Run\ProGuard.UI`:

- install with `START_AT_SIGNIN=1`: `RegAddValue(Name=ProGuard.UI,Value="C:\Program Files\ProGuard\ProGuard.UI.exe" --tray)`
- upgrade: `EXISTING_RUN_VALUE` read, `KeepStartAtSignInOff` run only when it is empty
- uninstall: `RegRemoveValue(Name=ProGuard.UI)`, and the `reg delete` safety net then finds nothing left

But while ProGuard was installed, the value repeatedly changed a few seconds after the installer or I set it.
Something removed it about 5 s after the install wrote it. Something added it back about 6 s after I deleted it. It
reappeared after uninstalls had removed it. Each time, the data was exactly what the UI's `RunKeyStartup.Enable()`
writes. It also happened at moments when no UI was running from Program Files. Once ProGuard was uninstalled, the
value stayed as set: present for 90 s in one check, absent for 120 s in another. I couldn't find the writer. Other
sessions were running `dotnet test` and dev builds on the machine at the same time. The installer-side checks that
depend on this value ("Run value", "stays off", "gone after uninstall") are therefore judged from the MSI logs, not
from the registry afterwards. Follow-up: find what writes this value while the engine runs.

## After the runs

No ProGuard service, `C:\Program Files\ProGuard`, Start menu shortcut, ARP entry, ProGuard process, Run value or fake
Process Lasso key. `%ProgramData%\ProGuard` is kept, as designed. Its database was used by the installed engine during
run 3, so it differs from the pre-test backup in `E:\projects\proguard-wt\installer-test\ProgramData-ProGuard-backup`.
The dev engine and UI that were running before the test weren't restarted.
