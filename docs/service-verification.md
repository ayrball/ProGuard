# Service deployment verification

ProGuard installed for real and checked against real processes: the engine as a LocalSystem Windows service in
session 0, the UI as the signed-in user, unelevated. Everything below was run on 2026-10-11 on the development machine
(Windows 11 Pro 26200, 16 logical CPUs, 47 GB RAM). Process Lasso 18.4's ProcessGovernor service and tray GUI were
stopped for the run and restored afterwards.

## How it was run

- `dotnet publish -c Release` for the engine and UI; `src/ProGuard.Engine/Installer/install-service.ps1` (elevated)
  installed `ProGuardEngine` from `%ProgramFiles%\ProGuard\Engine` (`sc qc`: `WIN32_OWN_PROCESS`, `AUTO_START`,
  `SERVICE_START_NAME LocalSystem`, restart-on-failure 5 s / 10 s / 60 s).
- UI started from the publish folder by the signed-in user: session 1, token `TokenElevation=0`,
  `TokenElevationType=3` (limited). `whoami /groups` for that shell: `Mandatory Label\Medium Mandatory Level`,
  `BUILTIN\Administrators ... Group used for deny only`.
- Test programs: `burner.exe` (N threads busy for S seconds, then idle) copied under the names the test rules match
  (`pgtest-*.exe`), and a windowed variant that takes the foreground. A small pipe client sent the same
  newline-delimited JSON the UI sends, from the same unelevated account (and, for administrator checks, from an
  elevated PowerShell).
- Evidence: the engine's new log (`%ProgramData%\ProGuard\logs\engine.log`), process priority/affinity/I/O/memory
  priority/EcoQoS/CPU Sets read with the Win32 APIs, `D3DKMTGetProcessSchedulingPriorityClass`, `powercfg
  /getactivescheme`, `Win32_Process` (session and command line), the registry, SQLite schema queries and memory
  counters.
- The service was installed three times: as found on `main` (15:18), after the first round of fixes (15:57: everything
  except fd48ff2, 377d338, a17ee44, 45b0884) and with the final build (16:04). Items 4–6 were measured on the second
  install; the later commits don't touch those features. Everything else below is the final build unless it says
  otherwise.

## Checklist

| # | Item | First install (`main`) | Final build |
|---|------|------------------------|-------------|
| 1 | UI ↔ command pipe; standard-user commands work; admin-only commands refused clearly | **FAIL** | PASS |
| 2 | Foreground PID and idle time from the UI; ProBalance exempts the foreground; IdleSaver | PASS (no visibility) | PASS |
| 3 | ProBalance restrains/restores on Process Lasso's timing | **FAIL** (slow) | PASS |
| 4 | Per-process rules applied at process start, GPU preference in the user's hive | **FAIL** (rules never loaded) | PASS |
| 5 | CPU Limiter affinity reduction, instance limit, Instance Balancer, startup boost | not reached (same cause) | PASS |
| 6 | Watchdog Restart / Launch / Keep Running in the user's session | not reached (same cause) | PASS |
| 7 | SmartTrim trim, standby purge, file-cache purge with the service's privileges | **FAIL** | PASS |
| 8 | Automatic Performance Mode; load switcher and IdleSaver respect its precedence | not reached | PASS |
| 9 | Older database at the shared path upgrades when the new engine starts | PASS | PASS |

### 1. UI and the command pipe

**Pipe commands from the unelevated account** (process commands on the first install, whose process-command
authorization is unchanged since; rule and settings commands on the final build):

| Request | Result |
|---|---|
| Ping, GetStatus | answered; status now carries `ForegroundPid`, `UserIdleSeconds`, `PerformanceModeActive` |
| SetPriority BelowNormal / SetAffinity 0x3 on own process | done (`priority=BelowNormal affinity=3`) |
| SetPriority RealTime | `Access denied: DESKTOP-7JFAO78\Tee may not set Realtime priority. Run ProGuard as administrator to do this.` |
| SetPriority / TerminateProcess on `spoolsv` (SYSTEM) | `Access denied: ... may not SetPriority process 10084: it belongs to another user, is elevated or protected, or is not running...` |
| PurgeMemory | `Access denied: ... may not run PurgeMemory. Run ProGuard as administrator to do this.` |
| SaveProcessRule `pgtest-pb.exe` (BelowNormal, I/O 0) | saved (Id 10); a new `pgtest-pb` started as `priority=BelowNormal io=0` |
| SaveProcessRule with Realtime | `... may not set Realtime priority` |
| SaveProcessRule `lsass.exe` | `... may not change rules for lsass.exe, a system process` |
| SaveWatchdogRule with a Launch action / DeleteWatchdogRule of the administrator's Launch rule | refused: Launch actions would start a program as another user |
| ClearActionLog | `... may not run ClearActionLog` |
| SetSetting `{"ProBalanceSensitivity":5}` | applied; other settings untouched |

