using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProGuard.UI.IPC;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;
using ProGuard.UI.Views;
using System.Windows;
using Wpf.Ui;

namespace ProGuard.UI;

public partial class App : Application
{
    static App()
    {
        System.IO.Directory.SetCurrentDirectory(System.AppContext.BaseDirectory);
    }
    private static readonly IHost _host = Host
        .CreateDefaultBuilder()
        .ConfigureServices((context, services) =>
        {
            services.AddHostedService<ApplicationHostService>();
            services.AddHostedService<ForegroundReporter>();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<ISnackbarService, SnackbarService>();
            
            services.AddSingleton<MainWindow>();
            services.AddSingleton<MainViewModel>();
            
            services.AddSingleton<DashboardPage>();
            services.AddSingleton<DashboardViewModel>();
            
            services.AddSingleton<ProcessListPage>();
            services.AddSingleton<ProcessListViewModel>();
            
            services.AddSingleton<RulesPage>();
            services.AddSingleton<RulesViewModel>();
            
            services.AddSingleton<WatchdogPage>();
            services.AddSingleton<WatchdogViewModel>();
            
            services.AddSingleton<EngineProxy>();

            services.AddSingleton<PowerPage>();
            services.AddSingleton<PowerViewModel>();
            
            services.AddSingleton<LogsPage>();
            services.AddSingleton<LogsViewModel>();
            
            services.AddSingleton<SettingsPage>();
            services.AddSingleton<SettingsViewModel>();
            
            services.AddSingleton<TrayIconService>();
        }).Build();

    public static T GetService<T>() where T : class
    {
        return (_host.Services.GetService(typeof(T)) as T)!;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            System.IO.File.WriteAllText("crash.log", args.ExceptionObject?.ToString());
        };
        DispatcherUnhandledException += (s, args) =>
        {
            System.IO.File.WriteAllText("dispatcher_crash.log", args.Exception.ToString());
            args.Handled = true;
        };

        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            await _host.StartAsync();

            var mainWindow = GetService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();

            ShutdownMode = ShutdownMode.OnMainWindowClose;

            try
            {
                var trayIcon = GetService<TrayIconService>();
                trayIcon.Initialize();
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("tray_error.log", ex.ToString());
            }
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText("startup_error.log", ex.ToString());
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}
