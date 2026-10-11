# Driving the real UI without touching the desktop

What docs/volt-verification.md was run with. The UI under test runs on a separate Windows desktop of the signed-in
session, and a UI Automation client on that same desktop drives it with Invoke, Value, Toggle, SelectionItem,
ExpandCollapse and RangeValue patterns. Nothing appears on the user's screen and no mouse or keyboard input is sent.

| File | What it is |
|---|---|
| `rundesk.cs` | `rundesk.exe <desktop> [-nowait] <command line>`: creates the desktop if needed and runs a process on it. With `-nowait` it prints the PID (and waits 8 s so the desktop survives until the process attaches); otherwise it waits and relays stdout. |
| `uia.ps1` | Helpers: `Get-PgWindow`, `Go-Page`, `Press`/`Click-Name`/`Click-Id`, `Choose` (choice lists), `Type-In`, `Switch-To`, `Open-Section`, `Select-Proc`, `Toasts`/`Wait-Toast`, `Texts`, `Dump`, `Probe`. |
| `drive.ps1`, `d.ps1` | `drive.ps1 my-check.ps1` runs a script on the `PgTest` desktop with `uia.ps1` loaded. |
| `probe.cs` | `probe.exe <pid>`: priority, affinity, I/O priority, memory priority, EcoQoS, priority boost, CPU Sets and GPU priority as Windows reports them. |
| `burner.cs` | `burner.exe <threads> <busy s> <total s> [marker]`: test load; copy it under the names your rules match. |
| `pipe.ps1` | One request to the engine pipe as the current user: `pipe.ps1 -Command GetSettings`, `pipe.ps1 -Type GetLogs -Payload '{"After":0}'`. |

Setup (the work folder holds binaries, the UI's PID and shots; default `work\` next to these files, or
`$env:PROGUARD_UIA_WORK`):

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
New-Item -ItemType Directory -Force work\bin | Out-Null
& $csc -nologo -out:work\bin\rundesk.exe rundesk.cs
& $csc -nologo -out:work\bin\probe.exe probe.cs
& $csc -nologo -out:work\bin\burner.exe burner.cs
work\bin\rundesk.exe PgTest -nowait "<publish folder>\ProGuard.UI.exe" | Set-Content work\ui.pid
```

Then write a check, e.g. `check.ps1`:

```powershell
$w = Get-PgWindow
Go-Page $w 'PROCESSES'
Select-Proc $w 'notepad' | Out-Null
Click-Name $w 'PRIORITY BELOW NORMAL'; Wait-Toast $w
```

and run `.\drive.ps1 check.ps1`.

Limits:
- Nothing composites a second desktop, so `Shot` (PrintWindow) returns black there; read the UI through `Texts`/`Dump`.
- A UI on that desktop has no foreground window to report, so the engine sees no foreground process: ProBalance can
  then restrain whatever the user is really using. Turn ProBalance off while such a UI is the only one running.
- No tray icon (there is no taskbar on that desktop), and context menus need real input.
- An elevated UI needs an elevated client (UIPI): start both from an elevated shell.
