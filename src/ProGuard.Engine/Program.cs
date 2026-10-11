using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Services;
using ProGuard.Core.Data;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;
using ProGuard.Engine.IPC;
using ProGuard.Engine.Logging;

namespace ProGuard.Engine;

public class Program
{
    public static void Main(string[] args)
    {
        System.IO.Directory.SetCurrentDirectory(System.AppContext.BaseDirectory);
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
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

                services.AddSingleton<ProcessMonitor>();
                services.AddSingleton<IProcessMonitor>(sp => sp.GetRequiredService<ProcessMonitor>());

                services.AddSingleton<ProBalanceEngine>();
                services.AddSingleton<IProBalanceEngine>(sp => sp.GetRequiredService<ProBalanceEngine>());

                services.AddSingleton<CpuLimiter>();
                services.AddSingleton<ICpuLimiter>(sp => sp.GetRequiredService<CpuLimiter>());
                services.AddSingleton<CpuAffinityLimiter>();

                services.AddSingleton<SmartTrimService>(sp => new SmartTrimService(
                    sp.GetRequiredService<ILogger<SmartTrimService>>(), sp.GetRequiredService<IProcessMonitor>())
                {
                    // PowerManager owns the Performance Mode state; resolved lazily, at the first check.
                    IsPerformanceModeActive = () => sp.GetRequiredService<PowerManager>().IsPerformanceModeActive
                });
                services.AddSingleton<ISmartTrimService>(sp => sp.GetRequiredService<SmartTrimService>());

                services.AddSingleton<PowerManager>();
                services.AddSingleton<IPowerManager>(sp => sp.GetRequiredService<PowerManager>());

                services.AddSingleton<AutoPerformanceModeService>();
                services.AddSingleton<IAutoPerformanceMode>(sp => sp.GetRequiredService<AutoPerformanceModeService>());

                services.AddSingleton<IProcessActions, Win32ProcessActions>();
                services.AddSingleton<IUserSessionLauncher, UserSessionLauncher>();
                services.AddSingleton<WatchdogEventLog>();
                services.AddSingleton<KeepRunningService>();
                services.AddSingleton<WatchdogService>();
                services.AddSingleton<IWatchdogService>(sp => sp.GetRequiredService<WatchdogService>());

                // Per-rule features the governor drives as matching processes start and exit.
                services.AddSingleton<IRuleProcessAccess, Win32RuleProcessAccess>();
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<InstanceLimiter>();
                services.AddSingleton<InstanceBalancer>();
                services.AddSingleton<StartupBoostService>();

                services.AddSingleton<IProcessTuningNative, Win32ProcessTuning>();
                // Factory: both constructors are resolvable (IEnumerable<IProcessRuleApplier> resolves to empty),
                // which the container rejects as ambiguous. The native one builds the real appliers.
                services.AddSingleton(sp => new PersistentRuleService(
                    sp.GetRequiredService<ILogger<PersistentRuleService>>(),
                    sp.GetRequiredService<IProcessTuningNative>(),
                    sp.GetRequiredService<IProcessMonitor>(),
                    sp.GetRequiredService<IRuleStore>()));

                services.AddSingleton<ProcessGovernor>();
                services.AddSingleton<IProcessGovernor>(sp => sp.GetRequiredService<ProcessGovernor>());
                
                services.AddSingleton<ProcessController>();
                services.AddSingleton<IProcessController>(sp => sp.GetRequiredService<ProcessController>());

                services.AddSingleton<SettingsApplier>();

                services.AddSingleton<ForegroundBoostService>();

                // What the thin client asks for over the command pipe.
                services.AddSingleton<ActivityLog>();
                services.AddSingleton<ActivityFeed>();
                services.AddSingleton<IProcessDetailsReader, Win32ProcessDetailsReader>();
                services.AddSingleton<SnapshotBuilder>();
                services.AddSingleton<ProcessActionService>();
                services.AddSingleton<RuleService>();
                services.AddSingleton<ThinClientCommands>();
                services.AddSingleton<IpcDispatcher>();
                services.AddSingleton<PipeServer>();
                services.AddSingleton<ForegroundPipeServer>();
                
                services.AddHostedService<EngineWorker>();
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.AddConsole();
                // A service has no console: keep a text log next to the database for diagnosis.
                logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultPath));
            });
}