From an elevated client: SetPriority RealTime → `RealTime`; PurgeMemory → success (see 7); SaveWatchdogRule (Launch)
→ saved.

**Failures found on the first install, now fixed:**
- *The UI could not save anything.* The service creates `%ProgramData%\ProGuard` and the database as SYSTEM; users get
  read-only access (Process Lasso's `prolasso.ini` is the same). Writing as the UI's account: `attempt to write a
  readonly database`. Every Settings/Power/Rules/Watchdog/Logs save went straight to the database, and the settings
  pages swallowed the error. → **bd54dd9**: the engine owns the database; the UI saves through new pipe commands
  (`SaveProcessRule`, `DeleteProcessRule`, `SaveWatchdogRule`, `DeleteWatchdogRule`, `ClearActionLog`) and a merging
  `SetSetting`, with `RuleAuthorization` deciding what a standard user may put in a rule; save errors are shown.
- *The process list bypassed the engine.* Priority, affinity, suspend/resume, trim and terminate ran with the UI's own
  rights and swallowed failures: unelevated, a Realtime request silently became High while the list showed
  "RealTime", and actions on other users' processes silently did nothing. Its CPU limit called an `ICpuLimiter` the UI
  never registers, so it did nothing at all. → **4b1837c**: these go through the engine (local fallback only without
  an engine) and show its refusal; the list shows the priority Windows actually set.
- *Tests drove the installed engine.* With the service installed, the full test suite turned Performance Mode on in
  the live engine (`PowerViewModel_PerformanceMode_TogglesCorrectly`; seen twice in the engine log from other
  sessions' test runs) and, without a service, switched the machine's power plan locally. → **637aaf0**:
  `PROGUARD_PIPE_NAME`, set by `TestEnvironment` to a pipe nobody serves.

Not covered: the UI's buttons were not clicked by automation (WPF-UI's navigation items expose no UI Automation
select/invoke pattern, and synthetic mouse input would have taken over the desktop). The UI's save path is the
`ConfigWriter`/`EngineProxy` code covered by `RuleEditAuthorizationTests`; the pipe messages above are the ones it
sends, from the same account.

### 2. Foreground and idle reports

- The UI's reporter connects (`Foreground reporter 1 connected from session 1`); GetStatus shows the foreground PID
  and idle time (`"ForegroundPid":24652,"UserIdleSeconds":18.687`).
- Windowed 2-thread burner (12.5% CPU), final build, sampling the process and GetStatus every 200 ms:
  `0.32s Normal engineForegroundPid=self` → (minimized at 8.0 s) `9.72s BelowNormal` → (foreground again at 16.0 s)
  `16.52s engineForegroundPid=self`, `16.74s Normal`. Log: `ProBalance restored pgtest-fg (38432) to Normal: Came to
  the foreground`. Never restrained while in front; no boost (see below).
- IdleSaver (1 min, Balanced): `Power plan High performance -> Balanced: IdleSaver: no input for 1 min` at 62 s idle;
  one injected mouse event (`SendInput`) → `Balanced -> High performance: IdleSaver: user input; restored the plan from
  before IdleSaver` **0.40 s** later (0.39 s on the first install).
- Found: ForegroundBoost was always on. Once the engine knew the foreground process, it raised every foreground app to
  Above Normal (seen at 0.3 s in the first-install run). Process Lasso's default is off. → **21d1b0a**
  (`AppSettings.ForegroundBoostEnabled`, default false, toggle on the Settings page).
- Added for diagnosis: **662f1c6** (foreground/idle/Performance Mode in GetStatus, reporter connect/disconnect logged).

### 3. ProBalance timing

Background burner on 2 of 16 threads (12.5% total CPU), priority sampled every 50–100 ms:

| | Restrained after start | Restored after the load stopped |
|---|---|---|
| Process Lasso (docs/process-lasso-internals.md) | 1.86 s | ~1.7 s |
| First install | 3.18 s | 2.18 s |
| Final build, 3 runs | 1.13 s, 1.11 s, 2.00 s | 1.13 s, 1.11 s, 1.00 s |

Cause: ProBalance evaluated on its own 1 s timer, out of phase with ProcessMonitor's 1 s CPU sampling, so it judged
usage up to a second old; and a new process read 0% on its first sample. → **3bba281**: ProBalance evaluates on
`IProcessMonitor.Refreshed`; a process first seen within 5 s of starting gets its usage since start as its first
reading.

### 4. Per-process rules at process start

Rules were written to the database while the service ran. **First install: none applied.** GetStatus kept reporting
`ActiveRuleCount: 1` after 9 rules were added: `RuleStore` only reloaded when `proguard.db`'s write time changed, but
in WAL mode another process's commit only touches `proguard.db-wal`. → **bd7e4b1** (also affects rules saved by an
elevated UI or any tool; the existing test bumped the file time by hand, which hid it).

