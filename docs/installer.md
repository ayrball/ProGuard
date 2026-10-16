# Installer

ProGuard ships as a per-machine x64 MSI built with WiX Toolset v5 (`installer/`), plus a portable zip. Users' install,
upgrade and uninstall steps are in [installer/portable/README.txt](../installer/portable/README.txt), which also ships in
the zip.

## Building

```powershell
.\installer\build.ps1                    # version: the v* tag on HEAD, else 0.0.<commit count>
.\installer\build.ps1 -Version 1.2.0 -Portable
```

Only the .NET 8 SDK is needed; WiX comes from NuGet. Output goes to `artifacts\installer\`:

| File | What |
| --- | --- |
| `ProGuard-<version>-x64.msi` | the installer |
| `ProGuard-<version>-portable-win-x64.zip` | with `-Portable`: the same exes, `Start ProGuard.cmd`, `Stop ProGuard.cmd`, README.txt |
| `files\` | exactly what the MSI installs into `Program Files\ProGuard` |

`build.ps1` publishes the engine, UI and session agent self-contained for win-x64, one exe each, and stages them in one
folder. Two details there:

- The engine's publish output contains a framework-dependent `ProGuard.SessionAgent.exe` (from its project reference),
  which needs .NET installed. It is dropped; the session agent's own self-contained publish replaces it.
- Native libraries (`e_sqlite3.dll`, WPF's `*_cor3.dll`) stay next to the exes rather than inside them
  (`IncludeNativeLibrariesForSelfExtract` is off). A single-file exe extracts embedded native libraries to a temp folder
  and loads them from there, and the engine runs as LocalSystem: it must only load code from Program Files, which only
  administrators can change. The exes aren't compressed either, because the MSI cabinet and the zip compress them, and
  an uncompressed exe starts faster and doesn't hold a decompressed copy in memory.

`installer/ProGuard.Installer.wixproj` only packages `files\`, so it isn't in `ProGuard.sln`. `dotnet build` of the
solution is unaffected.

**Version.** `-Version` takes `1.2.3`, `v1.2.3` or `1.2.3-beta.1`. The exes get the whole string; the MSI gets
`1.2.3`, because Windows Installer versions are numeric and upgrades compare only the first three fields. The wixproj
takes it as the `ProductVersion` MSBuild property. CI builds use `0.0.<run number>`; releases use the tag.

## What the MSI does

| | |
| --- | --- |
| Program files | `C:\Program Files\ProGuard`: `ProGuard.Engine.exe`, `ProGuard.UI.exe`, `ProGuard.SessionAgent.exe` (the engine starts it from its own folder), native libraries, `appsettings.json`, `LICENSE.txt`, `NOTICE.txt` |
| Service | `ProGuardEngine`, "ProGuard Engine", LocalSystem, automatic start, same description as `install-service.ps1`. Recovery: restart after 5 s, 10 s, then every 60 s; the count resets after a day. Started at the end of the install, which fails and rolls back if the engine doesn't start. |
| Start menu | "ProGuard" (all users) → `ProGuard.UI.exe` |
| Start at sign-in | Optional, on by default: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `ProGuard.UI` = `"C:\Program Files\ProGuard\ProGuard.UI.exe" --tray` |
| Data | `%ProgramData%\ProGuard` belongs to the engine, which creates it. The MSI doesn't install or remove it, except on uninstall with `REMOVE_DATA=1`. |

Properties: `START_AT_SIGNIN=0|1` (default 1), `REMOVE_DATA=1` (uninstall only). The dialogs set the same properties:
Options (install) has the start-at-sign-in checkbox; Remove (in the maintenance dialogs) has the delete-data checkbox.

### Why start-at-sign-in uses HKCU, not HKLM

The UI already has a "start with Windows" switch on its System page (`RunKeyStartup`), and it reads and writes this
same HKCU value. Using it means the installer and the UI always agree: the switch shows what the installer did, and a
user can turn it off without being an administrator. An HKLM Run value would start the tray for every user but would be
invisible to that switch (it would show "off" while ProGuard still started) and only an administrator could remove it.
Starting the tray isn't needed for ProGuard to work, since the engine is a service; it's a per-user preference, so it
belongs to the user. The trade-off: the installer sets it only for the user who runs the installer. Other users turn it
on in the UI.

On upgrade the current state is kept: if the value is gone (the user turned it off), the new version doesn't add it
back. On uninstall the value is removed for the user who uninstalls, whoever created it. Other users who turned it on
in the UI keep a Run value that points at a deleted exe; Windows ignores it.

### Upgrades

Major upgrade with a fixed UpgradeCode (`276E7864-81FA-4F30-AC57-41896E15D3C6`; never change it) and a new
ProductCode per build. `AllowSameVersionUpgrades` is on: two builds with the same version must still replace each other,
or both would try to own the service. The old version is removed after `InstallInitialize`, inside the new install's
transaction, so a failed upgrade rolls back to it. Downgrades are refused.

During an upgrade the old package stops and deletes the service, ends any UI and leftover session agent running from
`Program Files\ProGuard` (in every session, matched by path so a portable copy elsewhere is left alone) and removes its
files; the new package installs its files and starts the service. `%ProgramData%\ProGuard` isn't touched. UI windows
aren't reopened after a quiet upgrade; the Finish page of an interactive one offers to open ProGuard.

Installing the MSI over a service installed by `install-service.ps1` replaces it: the MSI deletes the existing
`ProGuardEngine` service before creating its own and removes `Program Files\ProGuard\Engine`.
`install-service.ps1` refuses to run while the MSI is installed.

### Uninstall

Stops and deletes the service, ends the UI and session agents (as above), removes the program files, the Start menu
shortcut and the uninstalling user's Run value. Keeps `%ProgramData%\ProGuard` unless `REMOVE_DATA=1`
(`msiexec /x ProGuard.msi /qn REMOVE_DATA=1`, or the checkbox in the Remove dialog).

### Process Lasso

The MSI looks for `HKLM\SOFTWARE\ProcessLasso\Install_Dir` (64- and 32-bit views) and the `ProcessGovernor` service.
If it finds either, it sets `PROCESS_LASSO_FOUND=1` and the Options dialog shows a note that the two will fight. The
install carries on either way, and a quiet install only records the property in its log. Process Lasso is never
changed or removed.

## Code signing

Off. `installer/sign.ps1` is the hook: `build.ps1` calls it for the exes before packaging them and for the MSI
afterwards. With `PROGUARD_SIGNING` unset it prints that signing is off and returns. Set it to `pfx`,
`trusted-signing` or `signpath` and provide that provider's variables (listed at the top of `sign.ps1`) to switch it on;
after signing, it fails the build if a file still has no signature.

In CI, `.github/workflows/release.yml` passes the repository variable `PROGUARD_SIGNING` and the matching secrets to
the build step, so turning signing on for releases needs no workflow change: add the secrets, then set the variable.
PR builds are never signed. CI checks the `pfx` route on every PR with a throw-away self-signed certificate on a copy of
one exe. `trusted-signing` and `signpath` are untested until there's an account.

## Testing an install

Never on a machine where a dev engine is running: both use the same pipe names. From an elevated prompt:

```powershell
msiexec /i artifacts\installer\ProGuard-0.0.1-x64.msi /qn /l*v install.log
Get-Service ProGuardEngine; sc.exe qfailure ProGuardEngine
msiexec /i artifacts\installer\ProGuard-0.0.2-x64.msi /qn /l*v upgrade.log
msiexec /x artifacts\installer\ProGuard-0.0.2-x64.msi /qn /l*v uninstall.log
```

Results of the last full check are in [installer-verification.md](installer-verification.md).
