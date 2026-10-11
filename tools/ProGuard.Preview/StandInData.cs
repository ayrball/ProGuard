using System.Diagnostics;
using ProGuard.Shared.Contracts;
using ActivityKind = ProGuard.Shared.Contracts.ActivityKind;

namespace ProGuard.Preview;

/// <summary>
/// The canned machine the stand-in engine reports: a 24-thread hybrid desktop (8 P-cores with Hyper-Threading plus
/// 8 E-cores) running a believable mix of Windows services and user apps, with rules, watchdog rules, settings and
/// history chosen so every state and every field the UI can show appears somewhere.
/// </summary>
public static class StandInData
{
    public const string Me = @"DESKTOP-9F2K1TE\Tee";
    public const string SystemAccount = @"NT AUTHORITY\SYSTEM";
    public const string LocalService = @"NT AUTHORITY\LOCAL SERVICE";
    public const string NetworkService = @"NT AUTHORITY\NETWORK SERVICE";
    public const string Dwm = @"Window Manager\DWM-1";

    /// <summary>Logical processors: threads 0-15 are the P-cores' (two per core), 16-23 the E-cores'.</summary>
    public const int Logical = 24;
    public const long AllCores = 0xFFFFFF;
    public const long PCores = 0x00FFFF;
    public const long ECores = 0xFF0000;

    public const long TotalMemoryBytes = 32L * 1024 * 1024 * 1024;