Final build, `pgtest-rule.exe` started by the user (control: `burner.exe`, no rule):

| Setting | Rule | Read back from the process |
|---|---|---|
| Priority | AboveNormal | `priority=AboveNormal` (control: Normal) |
| Affinity | 0xF0 | `affinity=0xF0` (control: 0xFFFF) |
| I/O priority | Low (1) | `io=1` (control: 2) |
| Memory priority | 2 | `mem=2` (control: 5) |
| Efficiency mode | on | `PROCESS_POWER_THROTTLING ControlMask/StateMask = 0x1/0x1` (control 0/0) |
| Priority boost | disabled | `GetProcessPriorityBoost = True` |
| GPU priority | Below Normal | `D3DKMTGetProcessSchedulingPriorityClass = 1` (control: 2) |
| CPU Sets (`pgtest-cpuset.exe`) | 256,257 | `GetProcessDefaultCpuSets = 256 257` |
| GPU preference | High performance | `HKU\S-1-5-21-…-1001\Software\Microsoft\DirectX\UserGpuPreferences\<exe> = "GpuPreference=2;"`; nothing under `HKU\S-1-5-18` |

Log: `Applied GPU preference High performance for S-1-5-21-2953843503-2753174364-614686599-1001 at …\pgtest-rule.exe`.

### 5. CPU Limiter, instance limits, Instance Balancer, startup boost

- CPU Limiter (≥5% for 2 s → remove 8 cores for 10 s), 2-thread burner: affinity `0xFFFF` → `0xFF` at 4.1 s, back to
  `0xFFFF` at 14.0 s, reduced again at 16.1 s while the load continued. Log: `Constraining CPU affinity by 8 CPUs for
  10000 ms. New CPU affinity is 0xFF, original was 0xFFFF` / `restored pgtest-cpu (33528) to 0xFFFF: limit period ended`.
- Instance limit 2: of three `pgtest-inst` started 0.7 s apart, the third was ended: `Instance limit for pgtest-inst:
  terminated 42716 (3 instances in session 1, limit 2)`.
