# ProGuard

[![CI](https://github.com/ayrball/ProGuard/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/ayrball/ProGuard/actions/workflows/ci.yml)

A Windows process manager in the spirit of Process Lasso: an engine that runs as a Windows service (ProBalance,
rules, Watchdog, SmartTrim, IdleSaver, power plan switching) and a WPF UI that talks to it over a named pipe.

- `src/ProGuard.Engine` — the engine (Windows service, or a console process for development)
- `src/ProGuard.UI` — the WPF UI
- `src/ProGuard.Core`, `src/ProGuard.Shared` — engine logic and the UI/engine contract
- `tools/ProGuard.Preview` — renders the UI to PNG against a stand-in engine
- `tests/ProGuard.Tests` — xUnit tests
- `installer/` — the MSI (WiX v5) and the portable zip
- `docs/` — schema, installer, service verification, and notes on how Process Lasso does things

## Install

Download `ProGuard-<version>-x64.msi` from [Releases](https://github.com/ayrball/ProGuard/releases) and run it
(64-bit Windows 10 or 11; no .NET install needed). It installs ProGuard in `C:\Program Files\ProGuard`, runs the
engine as the `ProGuardEngine` service and adds ProGuard to the Start menu. Tick or untick "Start ProGuard in the
notification area when I sign in" (on by default; you can change it later in ProGuard's System page). If Process Lasso
is installed, the installer warns you, since the two fight over the same processes; it never touches Process Lasso.

Silent install from an elevated prompt: `msiexec /i ProGuard-<version>-x64.msi /qn` (add `START_AT_SIGNIN=0` to skip
the tray icon at sign-in, or `START_AS_ADMIN=0` so the window doesn't ask for administrator rights each time it starts).

**Upgrade:** run the newer MSI. It stops the service, replaces the files and starts the service again, keeping your
settings, rules and logs (`C:\ProgramData\ProGuard`) and your start-at-sign-in choice.

**Uninstall:** Settings > Apps > Installed apps > ProGuard > Uninstall. This stops and removes the service, closes
ProGuard and deletes the program files. `C:\ProgramData\ProGuard` is kept; to delete it too, run
`msiexec /x ProGuard-<version>-x64.msi /qn REMOVE_DATA=1`, or run the MSI again, choose Remove and tick
"Also delete ProGuard's settings, rules and logs".

Each release also has a portable zip, `ProGuard-<version>-portable-win-x64.zip`: unzip and run `Start ProGuard.cmd`,
nothing installed. Its README.txt covers both. How the installer works and how to build it:
[docs/installer.md](docs/installer.md).

## Requirements

Windows 10/11 and the .NET 10 SDK (pinned by `global.json`).

## Build, run, test

```powershell
dotnet build ProGuard.sln
.\run.ps1            # starts the engine (unless already running), then the UI; run elevated to manage system processes
.\run.ps1 -Preview   # renders every page and dialog to artifacts\preview\ without a real engine
dotnet test ProGuard.sln
```

Two tests skip unless the test host is elevated or runs as LocalSystem. To install a development build of the engine as
a service, see `src/ProGuard.Engine/Installer/install-service.ps1` and `docs/service-verification.md` (it refuses to
run while the MSI is installed).

```powershell
.\installer\build.ps1 -Version 1.2.0 -Portable   # MSI and portable zip in artifacts\installer\
```

## CI

Every pull request and push to `main` builds in Release, runs the tests on `windows-latest`, and uploads the UI preview
PNGs as the `ui-preview` artifact. Test results (TRX, plus dumps if a test hangs for 5 minutes) are uploaded as
`test-results` when the tests fail. It also builds the MSI (version `0.0.<run number>`, unsigned) and uploads it as the
`installer` artifact.

Pushing a `v*` tag (`v1.2.0`, or `v1.2.0-beta.1` for a pre-release) runs `.github/workflows/release.yml`: it builds the
MSI and the portable zip at that version and attaches both to a GitHub Release with generated notes. Code signing is
wired in but off; see [docs/installer.md](docs/installer.md#code-signing).

## License

Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE). You are free to use, modify and share this for personal, hobby, educational, research and other noncommercial purposes.

**Commercial use** (selling it, building a paid product or service on it, or using it in a business) needs a separate license from the author. Commercial licenses are available: get in touch via [github.com/ayrball](https://github.com/ayrball) to discuss terms.
