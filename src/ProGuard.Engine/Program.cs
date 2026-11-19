using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Services;
using ProGuard.Core.Data;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Engine.Services.SessionAgents;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Logging;
using ProGuard.Engine.Security;

namespace ProGuard.Engine;

public class Program
{
    public static int Main(string[] args)
    {
        System.IO.Directory.SetCurrentDirectory(System.AppContext.BaseDirectory);

        // Before anything opens a file there: the logger and the database both create the folder when they start.
        var guard = DataDirectoryGuard.ForThisProcess();
        var dataDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(FileLoggerProvider.DefaultPath))!;
        var ownDirectoryUsable = guard.SecureOwnDirectory(dataDirectory);
        var databaseDirectory = System.IO.Path.GetDirectoryName(ProGuardDbContext.DatabasePath)!;
        var databaseUsable = SamePath(databaseDirectory, dataDirectory)
            ? ownDirectoryUsable
            : guard.CheckOtherDirectory(databaseDirectory);

        using var host = CreateHostBuilder(args, fileLogging: ownDirectoryUsable).Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProGuard.Engine.DataDirectory");
        foreach (var finding in guard.Findings)
            logger.Log(finding.Level, "{Finding}", finding.Message);
        if (!ownDirectoryUsable || !databaseUsable)
        {
            logger.LogCritical("The engine is not starting: its data folder isn't safe to use (see the messages above).");
            return 1;
        }