    public static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Ultimate = Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>The window the user is working in (Visual Studio Code's main process).</summary>
    public const int ForegroundPid = 9420;

    /// <summary>The engine, which the UI must never be able to end.</summary>
    public const int EnginePid = 1888;

    private const long MB = 1024 * 1024;

    /// <summary>A process and the share of the machine's CPU it takes when it is busy, relative to the others.</summary>
    public sealed record Seed(ProcessSnapshot Process, double Weight);

    /// <param name="showPaths">False for a standard user: the engine hides the path of any process the caller doesn't own.</param>
    public static List<Seed> Processes(DateTime nowUtc, bool showPaths)
    {
        var list = new List<Seed>();

        void P(int pid, int ppid, string name, string? path, string user, double weight, double wsMb, int threads,
            ProcessPriorityClass priority = ProcessPriorityClass.Normal, int ageMinutes = 600, double? privateMb = null,
            Action<ProcessSnapshot>? tweak = null)
        {
            int session = user is Me or Dwm ? 1 : 0;
            var p = new ProcessSnapshot
            {
                Pid = pid,
                Name = name,
                FilePath = showPaths || user == Me ? path : null,
                ParentPid = ppid,
                SessionId = session,
                UserName = user,
                StartTimeUtc = nowUtc.AddMinutes(-ageMinutes),
                WorkingSetBytes = (long)(wsMb * MB),
                PrivateBytes = (long)((privateMb ?? wsMb * 0.8) * MB),
                ThreadCount = threads,
                Priority = priority,
                IoPriority = 2,
                MemoryPriority = 5,
                AffinityMask = AllCores,
                IsSystem = session == 0,
            };
            tweak?.Invoke(p);
            list.Add(new Seed(p, weight));
        }

        const string Sys = @"C:\Windows\System32\";
        const int Boot = 2900;

        // ---- Windows itself (session 0)
        P(4, 0, "System", null, SystemAccount, 0.5, 0.1, 236, ageMinutes: Boot);
        P(92, 4, "Registry", null, SystemAccount, 0.02, 58, 4, ageMinutes: Boot);
        P(392, 4, "smss", Sys + "smss.exe", SystemAccount, 0.01, 1.1, 2, ageMinutes: Boot);
        P(520, 504, "csrss", Sys + "csrss.exe", SystemAccount, 0.3, 5.4, 14, ProcessPriorityClass.High, Boot);
        P(612, 504, "wininit", Sys + "wininit.exe", SystemAccount, 0, 6.2, 3, ProcessPriorityClass.High, Boot);
        P(676, 612, "services", Sys + "services.exe", SystemAccount, 0.1, 9.8, 8, ageMinutes: Boot);
        P(696, 612, "lsass", Sys + "lsass.exe", SystemAccount, 0.2, 31, 11, ageMinutes: Boot);
        P(824, 676, "svchost", Sys + "svchost.exe", SystemAccount, 0.9, 28, 24, ageMinutes: Boot);
        P(884, 676, "svchost", Sys + "svchost.exe", NetworkService, 0.3, 21, 13, ageMinutes: Boot);
        P(1180, 676, "svchost", Sys + "svchost.exe", SystemAccount, 1.1, 64, 40, ageMinutes: Boot);
        P(1888, 676, "ProGuard.Engine", @"C:\Program Files\ProGuard\ProGuard.Engine.exe", SystemAccount, 0.8, 74, 21, ageMinutes: 192);
        P(2480, 676, "MsMpEng", @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.24090.11-0\MsMpEng.exe", SystemAccount, 9, 412, 41,
            ProcessPriorityClass.BelowNormal, 192, tweak: p => p.IsRestrained = true);
        P(2700, 676, "SearchIndexer", Sys + "SearchIndexer.exe", SystemAccount, 2.4, 96, 18, ProcessPriorityClass.Idle, Boot,
            tweak: p => { p.IsRestrained = true; p.IoPriority = 0; p.MemoryPriority = 1; });
        P(3120, 676, "audiodg", Sys + "audiodg.exe", LocalService, 0.6, 22, 9, ProcessPriorityClass.High, Boot);

        // ---- the desktop
        P(1112, 520, "dwm", Sys + "dwm.exe", Dwm, 3.5, 148, 29, ProcessPriorityClass.High, Boot);
        P(5120, 5040, "explorer", @"C:\Windows\explorer.exe", Me, 1.6, 286, 71, ageMinutes: 1500);
        P(5460, 676, "SearchHost", @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe", Me, 0.1, 34, 22,
            ageMinutes: 1500, tweak: p => p.IsEfficiencyMode = true);
        P(6010, 5120, "OneDrive", @"C:\Program Files\Microsoft OneDrive\OneDrive.exe", Me, 0.6, 88, 34, ageMinutes: 1500,
            tweak: p => p.IsEfficiencyMode = true);
        P(3300, 5120, "Dropbox", @"C:\Program Files (x86)\Dropbox\Client\Dropbox.exe", Me, 3.4, 160, 58, ageMinutes: 1500,
            tweak: p => p.CpuLimitPercent = 15);
        P(6320, 5120, "Spotify", @"C:\Users\Tee\AppData\Roaming\Spotify\Spotify.exe", Me, 1.4, 186, 62, ageMinutes: 400);

        // ---- Chrome: ProBalance has stepped on one noisy renderer
        const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        P(7040, 5120, "chrome", Chrome, Me, 1.2, 322, 48, ageMinutes: 300);
        P(7100, 7040, "chrome", Chrome, Me, 2.2, 468, 31, ageMinutes: 300);
        P(7168, 7040, "chrome", Chrome, Me, 3.1, 610, 22, ageMinutes: 298);
        P(7220, 7040, "chrome", Chrome, Me, 6, 244, 19, ProcessPriorityClass.BelowNormal, 120, tweak: p => p.IsRestrained = true);
        P(7388, 7040, "chrome", Chrome, Me, 2.5, 1210, 27, ageMinutes: 90);

        // ---- Discord: the rule caps the main process and keeps the whole tree on the E-cores
        const string Discord = @"C:\Users\Tee\AppData\Local\Discord\app-1.0.9172\Discord.exe";
        P(8812, 5120, "Discord", Discord, Me, 14, 212, 91, ProcessPriorityClass.BelowNormal, 400,
            tweak: p => { p.CpuLimitPercent = 10; p.AffinityMask = ECores; });
        P(8900, 8812, "Discord", Discord, Me, 0.8, 110, 17, ProcessPriorityClass.BelowNormal, 400, tweak: p => p.AffinityMask = ECores);
        P(8944, 8812, "Discord", Discord, Me, 1.1, 175, 21, ProcessPriorityClass.BelowNormal, 400, tweak: p => p.AffinityMask = ECores);

        // ---- the editor in the foreground, with Foreground Boost lifting it
        const string Code = @"C:\Users\Tee\AppData\Local\Programs\Microsoft VS Code\Code.exe";
        P(9420, 5120, "Code", Code, Me, 5.2, 296, 44, ProcessPriorityClass.AboveNormal, 240, tweak: p => p.IsForeground = true);
        P(9500, 9420, "Code", Code, Me, 2, 380, 36, ageMinutes: 240);
        P(9560, 9420, "Code", Code, Me, 1.5, 540, 28, ageMinutes: 240);

        // ---- the rest of the user's session
        P(9921, 5120, "obs64", @"C:\Program Files\obs-studio\bin\64bit\obs64.exe", Me, 4.8, 640, 60, ProcessPriorityClass.High, 75,
            tweak: p => p.IoPriority = 3);
        P(10444, 5120, "pwsh", @"C:\Program Files\PowerShell\7\pwsh.exe", Me, 0.1, 118, 24, ageMinutes: 60);
        P(10600, 10444, "dotnet", @"C:\Program Files\dotnet\dotnet.exe", Me, 5.5, 1180, 38, ageMinutes: 6);
        P(10300, 5120, "WindowsTerminal", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.2701.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe",
            Me, 0.5, 98, 33, ageMinutes: 60);
        P(11200, 10444, "ffmpeg", @"C:\Tools\ffmpeg\bin\ffmpeg.exe", Me, 14, 780, 56, ProcessPriorityClass.BelowNormal, 18,
            tweak: p => { p.IsAffinityLimited = true; p.AffinityMask = 0x0FFFFF; });
        P(4400, 5120, "Steam", @"C:\Program Files (x86)\Steam\steam.exe", Me, 0, 142, 47, ageMinutes: 1500, tweak: p => p.IsSuspended = true);
        P(4500, 4400, "steamwebhelper", @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe", Me, 0.2, 205, 29, ageMinutes: 1500);
        P(7600, 5120, "ms-teams", @"C:\Program Files\WindowsApps\MSTeams_24295.605.3225.8804_x64__8wekyb3d8bbwe\ms-teams.exe", Me, 0.9, 310, 63,
            ageMinutes: 800, tweak: p => p.IsEfficiencyMode = true);
        P(7700, 7600, "msedgewebview2", @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0.2849.46\msedgewebview2.exe", Me, 0.3, 120, 18,
            ageMinutes: 800, tweak: p => p.IsEfficiencyMode = true);
        P(12040, 5120, "Notepad", Sys + "notepad.exe", Me, 0, 24, 11, ageMinutes: 8);
        P(6644, 5120, "ProGuard.UI", @"C:\Program Files\ProGuard\ProGuard.UI.exe", Me, 1.3, 142, 39, ageMinutes: 190);

        return list.OrderBy(s => s.Process.Pid).ToList();
    }

    public static List<ProcessRuleDto> Rules() => new()
    {
        new()
        {
            Id = 1, ProcessName = "obs64.exe", PriorityClass = (int)ProcessPriorityClass.High, ExcludeFromProBalance = true,
            IoPriority = 3, MemoryPriority = 5, GpuPriority = 4, GpuPreference = 2, DisablePriorityBoost = true,
        },
        new()
        {
            Id = 2, ProcessName = "Discord.exe", PriorityClass = (int)ProcessPriorityClass.BelowNormal, CpuLimitPercent = 10, AffinityMask = ECores,
            EfficiencyMode = true, IoPriority = 1, ReapplyIfChanged = true,
        },
        new()
        {
            Id = 3, ProcessName = "SearchIndexer.exe", PriorityClass = (int)ProcessPriorityClass.Idle, IoPriority = 0, MemoryPriority = 1,
            EfficiencyMode = true, ApplyPrioritiesAtCreation = true,
        },
        new()
        {
            Id = 4, ProcessName = "ffmpeg.exe", PriorityClass = (int)ProcessPriorityClass.BelowNormal,
            CpuLimitThresholdPercent = 85, CpuLimitOverThresholdSeconds = 10, CpuLimitCoresToRemove = 4, CpuLimitDurationSeconds = 30,
        },
        new()
        {
            Id = 5, ProcessName = "HandBrakeCLI.exe", MaxInstances = 2, MaxInstancesAllSessions = true,
            InstanceBalancerMode = BalancerMode.EqualCoresPerInstance, InstanceBalancerAvoidSmt = true,
            InstanceBalancerReservedCores = 2, InstanceBalancerMinCpuPercent = 5,
        },
        new()
        {
            Id = 6, ProcessName = "blender.exe", InstanceBalancerMode = BalancerMode.FixedCoresPerInstance, InstanceBalancerCoresPerInstance = 8,
            MaxInstances = 3,
        },
        new()
        {
            Id = 7, ProcessName = "Cyberpunk2077.exe", PriorityClass = (int)ProcessPriorityClass.Normal,
            StartupBoostPriorityClass = (int)ProcessPriorityClass.High, StartupBoostSeconds = 60,
            PowerProfileGuid = HighPerformance.ToString(), CpuSetIds = string.Join(",", Enumerable.Range(256, 16)),
            GpuPriority = 4, GpuPreference = 2, EfficiencyMode = false, ReapplyIfChanged = true, DisablePriorityBoost = true,
        },
        new() { Id = 8, ProcessName = "cryptominer.exe", DisallowExecution = true },
        new() { Id = 9, ProcessName = "msedgewebview2.exe", MaxInstances = 6, MaxInstancesAllSessions = true, EfficiencyMode = true },
        new() { Id = 10, ProcessName = "chrome.exe", PriorityClass = (int)ProcessPriorityClass.Normal, IoPriority = 2, MemoryPriority = 5 },
        new() { Id = 11, ProcessName = "syncthing.exe", KeepRunning = true, EfficiencyMode = true, PriorityClass = (int)ProcessPriorityClass.BelowNormal },
        new() { Id = 12, ProcessName = "Plex Media Server.exe", KeepRunning = true, PriorityClass = (int)ProcessPriorityClass.BelowNormal, IoPriority = 1 },
        new() { Id = 13, ProcessName = "Unity*.exe", PriorityClass = (int)ProcessPriorityClass.AboveNormal, GpuPreference = 2 },
        new()
        {
            Id = 14, ProcessName = @"C:\Games\Steam\steamapps\common\*\*.exe", PowerProfileGuid = Ultimate.ToString(),
            StartupBoostPriorityClass = (int)ProcessPriorityClass.AboveNormal, StartupBoostSeconds = 30,
        },
    };

    public static List<WatchdogRuleDto> WatchdogRules() => new()
    {
        new()
        {
            Id = 1, TargetProcessName = "chrome.exe", ConditionType = WatchdogTrigger.RamAbove, ThresholdValue = 4096, DurationSeconds = 60,
            ActionType = WatchdogResponse.LogAlert, MemoryMetric = MemoryMetric.PrivateBytes, CooldownSeconds = 300,
        },
        new()
        {
            Id = 2, TargetProcessName = "MsMpEng.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 40, DurationSeconds = 30,
            ActionType = WatchdogResponse.SetPriority, ActionArgument = "BelowNormal", ActionDurationSeconds = 300,
        },
        new()
        {
            Id = 3, TargetProcessName = "leaky.exe", IsEnabled = false, ConditionType = WatchdogTrigger.InstanceCountAbove, ThresholdValue = 3,
            DurationSeconds = 0, ActionType = WatchdogResponse.Terminate,
        },
        new()
        {
            Id = 4, TargetProcessName = "ffmpeg.exe", ConditionType = WatchdogTrigger.RunningLongerThan, ThresholdValue = 7200, DurationSeconds = 0,
            ActionType = WatchdogResponse.Notify, ActionArgument = "ffmpeg has been encoding for over two hours.",
        },
        new()
        {
            Id = 5, TargetProcessName = "ms-teams.exe", ConditionType = WatchdogTrigger.RamAbove, ThresholdValue = 1500, DurationSeconds = 60,
            ActionType = WatchdogResponse.TrimWorkingSet, ActionArgument = "continuous",
        },
        new()
        {
            Id = 6, TargetProcessName = "Discord.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 30, DurationSeconds = 10,
            ActionType = WatchdogResponse.SetAffinity, ActionArgument = "0xFF0000", ActionDurationSeconds = 120,
        },
        new()
        {
            Id = 7, TargetProcessName = "SearchIndexer.exe", ConditionType = WatchdogTrigger.CpuAbove, ThresholdValue = 25, DurationSeconds = 300,
            ActionType = WatchdogResponse.SetIoPriority, ActionArgument = "0", ActionDurationSeconds = 600,
        },
        new()
        {
            Id = 8, TargetProcessName = "Plex Media Server.exe", ConditionType = WatchdogTrigger.RamAbove, ThresholdValue = 6000, DurationSeconds = 60,
            ActionType = WatchdogResponse.Restart, MemoryMetric = MemoryMetric.WorkingSet, CooldownSeconds = 900,
        },
        new()
        {
            Id = 9, TargetProcessName = "Backup.exe", ConditionType = WatchdogTrigger.RunningLongerThan, ThresholdValue = 3600, DurationSeconds = 0,
            ActionType = WatchdogResponse.Launch, ActionArgument = @"C:\Tools\notify.cmd --job backup --state stuck",
        },
    };

    public static EngineSettingsDto Settings() => new()
    {
        ProBalanceEnabled = true,
        ProBalanceSensitivity = 6,
        ProBalanceCpuThreshold = 25,
        ProBalanceRecoveryThreshold = 10,
        ProBalanceLowerIoPriority = true,
        ProBalanceUseEfficiencyMode = true,
        ProBalanceDisableWhenSysIdle = true,
        ProBalanceDisableIfSysIdleMs = 30000,
        ForegroundBoostEnabled = true,
        SmartTrimEnabled = true,
        SmartTrimIntervalSeconds = 900,
        SmartTrimMemoryThresholdMB = 512,
        SmartTrimMinimumRamLoadPercent = 65,
        SmartTrimExclusions = "explorer.exe;dwm.exe;ProGuard.UI.exe",
        SmartTrimLogNoAction = false,
        StandbyPurgeEnabled = true,
        StandbyPurgeFreeRamThresholdMB = 1024,
        StandbyPurgeOnlyInPerformanceMode = true,
        FileCachePurgeEnabled = false,
        IdleSaverEnabled = true,
        IdleTimeoutMinutes = 15,
        IdlePowerPlanGuid = PowerSaver.ToString(),
        PerformanceModePlanGuid = HighPerformance.ToString(),
        AutoPerformanceModeProcesses = @"Cyberpunk2077.exe;eldenring.exe;*\steamapps\common\*",
        AutoPerformanceModeExclusions = "launcher.exe",
        AutoPerformanceModeDetectGames = true,
        PerformanceModeEngageForSteam = false,
        LoadSwitcherEnabled = false,
        LoadSwitcherLowThreshold = 10,
        LoadSwitcherHighThreshold = 50,
        LoadSwitcherAveragingWindowSamples = 5,
        LoadSwitcherMinimumTimeInPlanMs = 5000,
        LoadSwitcherLowPlanGuid = Balanced.ToString(),
        LoadSwitcherHighPlanGuid = HighPerformance.ToString(),
        LoadSwitcherDeferLowPlan = true,
        StartWithPowerPlanGuid = null,
        StartWithWindows = true,
        MinimizeToTray = true,
        Theme = "System",
        PollingIntervalMs = 1000,
    };

    public static List<PowerPlanDto> PowerPlans() => new()
    {
        new() { Id = Balanced, Name = "Balanced" },
        new() { Id = HighPerformance, Name = "High performance" },
        new() { Id = PowerSaver, Name = "Power saver" },
        new() { Id = Ultimate, Name = "Ultimate Performance" },
    };

    /// <summary>Hybrid layout: IDs start at 256 as Windows numbers them; two E-cores are parked.</summary>
    public static List<CpuSetDto> CpuSets()
    {
        var sets = new List<CpuSetDto>();
        for (int i = 0; i < Logical; i++)
        {
            bool performance = i < 16;
            sets.Add(new CpuSetDto
            {
                Id = 256 + i,
                Group = 0,
                LogicalProcessorIndex = i,
                CoreIndex = performance ? i / 2 : 8 + (i - 16),
                NumaNodeIndex = 0,
                EfficiencyClass = performance ? 1 : 0,
                IsParked = i >= 22,
            });
        }
        return sets;
    }

    /// <summary>History spanning the engine's life, newest first once sorted, with every <see cref="ActivityKind"/> present.</summary>
    public static List<ActivityEntry> Activity(DateTime nowUtc)
    {
        var list = new List<ActivityEntry>();
        void A(int secondsAgo, ActivityKind kind, string name, int pid, string message) =>
            list.Add(new ActivityEntry { TimestampUtc = nowUtc.AddSeconds(-secondsAgo), Kind = kind, ProcessName = name, Pid = pid, Message = message });

        A(9, ActivityKind.Restrained, "MsMpEng", 2480, "CPU 6.4% while the machine was at 91%. Lowered to Below normal.");
        A(31, ActivityKind.Manual, "Dropbox", 3300, "CPU capped at 15%.");
        A(47, ActivityKind.Rule, "ffmpeg", 11200, "CPU Limiter: over 85% for 10s, removed 4 cores for 30s.");
        A(88, ActivityKind.Restrained, "chrome", 7220, "CPU 5.8% while the machine was at 88%. Lowered to Below normal.");
        A(140, ActivityKind.Watchdog, "chrome", 7388, "Memory above 4096 MB for 60s. Logged an alert.");
        A(205, ActivityKind.Memory, "", 0, "SmartTrim trimmed 31 processes and freed 1.4 GB.");
        A(262, ActivityKind.Restored, "SearchIndexer", 2700, "Load back under 50%. Priority kept at Idle by its rule.");
        A(340, ActivityKind.Rule, "Discord", 8812, "Rule applied: CPU cap 10%, cores 16-23, priority Below normal.");
        A(410, ActivityKind.Manual, "Steam", 4400, "Paused.");
        A(515, ActivityKind.Restored, "chrome", 7168, "Load back under 50%. Priority restored to Normal.");
        A(640, ActivityKind.Watchdog, "ms-teams", 7600, "Memory above 1500 MB for 60s. Working set trimmed.");
        A(780, ActivityKind.Power, "", 0, "Power plan changed to Balanced (Performance Mode ended).");
        A(905, ActivityKind.Rule, "obs64", 9921, "Rule applied: priority High, I/O priority High, left out of ProBalance.");
        A(1190, ActivityKind.Rule, "Cyberpunk2077", 13760, "Startup boost: High for the first 60s, then back to Normal.");
        A(1260, ActivityKind.Power, "", 0, "Power plan changed to High performance (Performance Mode: Cyberpunk2077.exe started).");
        A(1480, ActivityKind.Restrained, "MsMpEng", 2480, "CPU 7.9% while the machine was at 94%. Lowered to Below normal.");
        A(1710, ActivityKind.Watchdog, "Plex Media Server", 5284, "Keep Running relaunched Plex Media Server.exe after it exited.");
        A(1950, ActivityKind.Manual, "notepad", 8116, "Ended.");
        A(2300, ActivityKind.Restored, "MsMpEng", 2480, "Load back under 50%. Priority restored to Normal.");
        A(2650, ActivityKind.Memory, "", 0, "Purge now: standby list purged, 2.1 GB freed.");
        A(3020, ActivityKind.Manual, "Discord", 8812, "CPU capped at 10%.");
        A(3480, ActivityKind.Rule, "msedgewebview2", 7700, "Instance limit: 6 allowed, one blocked from starting.");
        A(3900, ActivityKind.Power, "", 0, "Power plan changed to Power saver (idle for 15 minutes).");
        A(4400, ActivityKind.Restrained, "dotnet", 10600, "CPU 8.2% while the machine was at 86%. Lowered to Below normal.");
        A(4820, ActivityKind.Restored, "dotnet", 10600, "Load back under 50%. Priority restored to Normal.");
        A(5300, ActivityKind.Watchdog, "MsMpEng", 2480, "CPU above 40% for 30s. Priority set to Below normal for 5 minutes.");
        A(6100, ActivityKind.System, "", 0, "Settings saved.");
        A(7400, ActivityKind.Memory, "", 0, "SmartTrim trimmed 28 processes and freed 1.1 GB.");
        A(8200, ActivityKind.Rule, "SearchIndexer", 2700, "Rule applied: priority Idle, I/O priority Very low, Efficiency Mode on.");
        A(9400, ActivityKind.System, "", 0, "Power plan recovery: restored Balanced after the engine stopped during a switch.");
        A(11500, ActivityKind.System, "", 0, "Engine started with administrator rights.");

        return list.OrderByDescending(a => a.TimestampUtc).ToList();
    }
}
