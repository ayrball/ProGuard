using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ActivityKind = ProGuard.Shared.Contracts.ActivityKind;

namespace ProGuard.Preview;

/// <summary>
/// What the stand-in engine does with each command: the state of one canned machine (<see cref="StandInData"/>) and the
/// answers to every <see cref="CommandTypes"/> entry, with the same argument formats and result types the contract
/// documents. Actions succeed and change the machine, so the real UI can be driven against it with <c>--serve</c>.
/// </summary>
/// <remarks>
/// <paramref name="callerIsAdministrator"/> false plays a standard user. It answers "Access denied" the way
/// <c>IpcAuthorization</c> does for what the UI marks as needing administrator rights (process rules, watchdog rules,
/// Purge now, Realtime priority) and for acting on processes the caller doesn't own. With <paramref name="readOnly"/>
/// actions still succeed but change nothing, which keeps a render run repeatable.
/// </remarks>
public sealed class StandInMachine
{
    private readonly object _gate = new();
    private readonly bool _admin;
    private readonly bool _readOnly;
    private readonly DateTime _engineStarted = DateTime.UtcNow.AddHours(-3).AddMinutes(-12);

    private readonly List<StandInData.Seed> _processes;
    private readonly List<ProcessRuleDto> _rules = StandInData.Rules();
    private readonly List<WatchdogRuleDto> _watchdog = StandInData.WatchdogRules();
    private readonly List<PowerPlanDto> _plans = StandInData.PowerPlans();
    private readonly List<CpuSetDto> _cpuSets = StandInData.CpuSets();
    private readonly List<ActivityEntry> _activity;
    private EngineSettingsDto _settings = StandInData.Settings();

    private Guid _activePlan = StandInData.Balanced;
    private Guid _planBeforePerformanceMode = StandInData.Balanced;
    private string _planOwner = "User";
    private bool _performanceMode;
    private int _foreground = StandInData.ForegroundPid;
    private bool _timerGlobalRequests;
    private int _tick;

    public StandInMachine(bool callerIsAdministrator = true, bool readOnly = false)
    {
        _admin = callerIsAdministrator;
        _readOnly = readOnly;
        var now = DateTime.UtcNow;
        _processes = StandInData.Processes(now, showPaths: callerIsAdministrator);
        _activity = StandInData.Activity(now);
        MarkRuledProcesses();
    }

    public bool CallerIsAdministrator => _admin;

    private static string Caller => StandInData.Me;