        host.Run();
        return 0;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(a)),
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(b)), System.StringComparison.OrdinalIgnoreCase);

    public static IHostBuilder CreateHostBuilder(string[] args) => CreateHostBuilder(args, fileLogging: true);

    /// <param name="fileLogging">False when the data folder couldn't be made safe: then nothing is written there.</param>
    public static IHostBuilder CreateHostBuilder(string[] args, bool fileLogging) =>
        Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                options.ServiceName = "ProGuard Engine Service";
            })
            .ConfigureServices((hostContext, services) =>
            {
                // Register Core Services
                services.AddDbContext<ProGuardDbContext>();
                
                services.AddSingleton<RuleStore>();
                services.AddSingleton<IRuleStore>(sp => sp.GetRequiredService<RuleStore>());

                services.AddSingleton<ForegroundTracker>();
                services.AddSingleton<IForegroundTracker>(sp => sp.GetRequiredService<ForegroundTracker>());
                // The UI's foreground reports also carry the user's idle time, which a session-0 service can't read.
                services.AddSingleton<IUserIdleSource>(sp => sp.GetRequiredService<ForegroundTracker>());
                // With no report at all, Responsiveness Guard spares every program that could be the one in use.
                services.AddSingleton<IForegroundFallback, GuiProgramFallback>();

                services.AddSingleton<ProcessMonitor>();
                services.AddSingleton<IProcessMonitor>(sp => sp.GetRequiredService<ProcessMonitor>());

                services.AddSingleton<ResponsivenessGuardEngine>();
                services.AddSingleton<IResponsivenessGuardEngine>(sp => sp.GetRequiredService<ResponsivenessGuardEngine>());

                services.AddSingleton<CpuLimiter>();
                services.AddSingleton<ICpuLimiter>(sp => sp.GetRequiredService<CpuLimiter>());
                services.AddSingleton<CpuAffinityLimiter>();

                services.AddSingleton<MemoryTrimService>(sp => new MemoryTrimService(
                    sp.GetRequiredService<ILogger<MemoryTrimService>>(), sp.GetRequiredService<IProcessMonitor>())
                {
                    // PowerManager owns the Performance Mode state; resolved lazily, at the first check.
                    IsPerformanceModeActive = () => sp.GetRequiredService<PowerManager>().IsPerformanceModeActive
                });
                services.AddSingleton<IMemoryTrimService>(sp => sp.GetRequiredService<MemoryTrimService>());

                services.AddSingleton<PowerManager>();
                services.AddSingleton<IPowerManager>(sp => sp.GetRequiredService<PowerManager>());

                services.AddSingleton<AutoPerformanceModeService>();
                services.AddSingleton<IAutoPerformanceMode>(sp => sp.GetRequiredService<AutoPerformanceModeService>());

                services.AddSingleton<IProcessActions, Win32ProcessActions>();
                services.AddSingleton<UserSessionLauncher>();
                services.AddSingleton<IUserSessionLauncher>(sp => sp.GetRequiredService<UserSessionLauncher>());
                services.AddSingleton<WatchdogEventLog>();
                services.AddSingleton<KeepRunningService>();
                services.AddSingleton<WatchdogService>();
                services.AddSingleton<IWatchdogService>(sp => sp.GetRequiredService<WatchdogService>());

                // Per-rule features the orchestrator drives as matching processes start and exit.
                services.AddSingleton<IRuleProcessAccess, Win32RuleProcessAccess>();
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<InstanceLimiter>();
                services.AddSingleton<InstanceSpread>();
                services.AddSingleton<StartupBoostService>();

                services.AddSingleton<IProcessTuningNative, Win32ProcessTuning>();
                // Factory: both constructors are resolvable (IEnumerable<IProcessRuleApplier> resolves to empty),
                // which the container rejects as ambiguous. The native one builds the real appliers.
                services.AddSingleton(sp =>
                {
                    var persistent = new PersistentRuleService(
                        sp.GetRequiredService<ILogger<PersistentRuleService>>(),
                        sp.GetRequiredService<IProcessTuningNative>(),
                        sp.GetRequiredService<IProcessMonitor>(),
                        sp.GetRequiredService<IRuleStore>());
                    // Re-applying a rule's CPU priority must not fight what holds it for a while: those put it back.
                    var responsivenessGuard = sp.GetRequiredService<IResponsivenessGuardEngine>();
                    var startupBoost = sp.GetRequiredService<StartupBoostService>();
                    var foregroundBoost = sp.GetRequiredService<ForegroundBoostService>();
                    persistent.PriorityAccess = sp.GetRequiredService<IRuleProcessAccess>();
                    persistent.PriorityHeld = pid => responsivenessGuard.RestrainedPids.Contains(pid) || startupBoost.IsBoosting(pid) || foregroundBoost.BoostedPid == pid;
                    return persistent;
                });

                services.AddSingleton(sp => new LaunchPriorityService(sp.GetRequiredService<ILogger<LaunchPriorityService>>()));

                services.AddSingleton<ProcessOrchestrator>();
                services.AddSingleton<IProcessOrchestrator>(sp => sp.GetRequiredService<ProcessOrchestrator>());
                
                services.AddSingleton<ProcessController>();
                services.AddSingleton<IProcessController>(sp => sp.GetRequiredService<ProcessController>());

                services.AddSingleton(sp => new TimerResolutionService(sp.GetRequiredService<ILogger<TimerResolutionService>>()));
                services.AddSingleton(sp => new CoreParkingService(sp.GetRequiredService<ILogger<CoreParkingService>>(), sp.GetRequiredService<IRuleStore>()));
                // Per-plan core parking and frequency; works with CoreParkingService rather than against it.
                services.AddSingleton(sp => new CpuTuningService(sp.GetRequiredService<ILogger<CpuTuningService>>(), coreParking: sp.GetRequiredService<CoreParkingService>()));
                services.AddSingleton<SettingsApplier>();

                services.AddSingleton<ForegroundBoostService>();

                // What the thin client asks for over the command pipe.
                services.AddSingleton<ActivityLog>();
                services.AddSingleton<ActivityFeed>();
                services.AddSingleton<IProcessDetailsReader, Win32ProcessDetailsReader>();
                services.AddSingleton<SnapshotBuilder>();
                services.AddSingleton<ProcessActionService>();
                services.AddSingleton<RuleService>();
                services.AddSingleton(_ => new EngineLogReader(FileLoggerProvider.DefaultPath));
                services.AddSingleton<LogQueryService>();
                services.AddSingleton<ThinClientCommands>();
                services.AddSingleton<IConfigProfileStore, ConfigProfileStore>();
                services.AddSingleton<ConfigProfileService>();
                services.AddSingleton<IpcDispatcher>();
                services.AddSingleton<PipeServer>();
                services.AddSingleton<ForegroundPipeServer>();

                // The session agents that report each desktop's foreground process and idle time, UI or no UI. A
                // service supervises every user session (and asks the SCM for session changes); an engine run from a
                // console only its own.
                var isService = WindowsServiceHelpers.IsWindowsService();
                services.AddSingleton(sp => new SessionAgentSupervisor(
                    sp.GetRequiredService<ILogger<SessionAgentSupervisor>>(),
                    new Win32SessionAgentPlatform(sp.GetRequiredService<UserSessionLauncher>()),
                    onlySession: isService ? null : System.Diagnostics.Process.GetCurrentProcess().SessionId));
                if (isService)
                    services.AddSingleton<IHostLifetime, SessionAwareServiceLifetime>();
                
                services.AddHostedService<EngineWorker>();
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.AddConsole();
                // A service has no console: keep a text log next to the database for diagnosis.
                if (fileLogging) logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultPath));
            });
}
