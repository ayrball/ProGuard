# ProGuard

[![CI](https://github.com/ayrball/ProGuard/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/ayrball/ProGuard/actions/workflows/ci.yml)

A Windows process manager in the spirit of Process Lasso: an engine that runs as a Windows service (ProBalance,
rules, Watchdog, SmartTrim, IdleSaver, power plan switching) and a WPF UI that talks to it over a named pipe.

- `src/ProGuard.Engine` — the engine (Windows service, or a console process for development)
- `src/ProGuard.UI` — the WPF UI
- `src/ProGuard.Core`, `src/ProGuard.Shared` — engine logic and the UI/engine contract
- `tools/ProGuard.Preview` — renders the UI to PNG against a stand-in engine
- `tests/ProGuard.Tests` — xUnit tests
- `docs/` — schema, service verification, and notes on how Process Lasso does things

## Requirements

Windows 10/11 and the .NET 8 SDK.

## Build, run, test

```powershell
dotnet build ProGuard.sln
.\run.ps1            # starts the engine (unless already running), then the UI; run elevated to manage system processes
.\run.ps1 -Preview   # renders every page and dialog to artifacts\preview\ without a real engine
dotnet test ProGuard.sln
```

Two tests skip unless the test host is elevated or runs as LocalSystem. To install the engine as a service, see
`src/ProGuard.Engine/Installer/Install-service.ps1` and `docs/service-verification.md`.

## CI

Every pull request and push to `main` builds in Release, runs the tests on `windows-latest`, and uploads the UI preview
PNGs as the `ui-preview` artifact. Test results (TRX, plus dumps if a test hangs for 5 minutes) are uploaded as
`test-results` when the tests fail.