    /// <summary>Answers one command. Never throws: a failure is a <see cref="CommandResult.Fail"/>.</summary>
    public CommandResult Handle(CommandMessage cmd)
    {
        lock (_gate)
        {
            try
            {
                return Dispatch(cmd);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException)
            {
                return CommandResult.Fail($"Bad request for {cmd.CommandType}: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ dispatch

    private CommandResult Dispatch(CommandMessage cmd)
    {
        switch (cmd.CommandType)
        {
            // ---- reading
            case CommandTypes.GetSnapshot: return Data(BuildSnapshot());
            case CommandTypes.GetProcessRules: return Data(_rules);
            case CommandTypes.GetWatchdogRules: return Data(_watchdog);
            case CommandTypes.GetSettings: return Data(_settings);
            case CommandTypes.GetPower: return Data(PowerState());
            case CommandTypes.GetCpuSets: return Data(_cpuSets);
            case CommandTypes.GetTimerResolution: return Data(TimerState());
            case CommandTypes.GetActivity:
            {
                int max = int.TryParse(cmd.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 200;
                return Data(_activity.Take(max).ToList());
            }

            // ---- acting on one process
            case CommandTypes.SetPriority:
            {
                if (!Enum.TryParse<ProcessPriorityClass>(cmd.Argument, ignoreCase: true, out var priority) || !Enum.IsDefined(priority))
                    return CommandResult.Fail($"'{cmd.Argument}' is not a priority class.");
                if (priority == ProcessPriorityClass.RealTime && !_admin) return Deny("set Realtime priority");
                if (!TryTarget(cmd, out var p, out var error)) return error;
                Mutate(() => p.Priority = priority);
                Log(ActivityKind.Manual, p, $"Priority set to {priority}.");
                return CommandResult.Ok();
            }
            case CommandTypes.SetAffinity:
            {
                if (!TryParseMask(cmd.Argument, out var mask)) return CommandResult.Fail($"'{cmd.Argument}' is not an affinity mask.");
                if ((mask & StandInData.AllCores) == 0) return CommandResult.Fail("Pick at least one processor that exists.");
                if (!TryTarget(cmd, out var p, out var error)) return error;
                Mutate(() => { p.AffinityMask = mask & StandInData.AllCores; p.IsAffinityLimited = false; });
                Log(ActivityKind.Manual, p, $"Affinity set to 0x{mask & StandInData.AllCores:X}.");
                return CommandResult.Ok();
            }
            case CommandTypes.SetCpuLimit:
            {
                if (!double.TryParse(cmd.Argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || percent <= 0 || percent > 100)
                    return CommandResult.Fail("CPU limit must be in (0, 100]");
                if (!TryTarget(cmd, out var p, out var error)) return error;
                Mutate(() => p.CpuLimitPercent = percent);
                Log(ActivityKind.Manual, p, $"CPU capped at {percent:0.#}%.");
                return CommandResult.Ok();
            }
            case CommandTypes.RemoveCpuLimit:
            {
                if (!TryTarget(cmd, out var p, out var error)) return error;
                Mutate(() => p.CpuLimitPercent = null);
                Log(ActivityKind.Manual, p, "CPU cap removed.");
                return CommandResult.Ok();
            }
            case CommandTypes.SuspendProcess:
            case CommandTypes.ResumeProcess:
            {
                if (!TryTarget(cmd, out var p, out var error)) return error;
                bool suspend = cmd.CommandType == CommandTypes.SuspendProcess;
                Mutate(() => p.IsSuspended = suspend);
                Log(ActivityKind.Manual, p, suspend ? "Paused." : "Resumed.");
                return CommandResult.Ok();
            }
            case CommandTypes.TerminateProcess:
            {
                if (!TryTarget(cmd, out var p, out var error)) return error;
                if (p.Pid is 0 or 4 || p.Pid == StandInData.EnginePid) return CommandResult.Fail($"ProGuard won't end {p.Name} (PID {p.Pid}).");
                Mutate(() => _processes.RemoveAll(s => s.Process.Pid == p.Pid));
                Log(ActivityKind.Manual, p, "Ended.");
                return CommandResult.Ok();
            }
            case CommandTypes.TerminateProcessTree:
            {
                if (!TryTarget(cmd, out var p, out var error)) return error;
                if (p.Pid is 0 or 4 || p.Pid == StandInData.EnginePid) return CommandResult.Fail($"ProGuard won't end {p.Name} (PID {p.Pid}).");
                var tree = Descendants(p.Pid).Where(CanTouch).ToList();
                Mutate(() => _processes.RemoveAll(s => tree.Contains(s.Process.Pid)));
                Log(ActivityKind.Manual, p, $"Ended with {tree.Count - 1} child process(es).");
                return Data((long)tree.Count);
            }
            case CommandTypes.SetEfficiencyMode:
            {
                if (!bool.TryParse(cmd.Argument, out var on)) return CommandResult.Fail("Efficiency mode needs true or false.");
                if (!TryTarget(cmd, out var p, out var error)) return error;
                Mutate(() => p.IsEfficiencyMode = on);
                Log(ActivityKind.Manual, p, $"Efficiency Mode {(on ? "on" : "off")}.");
                return CommandResult.Ok();
            }
            case CommandTypes.SetIoPriority:
            {
                if (!int.TryParse(cmd.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) || level is < 0 or > 3)
                    return CommandResult.Fail("Invalid I/O priority");
                if (!TryTarget(cmd, out var p, out var error)) return error;
                Mutate(() => p.IoPriority = level);
                Log(ActivityKind.Manual, p, $"I/O priority set to {level}.");
                return CommandResult.Ok();
            }
            case CommandTypes.SetMemoryPriority:
            {
                if (!int.TryParse(cmd.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) || level is < 1 or > 5)
                    return CommandResult.Fail("Invalid memory priority");
                if (!TryTarget(cmd, out var p, out var error)) return error;
                Mutate(() => p.MemoryPriority = level);
                Log(ActivityKind.Manual, p, $"Memory priority set to {level}.");
                return CommandResult.Ok();
            }
            case CommandTypes.TrimWorkingSet:
            {
                if (cmd.TargetPid == null)
                {
                    var eligible = _processes.Select(s => s.Process).Where(p => p.WorkingSetBytes >= _settings.SmartTrimMemoryThresholdMB * 1024 * 1024).ToList();
                    long freed = eligible.Sum(p => p.WorkingSetBytes * 35 / 100);
                    Mutate(() => eligible.ForEach(p => p.WorkingSetBytes -= p.WorkingSetBytes * 35 / 100));
                    Log(ActivityKind.Manual, null, $"Trimmed {eligible.Count} processes.");
                    return Data(freed);
                }
                if (!TryTarget(cmd, out var p, out var error)) return error;
                long reclaimed = p.WorkingSetBytes * 40 / 100;
                Mutate(() => p.WorkingSetBytes -= reclaimed);
                Log(ActivityKind.Manual, p, "Working set trimmed.");
                return Data(reclaimed);
            }
            case CommandTypes.ClearActionLog:
            {
                if (!_admin) return Deny("clear the action log");
                // Like the engine: only the database action log goes. Watchdog events, manual actions and engine
                // messages live elsewhere and stay.
                Mutate(() => _activity.RemoveAll(a => a.Kind is not (ActivityKind.Watchdog or ActivityKind.Manual or ActivityKind.System)));
                return CommandResult.Ok();
            }
            case CommandTypes.PurgeMemory:
            {
                if (!_admin) return Deny("purge memory");
                var result = new MemoryPurgeResultDto
                {
                    ProcessesTrimmed = 31,
                    WorkingSetBytesReclaimed = 1480L * 1024 * 1024,
                    StandbyPurged = true,
                    StandbyBytesReclaimed = 2150L * 1024 * 1024,
                    FileCachePurged = _settings.FileCachePurgeEnabled,
                    FileCacheBytesReclaimed = _settings.FileCachePurgeEnabled ? 310L * 1024 * 1024 : 0,
                    Summary = "Trimmed 31 processes and purged the standby list: 3.5 GB freed.",
                };
                Log(ActivityKind.Memory, null, result.Summary);
                return Data(result);
            }
            case CommandTypes.ReportForeground:
            {
                // Sent on the foreground pipe by the real engine; accepted here so a test can send it to either.
                int pid = cmd.TargetPid ?? 0;
                Mutate(() => _foreground = _processes.Any(s => s.Process.Pid == pid) ? pid : 0);
                return CommandResult.Ok();
            }

            // ---- settings that change engine behaviour
            case CommandTypes.EnableProBalance:
            case CommandTypes.DisableProBalance:
                Mutate(() => _settings.ProBalanceEnabled = cmd.CommandType == CommandTypes.EnableProBalance);
                return CommandResult.Ok();
            case CommandTypes.SetSensitivity:
            {
                if (!int.TryParse(cmd.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) || level is < 1 or > 10)
                    return CommandResult.Fail("Sensitivity must be between 1 and 10");
                Mutate(() => _settings.ProBalanceSensitivity = level);
                return CommandResult.Ok();
            }
            case CommandTypes.ConfigureIdleSaver:
            {
                var arg = Argument<IdleSaverArgument>(cmd);
                if (arg.PlanGuid != null && !Guid.TryParse(arg.PlanGuid, out _)) return CommandResult.Fail("Invalid power plan GUID");
                Mutate(() =>
                {
                    _settings.IdleSaverEnabled = arg.Enabled;
                    _settings.IdleTimeoutMinutes = arg.TimeoutMinutes;
                    _settings.IdlePowerPlanGuid = arg.PlanGuid;
                });
                return CommandResult.Ok();
            }
            case CommandTypes.ConfigureSmartTrim:
            {
                var arg = Argument<SmartTrimArgument>(cmd);
                Mutate(() =>
                {
                    _settings.SmartTrimEnabled = arg.Enabled;
                    _settings.SmartTrimIntervalSeconds = arg.IntervalSeconds;
                    _settings.SmartTrimMemoryThresholdMB = arg.ThresholdMb;
                });
                return CommandResult.Ok();
            }
            case CommandTypes.EnablePerformanceMode:
            case CommandTypes.DisablePerformanceMode:
            {
                bool on = cmd.CommandType == CommandTypes.EnablePerformanceMode
                    && (cmd.Argument == null || !bool.TryParse(cmd.Argument, out var flag) || flag);
                Mutate(() => SetPerformanceMode(on));
                return CommandResult.Ok();
            }
            case CommandTypes.SetTimerResolution:
            {
                if (!_admin) return Deny("change the system timer resolution");
                var arg = Argument<TimerResolutionArgument>(cmd);
                if (arg.Resolution is < 0 or > 1_000_000) return CommandResult.Fail("The timer resolution must be between 0 and 100 ms");
                Mutate(() =>
                {
                    _settings.SetTimerResolutionAtStartup = arg.Resolution;
                    if (arg.GlobalRequests is bool global) _timerGlobalRequests = global;
                });
                Log(ActivityKind.Manual, null, arg.Resolution == 0 ? "Stopped asking for a timer resolution." : $"Asked for a {arg.Resolution / 10000.0:0.###} ms timer resolution.");
                return Data(TimerState());
            }
            case CommandTypes.SaveSettings:
            {
                var settings = Argument<EngineSettingsDto>(cmd);
                if (!_admin && settings.SetTimerResolutionAtStartup != _settings.SetTimerResolutionAtStartup)
                    return Deny("change the system timer resolution");
                if (!_admin && settings.ProBalanceDisableCoreParking != _settings.ProBalanceDisableCoreParking)
                    return Deny("change whether ProBalance unparks cores (it edits the power plan)");
                if (settings.ProBalanceSensitivity is < 1 or > 10) return CommandResult.Fail("Sensitivity must be between 1 and 10");
                if (settings.PollingIntervalMs < 100) return CommandResult.Fail("The polling interval must be at least 100 ms.");
                Mutate(() => _settings = settings);
                Log(ActivityKind.System, null, "Settings saved.");
                return Data(_readOnly ? _settings : settings);
            }
            case CommandTypes.SetPowerPlan:
            {
                if (!Guid.TryParse(cmd.Argument, out var id) || _plans.All(p => p.Id != id)) return CommandResult.Fail("That power plan isn't installed.");
                Mutate(() =>
                {
                    _activePlan = id;
                    _planOwner = "User";
                    _performanceMode = false;
                });
                Log(ActivityKind.Power, null, $"Power plan changed to {_plans.First(p => p.Id == id).Name}.");
                return CommandResult.Ok();
            }

            // ---- rules
            case CommandTypes.SaveProcessRule:
            {
                var rule = Argument<ProcessRuleDto>(cmd);
                if ((RuleRefusal(rule) ?? RuleRefusal(_rules.FirstOrDefault(r => r.Id == rule.Id && rule.Id != 0))) is { } refused) return Deny(refused);
                if (ValidateRule(rule) is { } problem) return CommandResult.Fail(problem);
                if (rule.Id != 0 && _rules.All(r => r.Id != rule.Id)) return CommandResult.Fail($"Rule {rule.Id} doesn't exist.");
                if (rule.Id == 0) rule.Id = _rules.Count == 0 ? 1 : _rules.Max(r => r.Id) + 1;
                Mutate(() =>
                {
                    int at = _rules.FindIndex(r => r.Id == rule.Id);
                    if (at >= 0) _rules[at] = rule; else _rules.Add(rule);
                    MarkRuledProcesses();
                });
                Log(ActivityKind.System, null, $"Rule saved for {rule.ProcessName}.");
                return Data(rule);
            }
            case CommandTypes.DeleteProcessRule:
            {
                if (!int.TryParse(cmd.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || _rules.All(r => r.Id != id))
                    return CommandResult.Fail("That rule doesn't exist.");
                if (RuleRefusal(_rules.First(r => r.Id == id)) is { } refused) return Deny(refused);
                Mutate(() =>
                {
                    _rules.RemoveAll(r => r.Id == id);
                    MarkRuledProcesses();
                });
                return CommandResult.Ok();
            }
            case CommandTypes.SaveWatchdogRule:
            {
                var rule = Argument<WatchdogRuleDto>(cmd);
                if ((WatchRefusal(rule) ?? WatchRefusal(_watchdog.FirstOrDefault(r => r.Id == rule.Id && rule.Id != 0))) is { } refused) return Deny(refused);
                if (string.IsNullOrWhiteSpace(rule.TargetProcessName)) return CommandResult.Fail("A watchdog rule needs a process name");
                if (rule.ThresholdValue <= 0) return CommandResult.Fail("The threshold has to be above zero.");
                if (rule.ConditionType == WatchdogTrigger.CpuAbove && rule.ThresholdValue > 100) return CommandResult.Fail("CPU can't go above 100%.");
                if (rule.DurationSeconds < 0 || rule.CooldownSeconds < 0) return CommandResult.Fail("Durations can't be negative.");
                if (!Enum.IsDefined(rule.ConditionType) || !Enum.IsDefined(rule.ActionType) || !Enum.IsDefined(rule.MemoryMetric))
                    return CommandResult.Fail("Unknown condition or action.");
                if (rule.Id != 0 && _watchdog.All(r => r.Id != rule.Id)) return CommandResult.Fail($"Rule {rule.Id} doesn't exist.");
                if (rule.Id == 0) rule.Id = _watchdog.Count == 0 ? 1 : _watchdog.Max(r => r.Id) + 1;
                Mutate(() =>
                {
                    int at = _watchdog.FindIndex(r => r.Id == rule.Id);
                    if (at >= 0) _watchdog[at] = rule; else _watchdog.Add(rule);
                });
                return Data(rule);
            }
            case CommandTypes.DeleteWatchdogRule:
            {
                if (!int.TryParse(cmd.Argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || _watchdog.All(r => r.Id != id))
                    return CommandResult.Fail("That rule doesn't exist.");
                if (WatchRefusal(_watchdog.First(r => r.Id == id)) is { } refused) return Deny(refused);
                Mutate(() => _watchdog.RemoveAll(r => r.Id == id));
                return CommandResult.Ok();
            }

            default:
                return CommandResult.Fail($"The stand-in engine doesn't implement '{cmd.CommandType}'.");
        }
    }

    /// <summary>The reply for a command the stand-in has no answer for. Tests look for it to catch contract additions.</summary>
    public static bool IsUnimplemented(CommandResult result) =>
        !result.Success && result.Error?.StartsWith("The stand-in engine doesn't implement", StringComparison.Ordinal) == true;

    // ------------------------------------------------------------------ the machine

    private EngineSnapshot BuildSnapshot()
    {
        _tick++;
        double load = Math.Clamp(46 + 24 * Math.Sin(_tick / 11.0) + 9 * Math.Sin(_tick / 3.1) + 5 * (Noise(_tick, 1) - 0.5), 6, 97);

        // Share the load out by weight, with each process wobbling a little from sample to sample.
        var busy = _processes.Where(s => !s.Process.IsSuspended).ToList();
        double raw = busy.Sum(s => s.Weight * (0.55 + 0.9 * Noise(_tick, s.Process.Pid)));
        double scale = raw <= 0 ? 0 : load * 0.93 / raw;
        foreach (var s in _processes)
        {
            var p = s.Process;
            double cpu = p.IsSuspended ? 0 : s.Weight * (0.55 + 0.9 * Noise(_tick, p.Pid)) * scale;
            if (p.IsRestrained) cpu *= 0.6;
            if (p.CpuLimitPercent is double cap) cpu = Math.Min(cpu, cap * 0.92);
            p.CpuPercent = Math.Round(cpu, 1);
            p.IsForeground = p.Pid == _foreground;
        }

        var cores = new List<double>(StandInData.Logical);
        for (int i = 0; i < StandInData.Logical; i++)
        {
            // P-core threads carry more of the work than the E-cores; each wanders on its own phase.
            double bias = i < 16 ? 1.15 : 0.7;
            double wave = Math.Sin(_tick / (4.0 + i % 5) + i);
            cores.Add(Math.Round(Math.Clamp(load * bias + 14 * wave + 10 * (Noise(_tick, 100 + i) - 0.5), 1, 100), 1));
        }

        double memory = Math.Round(60 + 4 * Math.Sin(_tick / 17.0) + 1.5 * Math.Sin(_tick / 5.3), 1);
        var active = _plans.FirstOrDefault(p => p.Id == _activePlan);
        return new EngineSnapshot
        {
            TakenAtUtc = DateTime.UtcNow,
            EngineUptime = DateTime.UtcNow - _engineStarted,
            CpuPercent = Math.Round(load, 1),
            CoreCpuPercent = cores,
            ResponsivenessPercent = Math.Round(Math.Clamp(100 - Math.Max(0, load - 70) * 0.5 - 3 * Noise(_tick, 7), 40, 100), 1),
            MemoryPercent = memory,
            TotalMemoryBytes = StandInData.TotalMemoryBytes,
            AvailableMemoryBytes = (long)(StandInData.TotalMemoryBytes * (1 - memory / 100)),
            LogicalProcessors = StandInData.Logical,
            ProBalanceEnabled = _settings.ProBalanceEnabled,
            ProBalancePaused = false,
            ProBalanceRestrained = _processes.Count(s => s.Process.IsRestrained),
            ProBalanceRestraintsTotal = 41 + _processes.Count(s => s.Process.IsRestrained),
            ForegroundBoostEnabled = _settings.ForegroundBoostEnabled,
            ForegroundPid = _foreground,
            PerformanceModeActive = _performanceMode,
            PowerPlanOwner = _planOwner,
            ActivePowerPlanId = _activePlan,
            ActivePowerPlanName = active?.Name ?? string.Empty,
            EngineIsElevated = true,
            CallerIsAdministrator = _admin,
            ProcessRuleCount = _rules.Count,
            WatchdogRuleCount = _watchdog.Count(r => r.IsEnabled),
            Processes = _processes.Select(s => s.Process).ToList(),
        };
    }

    private PowerStateDto PowerState() => new()
    {
        Plans = _plans,
        ActivePlanId = _activePlan,
        PerformanceMode = _performanceMode,
        PerformanceModeAutomatic = false,
        Owner = _planOwner,
    };

    /// <summary>A typical machine: 0.5-15.625 ms, with a browser holding 1 ms unless the engine asks for finer.</summary>
    private TimerResolutionDto TimerState()
    {
        int requested = _settings.SetTimerResolutionAtStartup;
        int held = requested == 0 ? 0 : Math.Clamp(requested, StandInData.TimerFinest, StandInData.TimerCoarsest);
        return new TimerResolutionDto
        {
            Current = held == 0 ? 10000 : Math.Min(held, 10000),
            Minimum = StandInData.TimerCoarsest,
            Maximum = StandInData.TimerFinest,
            Requested = requested,
            Held = held,
            GlobalRequestsSupported = true,
            GlobalRequests = _timerGlobalRequests,
        };
    }

    private void SetPerformanceMode(bool on)
    {
        if (on == _performanceMode) return;
        _performanceMode = on;
        if (on)
        {
            _planBeforePerformanceMode = _activePlan;
            _activePlan = Guid.TryParse(_settings.PerformanceModePlanGuid, out var plan) ? plan : StandInData.HighPerformance;
            _planOwner = "PerformanceMode";
            Log(ActivityKind.Power, null, "Power plan changed to the Performance Mode plan.");
        }
        else
        {
            _activePlan = _planBeforePerformanceMode;
            _planOwner = "User";
            Log(ActivityKind.Power, null, "Performance Mode ended; power plan restored.");
        }
    }

    /// <summary>Deterministic pseudo-random number in [0, 1) from two integers, so every run draws the same curves.</summary>
    private static double Noise(int a, int b)
    {
        double x = Math.Sin(a * 12.9898 + b * 78.233) * 43758.5453;
        return x - Math.Floor(x);
    }

    // ------------------------------------------------------------------ rules

    private static string? ValidateRule(ProcessRuleDto rule)
    {
        if (string.IsNullOrWhiteSpace(rule.ProcessName)) return "A rule needs a process name";
        if (rule.PriorityClass is int pc && !Enum.IsDefined((ProcessPriorityClass)pc)) return "Invalid priority class";
        if (rule.CpuLimitPercent is double cap && (cap <= 0 || cap > 100)) return "CPU limit must be in (0, 100]";
        if (rule.AffinityMask is long mask && (mask & StandInData.AllCores) == 0) return "A rule's affinity needs at least one processor that exists.";
        if (rule.IoPriority is int io && io is < 0 or > 3) return "Invalid I/O priority";
        if (rule.MemoryPriority is int mem && mem is < 1 or > 5) return "Invalid memory priority";
        if (rule.GpuPriority is int gpu && gpu is < 0 or > 4) return "Invalid GPU priority";
        if (rule.GpuPreference is int pref && pref is < 0 or > 2) return "Invalid GPU preference";
        if (rule.PowerProfileGuid != null && !Guid.TryParse(rule.PowerProfileGuid, out _)) return "Invalid power plan GUID";
        if (!string.IsNullOrWhiteSpace(rule.CpuSetIds) && rule.CpuSetIds.Split(',').Any(id => !int.TryParse(id.Trim(), out _))) return "Invalid CPU Set IDs";
        if (rule.MaxInstances is int max && max < 1) return "Invalid instance limit";
        if (rule.ApplyPrioritiesAtCreation)
        {
            var name = rule.ProcessName.Trim();
            if (name.IndexOfAny(new[] { '*', '?', '\\', '/', ':' }) >= 0)
                return "Priorities at launch need a plain program name such as game.exe: Windows can't match a wildcard or a path there";
            if (rule.PriorityClass == (int)ProcessPriorityClass.RealTime)
                return "Windows doesn't apply Realtime priority at launch; pick another priority or switch off priorities at launch";
            if (rule.PriorityClass == null && rule.IoPriority == null && rule.MemoryPriority == null)
                return "Priorities at launch need a priority, I/O priority or memory priority to apply";
        }
        return null;
    }

    /// <summary>Sets <see cref="ProcessSnapshot.HasRule"/> on every process a persistent rule matches.</summary>
    private void MarkRuledProcesses()
    {
        var matchers = _rules.Select(r => Pattern(r.ProcessName)).ToList();
        foreach (var s in _processes)
        {
            var p = s.Process;
            string exe = p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Name : p.Name + ".exe";
            p.HasRule = matchers.Any(m => m.IsMatch(exe) || (p.FilePath != null && m.IsMatch(p.FilePath)));
        }
    }

    private static Regex Pattern(string rule)
    {
        string name = rule.Trim();
        if (!name.Contains('\\') && !name.Contains('*') && !name.Contains('?') && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return new Regex("^" + Regex.Escape(name).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // ------------------------------------------------------------------ helpers

    private bool CanTouch(ProcessSnapshot p) => _admin || p.UserName == Caller;
    private bool CanTouch(int pid) => _processes.FirstOrDefault(s => s.Process.Pid == pid) is { } s && CanTouch(s.Process);

    private bool TryTarget(CommandMessage cmd, out ProcessSnapshot process, out CommandResult error)
    {
        process = null!;
        error = null!;
        if (cmd.TargetPid is not int pid || pid <= 0)
        {
            error = CommandResult.Fail($"{cmd.CommandType} needs a process.");
            return false;
        }
        var found = _processes.FirstOrDefault(s => s.Process.Pid == pid)?.Process;
        if (found == null)
        {
            error = _admin ? CommandResult.Fail($"Process {pid} isn't running.") : Deny($"{CommandTypes.Describe(cmd.CommandType)} process {pid}: it belongs to another user, is elevated or protected, or is not running");
            return false;
        }
        if (!CanTouch(found))
        {
            error = Deny($"{CommandTypes.Describe(cmd.CommandType)} process {pid}: it belongs to another user, is elevated or protected, or is not running");
            return false;
        }
        process = found;
        return true;
    }

    private IEnumerable<int> Descendants(int pid)
    {
        var seen = new HashSet<int> { pid };
        var queue = new Queue<int>();
        queue.Enqueue(pid);
        while (queue.Count > 0)
        {
            int parent = queue.Dequeue();
            yield return parent;
            foreach (var child in _processes.Where(s => s.Process.ParentPid == parent).Select(s => s.Process.Pid))
            {
                if (seen.Add(child)) queue.Enqueue(child);
            }
        }
    }

    private static bool TryParseMask(string? text, out long mask)
    {
        mask = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask)
            : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask);
    }

    private static T Argument<T>(CommandMessage cmd) =>
        string.IsNullOrWhiteSpace(cmd.Argument)
            ? throw new ArgumentException($"{cmd.CommandType} needs an argument.")
            : JsonSerializer.Deserialize<T>(cmd.Argument, EngineClient.Json) ?? throw new ArgumentException($"{cmd.CommandType} needs an argument.");

    private static CommandResult Data(object value) => CommandResult.Ok(JsonSerializer.Serialize(value, value.GetType()));

    // ---- what a standard user may put in a rule: a simplified copy of the engine's RuleAuthorization

    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memory compression", "secure system", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "lsaiso", "dwm", "fontdrvhost", "proguard.engine", "proguard.ui"
    };

    private static string Bare(string name)
    {
        var file = System.IO.Path.GetFileName(name.Trim());
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }

    /// <summary>Some running instance of the program belongs to Windows or another user, so the caller couldn't end it.</summary>
    private bool HasInstanceCallerCantEnd(string name) =>
        _processes.Any(s => s.Process.IsSystem && string.Equals(s.Process.Name, Bare(name), StringComparison.OrdinalIgnoreCase));

    private string? RuleRefusal(ProcessRuleDto? rule)
    {
        if (_admin || rule == null) return null;
        if (CriticalNames.Contains(Bare(rule.ProcessName))) return $"change rules for {rule.ProcessName}, a system process";
        if (rule.PriorityClass == (int)ProcessPriorityClass.RealTime || rule.StartupBoostPriorityClass == (int)ProcessPriorityClass.RealTime)
            return "set Realtime priority";
        if (rule.ApplyPrioritiesAtCreation) return "apply priorities at launch: they are written to the machine-wide Image File Execution Options";
        if ((rule.DisallowExecution || rule.MaxInstances != null) && HasInstanceCallerCantEnd(rule.ProcessName))
            return $"make a rule that ends {rule.ProcessName}: some of its instances belong to Windows or another user";
        return null;
    }

    private string? WatchRefusal(WatchdogRuleDto? rule)
    {
        if (_admin || rule == null) return null;
        if (CriticalNames.Contains(Bare(rule.TargetProcessName))) return $"change rules for {rule.TargetProcessName}, a system process";
        if (rule.ActionType == WatchdogResponse.Launch) return "add a Watchdog Launch action: the program would start as another user";
        if (rule.ActionType == WatchdogResponse.SetPriority && string.Equals(rule.ActionArgument?.Trim(), "RealTime", StringComparison.OrdinalIgnoreCase))
            return "set Realtime priority";
        if (rule.ActionType is WatchdogResponse.Terminate or WatchdogResponse.Restart && HasInstanceCallerCantEnd(rule.TargetProcessName))
            return $"make a rule that ends {rule.TargetProcessName}: some of its instances belong to Windows or another user";
        return null;
    }

    private static CommandResult Deny(string what) =>
        CommandResult.Fail($"Access denied: {Caller} may not {what}. Run ProGuard as administrator to do this.");

    private void Mutate(Action change)
    {
        if (!_readOnly) change();
    }

    private void Log(ActivityKind kind, ProcessSnapshot? process, string message)
    {
        if (_readOnly) return;
        _activity.Insert(0, new ActivityEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Kind = kind,
            ProcessName = process?.Name ?? string.Empty,
            Pid = process?.Pid ?? 0,
            Message = message,
        });
    }
}
