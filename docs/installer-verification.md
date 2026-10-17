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

## Start at sign-in: the installer was right; the test saw a different registry

The MSI logs show it handling `HKCU\...\Run\ProGuard.UI` as designed:

- install with `START_AT_SIGNIN=1`: `RegAddValue(Name=ProGuard.UI,Value="C:\Program Files\ProGuard\ProGuard.UI.exe" --tray)`
- upgrade: `EXISTING_RUN_VALUE` read, `KeepStartAtSignInOff` run only when it is empty
- uninstall: `RegRemoveValue(Name=ProGuard.UI)`, and the `reg delete` safety net then finds nothing left

During the runs the value seemed to change by itself: it was gone right after the install wrote it, back right after I
deleted it, and present after uninstalls had removed it. Nothing was changing it. The test looked at the registry
through a different view from the one Windows Installer uses:

- The test shells, the elevated scripts started from them, and the UIs they launched all descend from the Claude
  desktop app, which is an MSIX package (`Claude_pzs8sxrjxfjjc`). Its processes get the package's registry
  virtualization: writes under `HKCU\Software` go to a private hive
  (`%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\SystemAppData\Helium\User.dat`), and deleting a value there hides the
  real one. Writes under HKLM are refused, which is why `New-Item HKLM:\SOFTWARE\ProcessLasso` failed with "Requested
  registry access is not allowed" in run 1.
- The Windows Installer service, and the `reg delete` it starts, use the real registry.

Checked on 2026-10-17. With the value set from a test shell, `reg query` in that shell shows it, but the same query
from a process started through WMI (`Win32_Process.Create`, outside the app's process tree, same user) reports it
missing. The same is true with the shell's command sandbox off. The real registry after the runs: no `ProGuard.UI`
Run value and no `HKLM\SOFTWARE\ProcessLasso`.

The supposed flips are exactly the gaps between the two views: the MSI wrote and removed the real value, and the test
read and wrote the overlay. So the installer's start-at-sign-in handling is verified by its logs, and the "Run value",
"stays off" and "gone after uninstall" failures in run 2 were measurement errors. The UI's own `RunKeyStartup` is fine
too: nothing in ProGuard writes the value except the System page switch. It didn't change when the whole test suite
(twice, with the value absent and then present), the Preview tool (`--mode all`) or a Restart Manager shutdown and
restart of the UI ran against it.

When testing anything that touches the registry from a Claude session, read the real state from a process started
through WMI, for example
`Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{ CommandLine = 'cmd /c reg query ... > out.txt' }`.
## After the runs

No ProGuard service, `C:\Program Files\ProGuard`, Start menu shortcut, ARP entry, ProGuard process, Run value or fake
Process Lasso key. `%ProgramData%\ProGuard` is kept, as designed. Its database was used by the installed engine during
run 3, so it differs from the pre-test backup in `E:\projects\proguard-wt\installer-test\ProgramData-ProGuard-backup`.
The dev engine and UI that were running before the test weren't restarted.
