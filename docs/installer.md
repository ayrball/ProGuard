# Installer

ProGuard ships as a per-machine x64 MSI built with WiX Toolset v5 (`installer/`), plus a portable zip. Users' install,
upgrade and uninstall steps are in [installer/portable/README.txt](../installer/portable/README.txt), which also ships in
the zip.

## Building

```powershell
.\installer\build.ps1                    # version: the v* tag on HEAD, else 0.0.<commit count>
.\installer\build.ps1 -Version 1.2.0 -Portable
```

Only the .NET 10 SDK is needed; WiX comes from NuGet. Output goes to `artifacts\installer\`:

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

**File properties.** The exes' version resource comes from `Directory.Build.props`: company and publisher `ayrball`
(the MSI's Manufacturer), product `ProGuard`, `Copyright © 2026 ayrball`, and a description per exe ("ProGuard Engine
Service", "ProGuard", "ProGuard Session Agent"). The file version is the numeric part of `-Version` plus `.0`
(`1.2.3.0`); the product version is the whole string, plus the commit.

**Bundled runtime.** The exes are self-contained, so each carries the .NET runtime of the SDK that published it.
`build.ps1` prints that version for each exe (from the runtime packs named in the publish's `deps.json`), and with
`-MinRuntime 10.0.15` it fails if any is older. The release workflow passes the latest .NET 10 runtime from
[Microsoft's release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json), so a
release never ships a runtime with known, already-fixed vulnerabilities. A local build has no minimum unless you pass
one.

**Packages.** NuGet restores only from nuget.org (`nuget.config`) and against the committed `packages.lock.json` of
each project, the wixproj included; in CI a restore that would change a lock file fails (NU1004). After changing a
package reference, run `dotnet restore ProGuard.sln` and `.\installer\build.ps1` once, and commit the changed lock
files. Audit covers transitive packages, and a high or critical advisory (NU1903, NU1904) fails the build. The
single-file analyzer is off: it would add an SDK-versioned package to the lock files (see `Directory.Build.props`).

**Notices.** `files\` gets `LICENSE.txt`, `NOTICE.txt` and `THIRD-PARTY-NOTICES.txt`; the last is the repository's
`THIRD-PARTY-NOTICES.txt` with the bundled runtime pack's own `THIRD-PARTY-NOTICES.TXT` appended. All three go into the
MSI and the zip.

## Releases

Releases are built by CI, not by hand: pushing a `v*` tag runs `.github/workflows/release.yml`, which waits for the
owner's approval (the `release` environment, which only `v*` tags may use) and then:

1. publishes and stages the exes, unsigned, with `build.ps1 -Step Stage -MinRuntime <latest .NET 10 runtime>`;
2. signs the exes with `sign.ps1` (only when `PROGUARD_SIGNING` is set);
3. builds the MSI and the zip from those files with `build.ps1 -Step Package -Portable`;
4. signs the MSI (likewise);
5. writes `SHA256SUMS.txt` and attaches everything to the GitHub Release.

Only steps 2 and 4 are given the signing variables and secrets. A build from a developer machine is for testing; it
carries whatever runtime that machine's SDK has.

## What the MSI does

| | |
| --- | --- |
| Program files | `C:\Program Files\ProGuard`: `ProGuard.Engine.exe`, `ProGuard.UI.exe`, `ProGuard.SessionAgent.exe` (the engine starts it from its own folder), native libraries, `appsettings.json`, `LICENSE.txt`, `NOTICE.txt`, `THIRD-PARTY-NOTICES.txt` |
| Service | `ProGuardEngine`, "ProGuard Engine", LocalSystem, automatic start, same description as `install-service.ps1`. Recovery: restart after 5 s, 10 s, then every 60 s; the count resets after a day. It is set with the same `sc.exe failure` command as the script, because Windows Installer's own `MsiServiceConfigFailureActions` fails with access denied (error 1939) for restart actions. Started at the end of the install, which fails and rolls back if the engine doesn't start. |
| Start menu | "ProGuard" (all users) → `ProGuard.UI.exe` |
| Start at sign-in | Optional, on by default: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `ProGuard.UI` = `"C:\Program Files\ProGuard\ProGuard.UI.exe" --tray` |
| Data | `%ProgramData%\ProGuard` belongs to the engine. The MSI creates it (and `logs`) with a protected DACL, SYSTEM and Administrators full control and nothing for Users (`PermissionEx Sddl`), and It also makes Administrators the owner (`O:BA`), so an installed folder is trusted whoever created it first. The engine checks and reapplies that on every start (`DataDirectoryGuard`): a folder that is a link or owned by anyone else is renamed aside, not used, and an entry inside it that is a link, hard-linked or someone else's is moved out. If that can't be done (someone holds a file there open), the engine starts with a fresh `ProGuard.<n>` folder beside it instead and logs that loudly; `REMOVE_DATA` only removes `ProGuard` itself. Its files are never removed by the MSI, except on uninstall with `REMOVE_DATA=1`. |

Properties: `START_AT_SIGNIN=0|1` (default 1), `START_AS_ADMIN=0|1` (default 1), `REMOVE_DATA=1` (uninstall only). The
dialogs set the same properties: Options (install) has the start-at-sign-in and administrator-rights checkboxes; Remove (in
the maintenance dialogs) has the delete-data checkbox.

**Start at sign-in without a UAC prompt.** While the window runs elevated and "Always start as administrator" is on,
it moves start-at-sign-in from the Run value to a per-user scheduled task, `\ProGuard\Start at sign-in <user SID>`
(`SignInStartup`). The task runs `ProGuard.UI.exe --tray` at that user's sign-in with their highest rights, so there's no
UAC prompt. It has no time limit and normal priority (scheduled tasks otherwise run below normal and stop after three
days). Every elevated start reconciles the two: an upgrade that writes the Run value again has it removed, and turning
"Always start as administrator" off moves start-at-sign-in back to the Run value. The first elevated start after
installing (the finish page's "Open ProGuard now", after its UAC prompt) creates the task, so the first sign-in is already
prompt-free. Opening ProGuard from the Start menu still asks. On uninstall, `RemoveSignInTasks` removes every task under
`\ProGuard\` for all users, as LocalSystem; the empty folder may stay in Task Scheduler.

`START_AS_ADMIN` writes `HKLM\Software\ProGuard`, value `StartAsAdministrator` (DWORD), the machine-wide default for the
UI's "Always start as administrator". A copy handed out then starts elevated (after a UAC prompt) without its users
looking for "Restart as administrator". Each user's own choice on the System page is written to the same name under HKCU
and wins. Upgrading keeps the installed value (an earlier 0 stays 0), and uninstalling removes it.

### Why start-at-sign-in uses HKCU, not HKLM

The UI already has a "start with Windows" switch on its System page (`RunKeyStartup`), and it reads and writes this
same HKCU value. Using it means the installer and the UI always agree: the switch shows what the installer did, and a
user can turn it off without being an administrator. An HKLM Run value would start the tray for every user but would be
invisible to that switch (it would show "off" while ProGuard still started) and only an administrator could remove it.
Starting the tray isn't needed for ProGuard to work, since the engine is a service; it's a per-user preference, so it
belongs to the user. The trade-off: the installer sets it only for the user who runs the installer. Other users turn it
on in the UI.

On upgrade the current state is kept: if the value is gone (the user turned it off), the new version doesn't add it
back. On uninstall the value is removed for the user who uninstalls, whoever created it. The engine's uninstall cleanup (below) also
removes it from every other user signed in at the time, where it starts this installation's ProGuard.UI.exe. Users who
aren't signed in keep a Run value that points at a deleted exe; Windows ignores it.

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

Before the files go, the `CleanupUninstall` custom action runs `ProGuard.Engine.exe --cleanup-uninstall` as LocalSystem
(deferred, not impersonated, on removal only and never on an upgrade; its result is ignored). It removes what the engine
and UI wrote outside the folders Windows Installer tracks: every Image File Execution Options `PerfOptions` key marked
`ProGuardManaged`; the graphics preferences rules wrote to users' `UserGpuPreferences` (from the engine's record of them,
`gpu-preferences.json`, and only where the value still holds what ProGuard wrote); the `ProGuard.UI` Run value of every
loaded user hive that starts this installation; the tasks in Task Scheduler's `\ProGuard\` folder (through its COM
interface); and any CPU limit a crashed engine left on a running process. The hives of users who aren't signed in aren't
loaded, so their graphics preferences and Run values stay.


## Code signing

Off. `installer/sign.ps1` is the hook: `build.ps1` calls it for the exes before packaging them and for the MSI
afterwards. With `PROGUARD_SIGNING` unset it prints that signing is off and returns. Set it to `pfx`,
`trusted-signing` (Azure Artifact Signing, formerly Trusted Signing; `artifact-signing` is accepted too) or `signpath`
and provide that provider's variables (listed at the top of `sign.ps1`) to switch it on; after signing, it fails the
build if a file still has no signature.

- Artifact Signing uses signtool with Microsoft's dlib from the `Microsoft.ArtifactSigning.Client` NuGet package (the
  older `Microsoft.Trusted.Signing.Client` is unlisted). The release workflow pins its version and the SHA-512 of the
  `.nupkg`. The app registration needs the "Artifact Signing Certificate Profile Signer" role. The variable names keep
  the `PROGUARD_SIGN_TS_` prefix.
- SignPath uses the `SignPath` PowerShell module, pinned to one version (4.4.6) and refused unless SignPath GmbH's
  Authenticode signature on it is valid.

In CI, `.github/workflows/release.yml` reads the repository variable `PROGUARD_SIGNING` and gives the matching
variables and secrets to its two signing steps only (see [Releases](#releases)), so turning signing on for releases
needs no workflow change: add the secrets (preferably as secrets of the `release` environment), then set the variable.
PR builds are never signed. CI checks the `pfx` route on every PR with a throw-away self-signed certificate on a copy of
one exe. Artifact Signing and SignPath are untested until there's an account.

## Testing an install

Never on a machine where a dev engine is running: both use the same pipe names. From an elevated prompt:

```powershell
msiexec /i artifacts\installer\ProGuard-0.0.1-x64.msi /qn /l*v install.log
Get-Service ProGuardEngine; sc.exe qfailure ProGuardEngine
msiexec /i artifacts\installer\ProGuard-0.0.2-x64.msi /qn /l*v upgrade.log
msiexec /x artifacts\installer\ProGuard-0.0.2-x64.msi /qn /l*v uninstall.log
```

When the prompt descends from the Claude desktop app (an MSIX package), its registry is virtualized: it doesn't see
the HKCU values the MSI writes (the start-at-sign-in Run value), and its own HKCU writes don't reach the real
registry. Check those from a process started through WMI instead; see
[installer-verification.md](installer-verification.md#start-at-sign-in-the-installer-was-right-the-test-saw-a-different-registry).

Results of the last full check are in [installer-verification.md](installer-verification.md).