- Instance Balancer (equal cores): three instances got disjoint `0x3F`, `0x7C0`, `0xF800`.
- Startup boost (High for 5 s): `Normal` → `High` at 1.0 s (the WMI start event's 1 s polling) → `Normal` at 5.5 s.

### 6. Watchdog and Keep Running

All started processes checked with `Win32_Process` (the test processes were started from session 1):

- Restart (CPU > 5% for 3 s): `Launched "…\pgtest-restart.exe" 2 8 60 marker-restart as process 30908 in session 1`;
  `Win32_Process`: session 1, identical command line.
- Keep Running: killed `pgtest-keep` → `relaunching` → `"…\pgtest-keep.exe" 0 0 0 marker-keep as process 8136 in session
  1`, about 3 s later.
- Launch (running > 3 s): `burner.exe` started in session 1 with `"…\burner.exe" 0 1 120 launched-by-watchdog`. (The
  first attempt failed because the test rule I wrote into the database contained a stray `\b` character; corrected over
  the pipe as administrator, not a product bug.)

### 7. SmartTrim

**First install: FAIL.** An administrator's "Purge now" over the pipe returned `Standby list purge failed:
SeProfileSingleProcessPrivilege is not enabled`, with `OpenProcessToken failed (Access is denied., error 5)` for the
engine's own token. Once SmartTrim and the purges were enabled over the pipe, the periodic passes produced no log lines
at all and the standby list stayed at ~19 GB, though the same build run as an elevated console process trimmed and
purged normally (`purge returned 0x00000000 in 2704 ms`, standby 19062 → 131 MB).

Cause: `NamedPipeServerStream.RunAsClient` (used to check the caller) reverts the thread's token but leaves the client's
identity in the ExecutionContext, so await continuations, tasks and timers started from that connection later ran **as
the client**. The purge, dispatched after an await, ran as the admin client; SmartTrim's timers, created while applying
settings received over the pipe, ran as the unelevated user (trims of the user's own processes worked, purges failed,
and their log writes were denied). Reproduced in a test: a timer started after the check ran at
`TokenImpersonationLevel.Impersonation`. → **fd48ff2** (impersonated checks run in a throw-away context);
diagnostics **b41ae07**, **377d338**.

Final build:
- Periodic trim (enabled over the pipe by the user): `SmartTrim: trimmed Discord (23828) 621 MB -> 19 MB`,
  `trimmed claude (22864) 594 MB -> 4 MB`, ...
- Automatic standby + file-cache purge: `Memory purge: purged standby list, reclaimed 20146 MB`, `purged file cache,
  reclaimed 352 MB`.
- "Purge now" from an administrator over the pipe: `{"ProcessesTrimmed":1,"WorkingSetBytesReclaimed":619769856,
  "StandbyPurged":true,"FileCachePurged":true,"Errors":[]}`; from the standard user: refused (see 1).
- No `impersonating`, `could not enable` or `OpenProcessToken` lines in the final run's log.

### 8. Performance Mode precedence

`pgtest-perf.exe` listed for automatic Performance Mode (plan: Bitsum Highest Performance), load switcher set to want
Power saver, IdleSaver 1 min → Balanced; plan sampled every second (`evidence: powercfg`):

```
16:05:51 enable load switcher             -> 16:05:57 (Power saver)   Load switcher: average CPU 26.1% is below 90%
16:06:00 idle 60 s                        -> (Balanced)               IdleSaver: no input for 1 min
16:06:12 start pgtest-perf.exe            -> 16:06:14 (Bitsum Highest Performance)  Performance Mode engaged automatically
16:06:14 .. 16:07:42, idle 75 s -> 163 s     plan stays Bitsum Highest Performance (IdleSaver and load switcher held off)
16:07:42 stop pgtest-perf.exe             -> 16:07:44 (Power saver) Performance Mode released, 16:07:45 (Balanced) IdleSaver
```

Precedence holds (Performance Mode > IdleSaver > load switcher). Minor: on release the load switcher's plan applied for
one second before IdleSaver took over again (follow-up below).

### 9. Schema upgrade

The database already at `C:\ProgramData\ProGuard\proguard.db` was an early one (ProcessRules 9 columns, Settings 15,
WatchdogRules 8); a legacy process rule and Watchdog rule were added to it. After the service's first start:
ProcessRules 30 columns, WatchdogRules 11, Settings 36; `legacy-rule.exe` kept its priority (16384) and
ExcludeFromProBalance, new columns took their defaults (`CooldownSeconds 60`, `SmartTrimMinimumRamLoadPercent 65`,
`KeepRunning 0`), existing settings kept their values (`SmartTrimIntervalSeconds 300`), log `Loaded 1 process rules, 1
watchdog rules`.

## Other fixes made for the deployment

- **943511d** The engine logged only to the console, which a service doesn't have: it now writes
  `%ProgramData%\ProGuard\logs\engine.log` (10 MB roll-over).
- **a221287**, **45b0884** `install-service.ps1` only worked from its own folder and registered the LocalSystem service
  with its binary in the build output (writable by whoever can write the checkout). It now requires elevation, copies to
  `%ProgramFiles%\ProGuard\Engine`, sets recovery actions and supports `-Uninstall`.
- **a17ee44** Refusal wording for rule edits and deletes.

## Follow-ups (not fixed here)

- When automatic Performance Mode ends while the user is idle, the load switcher's plan is applied for about a second
  before IdleSaver re-takes the plan: IdleSaver's request is not re-evaluated when Performance Mode releases.
- The Power page's Performance Mode toggle starts off even when Performance Mode is on (manual or automatic); it doesn't
  read the engine's state (`EngineStatus.PerformanceModeActive` now has it).
- Watchdog Restart fires again for each restarted instance that still breaks the rule (the cooldown is per process);
  matches the rule as written, but a rule that keeps tripping restarts the program every few seconds.
- Rule checks for non-administrators look at the instances running when the rule is saved; an instance another user
  starts later is still affected by a Disallow or instance limit the standard user created.
- Not exercised: several signed-in users, Remote Desktop sessions, and clicking the UI itself (see 1).
