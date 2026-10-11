# Process Lasso 18.4.0.48 — how each feature works

Findings from static analysis (import tables, strings, resource DLL text, `prolasso.ini`)
and live observation of the installed copy. No disassembly/decompilation was done — the
mechanisms below are inferred from which Windows APIs the binaries pull in, the log/UI
text describing each action, the INI keys and their defaults, and timing experiments.

## Architecture

| Binary | Role |
|---|---|
| `ProcessGovernor.exe` | Core engine. Runs as the `ProcessGovernor` service (or per-user process). Applies every rule; GUI is optional. |
| `ProcessLasso.exe` | GUI. Process list, graphs, rule editor. Talks to the governor and edits the INI. |
| `bitsumsessionagent.exe` | Helper launched into the user session (service can't touch user desktop, play sounds, show balloons, launch processes as user). |
| `InstallHelper.exe`, `ProcessLassoLauncher.exe`, `srvstub.exe` | Install/elevation/startup plumbing. |
| `Insights.exe`, `LogViewer.exe`, `CPUEater.exe`, `ThreadRacer.exe`, `TweakScheduler.exe` | Side tools (sampling viewer, log viewer, ProBalance demo, benchmark, MMCSS tweaks). |

- **Config**: single INI at `C:\ProgramData\ProcessLasso\config\prolasso.ini` (every feature + default is listed there; it's the best spec you'll get).
- **Log**: CSV at `C:\ProgramData\ProcessLasso\logs\processlasso.log`: `filetime, date, host, user, pid, name, actionCode, "(0xNN) title", description, path, cmdline`. Action codes are the `PL_ACTION_*` enum (e.g. 0x2b ProBalance begun, 0x2c priority lowered, 0x2e priority restored, 0x20 restraint ended, 0x21 ended due to termination).
- **Polling**: `UpdateSpeedCore=1000` ms (governor), `UpdateSpeedGUI=1000` ms. Everything is a 1 s poll loop — no ETW, no kernel driver.
- **Process enumeration**: `NtQuerySystemInformation(SystemProcessInformation)` + `CreateToolhelp32Snapshot` for threads. Command lines read from the target PEB via `ZwQueryInformationProcess` + `ZwReadVirtualMemory`.
- **Persistent rules applied by polling**: new processes are detected on the next poll and rules applied then (so there's up to ~1 s where a new process runs with defaults). Exception: priority/IO/page priority can optionally be written to **IFEO** `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<exe>\PerfOptions` (`CpuPriorityClass`, `IoPriority`, `PagePriority`) so Windows applies them at process creation (`ioPriorityEnforcedByRegistry`).
- **IPC/eventing**: named kernel objects (`Global\ProcessLasso_ProBalanceEvent9`, mutexes) + the shared INI. GUI tells the governor to reload.
- **Rule matching**: name, wildcard, full path (`MatchWildcardsToPathnames`), optional command line (`MatchOnCommandLine`), regex.

## Feature → mechanism map

### ProBalance (`[OutOfControlProcessRestraint]`)
Dynamic priority restraint of background CPU hogs. Defaults:

| Key | Default | Meaning |
|---|---|---|
| `TotalProcessorUsageBeforeRestraint` | 10 % | System-wide CPU must be ≥ this before anything is restrained |
| `PerProcessUsageBeforeRestraint` | 5 % | Process must use ≥ this % of *total* CPU (all cores) |
| `TimeOverQuotaBeforeRestraint` | 900 ms | …continuously for this long |
| `PerProcessUsageForRestore` | 2 % | Restore once the process drops ≤ this |
| `MinimumTimeOfRestraint` | 4200 ms | Stay restrained at least this long |
| `MaximumTimeOfRestraint` | 0 (off) | Force-restore after this long (lets it run briefly, may re-restrain) |
| `TameOnlyNormal` | true | Only touch processes currently at Normal priority class |
| `LowerToIdleInsteadOfBelowNormal` | false | Restraint = `BELOW_NORMAL_PRIORITY_CLASS` |
| `ExcludeForegroundProcesses2` | true | `GetForegroundWindow` → `GetWindowThreadProcessId` is exempt; restraint ends if a restrained process comes to foreground |
| `ExcludeChildrenOfForeground` | true | Children (by parent PID) of the foreground process are exempt |
| `ExcludeServices` | true | Session 0 / service processes exempt (`ProcessIdToSessionId`, SCM enum) |
| `DisableProBalanceWhenSysIdle` / `…IfSysIdleThisManyMS` | false / 30000 | Uses `GetLastInputInfo` |
| `RestrainByAffinity` / `ProBalanceDropOneRandomCore` | false | Also shrink affinity (or drop one random core) during restraint, restore after |
| `LowerIOPriorityDuringRestraint` | false | `NtSetInformationProcess(ProcessIoPriority=33)` → Low |
| `UseEfficiencyMode` | false | `SetProcessInformation(ProcessPowerThrottling)` EcoQoS on during restraint |
| `OocDisableCoreParkingWhileIn` | false | Temporarily set core-parking min cores = 100 % on active power plan, restore after |
| `DoNotLowerPriorityClass` | false | Affinity/IO/efficiency-only restraint |

Plus a hard-coded exclusion list (critical system processes, shell, audio, etc.) overridable via `OocHardCodedExclusionOverrides`.

**Observed timing (this machine, defaults):** a hidden all-core burner went Normal → BelowNormal **1.86 s** after launch (900 ms over-quota + ~1 s poll granularity). A 6 s burst was restored to Normal **~1.7 s** after the load stopped (min-restraint already satisfied, needs one poll below 2 %). Restraint stores the *original* priority class and restores exactly that; if the process ends while restrained it logs 0x21.

### CPU Limiter (`CPULimitRules`)
**Not** a rate limiter and **not** a Job Object (no `CreateJobObject`/`SetInformationJobObject` anywhere in the binaries). It's dynamic affinity reduction:
> "Constraining CPU affinity by %u CPUs for %u ms. New CPU affinity is 0x…, original was 0x…"

Rule = process match, CPU % threshold, time over threshold, number of cores to remove, duration. When exceeded → `SetProcessAffinityMask(original & ~N cores)` for the duration → restore original.

### Hard throttle (`ProcessThrottles`, `OneTimeProcessThrottles`)
Separate, discouraged feature: "forces a periodic sleep on application threads". Implemented with `OpenThread` + `SuspendThread`/`ResumeThread` duty cycling (and `NtSuspendProcess`/`NtResumeProcess`), levels shown as arrows in the UI.

### Persistent per-process defaults (`[ProcessDefaults]`)
All applied on detection each poll, re-applied if changed (where configured).

| Feature | INI key | API |
|---|---|---|
| Priority class | `DefaultPriorities` | `SetPriorityClass` (optionally IFEO `CpuPriorityClass`) |
| CPU affinity | `DefaultAffinitiesEx`, `NamedAffinities` | `SetProcessAffinityMask`; >64-core: `SetThreadGroupAffinity` per thread, reapplied continuously |
| CPU Sets | `CPUSets` | `SetProcessDefaultCpuSets` / `SetProcessDefaultCpuSetMasks`, enumerate with `GetSystemCpuSetInformation`. System-reserved CPU sets via `NtSetSystemInformation` (registry-persisted, needs reboot) |
| I/O priority | `DefaultIOPriorities` | `NtSetInformationProcess(ProcessIoPriority=33)` (0 very low … 4 critical) |
| Memory priority | `DefaultMemoryPriorities` | `SetProcessInformation(ProcessMemoryPriority)` / `ProcessPagePriority=39` (0–7) |
| GPU priority | `DefaultGPUPriorities` | `D3DKMTSetProcessSchedulingPriorityClass` (gdi32) |
| GPU adapter preference | `DefaultGPUAdapterPreferences` | Writes `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` value `<fullpath>` = `GpuPreference=N;` (needs full path, no wildcards) |
| Efficiency mode | `EfficiencyMode` | `SetProcessInformation(ProcessPowerThrottling)` with `EXECUTION_SPEED` control/state (EcoQoS); E=on, e=off (off pins to P-cores on hybrid CPUs) |
| Dynamic thread priority boost | `ThreadPriorityBoosts` | `SetProcessPriorityBoost` (+ `SetThreadPriorityBoost` for existing threads) |

### Instance limits / disallowed / keep-running / instance balancer (`[ProcessAllowances]`)
- **Disallowed processes**: terminated on detection (`TerminateProcess`), critical processes refused.
- **Instance count limits** (`InstanceLimitedProcesses`): count matches per session (or all sessions), terminate the newest over the limit.
- **Keep Running** (`KeepRunningProcessesEx`): relaunch with `CreateProcessW` when it exits. Requires governor as a normal user process, not service (session 0 can't launch into the user's desktop).
- **Instance Balancer** (`InstanceManagedCPUAffinities`): splits cores across matching instances — "equal CPUs per instance" or "fixed cores per instance", optional SMT off, cores-per-group to reserve, min CPU% to be included. Re-balances on instance start/exit. Plain affinity.

### Startup boost (`StartupBoostRules`)
Raise priority class for N s (1–3600) after a new process starts, then restore — unless something else changed the priority during the boost.

### Foreground boost (`[ForegroundBoosting]`, default off)
Raise the foreground process to `ForegroundBoostPriorityClass=0x8000` (Above Normal) only if it is Normal (`BoostOnlyNormal`), optional GPU priority boost. Bitsum itself warns this is mostly ineffective.

### SmartTrim (`[MemoryManagement]`)
- **Working set trim**: `SetProcessWorkingSetSize(h, -1, -1)` on processes with WS > `MinimumProcessWSSInMb` (196 MB) when RAM load ≥ `SmartTrimAutoMinimumRAMLoad` (65 %), every `SmartTrimIntervalMins` (15).
- **Standby list purge**: `NtSetSystemInformation(SystemMemoryListInformation=80, MemoryPurgeStandbyList=4)` when free RAM (`\Memory\Free & Zero Page List Bytes`) < `ClearStandbyFreeRAMThresholdMB` (1024), by default only in Performance Mode.
- **File cache purge**: `NtSetSystemInformation(SystemFileCacheInformationEx=81)` with min/max = -1.
- All need `SeProfileSingleProcessPrivilege` / `SeIncreaseQuotaPrivilege` via `AdjustTokenPrivileges`.

### Power features (`[GamingMode]`, `[PowerSaver]`, `[LoadBasedPowerSwitcher]`, `[PowerManagement]`)
APIs: `PowerGetActiveScheme`/`PowerSetActiveScheme`, `PowerDuplicateScheme`/`PowerDeleteScheme` (installs "Bitsum Highest Performance"), `PowerSetActiveOverlayScheme`/`PowerGetActualOverlayScheme` (the Windows 11 power-mode slider), `SetThreadExecutionState` (keep awake), and power-scheme registry under `SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\%s`.
- **Performance Mode** (gaming): switch to `TargetPowerPlan`, optionally purge standby; automatic when a listed or *detected* game runs. Game detection is path heuristics: `\steamapps\common\`, `\epic games\`, `\gog games\`, `\ubisoft\`, `\battle.net\`, `\ea games\`… plus parsing Steam `libraryfolders.vdf` / `appmanifest_*.acf`.
- **IdleSaver / Dynamic Boost**: `GetLastInputInfo` idle time ≥ `PowerSaver_IdleTime` → switch to a low-power plan, revert on input; disabled while excluded processes run; optional CPU-threshold gate.
- **Load-based power switcher**: moving average of system CPU over `AveragingWindowSamples` (5); < low (10 %) → low plan, > high (50 %) → high plan; `MinimumTimeInPowerPlanMs` (5000) hysteresis.
- **Core parking control during ProBalance**: edits the active plan's `CPMINCORES` (processor-power subgroup) via powrprof setters and re-applies the scheme.
- Remembers the original plan (`IdleSaverRecoveryGUID`) and restores it if the governor was killed.

### Watchdog (`WatchdogRules2`)
"If process exceeds X % CPU (or memory) for Y s then …": terminate, restart, set priority, set IO priority, change affinity (for a duration, then restore), trim working set (once or continuous), launch a program, notify. Memory thresholds use `GetProcessMemoryInfo`.

### Processor Group Extender (`ProcessorGroupExtended`, >64 cores only)
Monitors threads of target processes (`Thread32First/Next`) and spreads them across processor groups with `SetThreadGroupAffinity`.

### System timer resolution (`SetTimerResolutionAtStartup`)
`NtQueryTimerResolution` / `NtSetTimerResolution`. Optionally global via the `GlobalTimerResolutionRequests` registry switch (reboot).

### Monitoring metrics (GUI)
- CPU: PDH `\Processor Information(_Total)\% Processor Utility` (matches Task Manager, frequency-scaled) and per-core `\Processor Information(%u,%u)\% Processor Time`; core parking from `\Processor Information(%u,%u)\Parking Status`.
- **Responsiveness**: PDH `\System\Processor Queue Length`.
- Per-process CPU: `GetProcessTimes` deltas (plus `QueryProcessCycleTime`).
- GPU: `D3DKMTQueryAdapterInfo` / `D3DKMTOpenAdapterFromLuid` + DXGI.
- Memory load: `GlobalMemoryStatusEx`.

## ProGuard vs Process Lasso — gaps

Paths are under `src/`.

### Bugs to fix first
1. **IO priority info class is wrong**: `ProGuard.Core/Native/Ntdll.cs:28` has `ProcessIoPriority = 21`. 21 is `ProcessAffinityMask`; IoPriority is **33 (0x21)**. The UI IO-priority action (`ProcessListViewModel.cs:655`) fails silently.
2. **Engine ignores settings and IPC**: nothing in the engine calls `RuleStore.GetSettings()`, and `PipeServer.cs:75` replies `Pong` to every message. ProBalance sensitivity, Performance Mode, IdleSaver and SmartTrim settings in the UI never reach the engine.
3. **ProBalance lowers non-Normal processes**: it only skips Idle/BelowNormal, so AboveNormal/High/Realtime get dropped to BelowNormal. PL's `TameOnlyNormal=true` is the safe default.

### ProBalance algorithm differences
| | Process Lasso | ProGuard (`ProBalanceEngine.cs`) |
|---|---|---|
| System CPU gate | ≥ 10 % | > 85 % |
| Per-process threshold | ≥ 5 % of total CPU | ≥ 15 % |
| Sustained for | 900 ms | single sample |
| Restore when | process ≤ 2 % **and** ≥ 4.2 s restrained | system < 50 % (all at once) |
| Max restraint | optional | none |
| Only Normal-class targets | yes | no |
| Child-of-foreground exclusion | yes | no |
| Services excluded | yes | no |
| Ends on becoming foreground | yes | no, and ForegroundBoost won't boost it either |
| Respect manual change during restraint | yes | overwrites |

PL's design point: low thresholds and per-process hysteresis. It restrains early and briefly, rather than waiting until the machine is already pegged at 85 %.

### Feature parity
| Feature | ProGuard status | To match PL |
|---|---|---|
| CPU Limiter | Job-object hard cap (`CpuLimiter.cs`) | PL uses dynamic **affinity reduction** (threshold → drop N cores for T ms). The job cap is arguably stronger; keep it as "hard throttle" and add the affinity mode |
| Persistent priority/affinity | Applied on start (`ProcessGovernor.cs:87-97`) | Add re-apply-if-changed, and optionally IFEO `PerfOptions` for zero-lag priority |
| IO / memory priority, efficiency mode | UI-only manual actions | Move into rules in the engine |
| CPU Sets, GPU priority, GPU preference, thread boost | missing | `SetProcessDefaultCpuSets`, `D3DKMTSetProcessSchedulingPriorityClass`, `UserGpuPreferences` registry, `SetProcessPriorityBoost` |
| Instance limits / keep running / instance balancer | `MaxInstances` field unused; no keep-running | |
| Startup boost | missing | |
| SmartTrim | Done (`SmartTrimService.cs`): trims WS ≥ 196 MB every 15 min while load ≥ 65 %; standby purge (80/4) below 1024 MB free, optionally only in Performance Mode; optional file cache purge (`SetSystemFileCacheSize(-1,-1,0)`); privileges enabled via `AdjustTokenPrivileges`; admin-only "Purge now" IPC command | — |
| Performance Mode / IdleSaver / load switcher | Plan switch exists but is never invoked; no idle detection; hardcoded 3 plans | `GetLastInputInfo`, `PowerEnumerate`, moving-average switcher, overlay schemes |
| Watchdog | Duration ignored; Restart/SetAffinity no-ops; SetPriority ignores argument | Implement "for Y seconds" and the remaining actions |
| Timer resolution, group extender | missing | Low priority |
| Metrics | `GetSystemTimes`; per-process CPU via `TotalProcessorTime` (0 % for protected processes) | PDH `% Processor Utility` to match Task Manager; `Processor Queue Length` for responsiveness |
