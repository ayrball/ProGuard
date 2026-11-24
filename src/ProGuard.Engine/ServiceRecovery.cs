using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ProGuard.Shared.Messages;

namespace ProGuard.Engine;

/// <summary>
/// <c>ProGuard.Engine.exe --configure-recovery</c>: what the MSI runs, as LocalSystem, after it has created the
/// ProGuardEngine service and before it starts it. It sets the service's recovery actions through
/// <c>ChangeServiceConfig2</c>: restart after 5 s, after 10 s, then every 60 s, and count the failures afresh after a
/// day without one. The same settings <c>install-service.ps1</c> gives a script install.
/// <para>
/// Why not the installer's own elements: Windows Installer's <c>MsiServiceConfigFailureActions</c> opens the service
/// without the start right that restart actions need, and fails with error 1939; WiX's <c>util:ServiceConfig</c> has
/// one restart delay for all three failures, so it can't say 5 s, 10 s, 60 s.
/// </para>
/// Exits 1 if the settings couldn't be written, which fails (and rolls back) the install, as before.
/// </summary>
internal static class ServiceRecovery
{
    public const string Argument = "--configure-recovery";

    public static bool IsRequested(string[] args) => args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));

    /// <summary>The service the MSI installs (<c>ServiceInstall Name</c> in Package.wxs).</summary>
    public const string ServiceName = PipeNames.Engine;

    /// <summary>The failure count starts again after a day without a failure.</summary>
    public static readonly TimeSpan ResetPeriod = TimeSpan.FromDays(1);

    /// <summary>Restart on the first, second and every later failure, after these delays.</summary>
    public static readonly IReadOnlyList<TimeSpan> RestartDelays = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60) };

    /// <summary>The real thing, for <c>Main</c>: 0 when the actions were set, 1 when not.</summary>
    public static int Run() => Run(new Win32ServiceFailureActions());

    internal static int Run(IServiceFailureActions service)
    {
        using var factory = LoggerFactory.Create(builder => builder.AddSimpleConsole());
        var logger = factory.CreateLogger("ProGuard.Engine.ServiceRecovery");
        try
        {
            service.Set(ServiceName, ResetPeriod, RestartDelays);
            logger.LogInformation("Recovery actions of {Service} set: restart after {Delays}; reset after {Reset}",
                ServiceName, string.Join(", ", RestartDelays), ResetPeriod);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not set the recovery actions of {Service}", ServiceName);
            return 1;
        }
    }
}

/// <summary>Writes a service's failure actions; a fake in tests.</summary>
internal interface IServiceFailureActions
{
    void Set(string serviceName, TimeSpan resetPeriod, IReadOnlyList<TimeSpan> restartDelays);
}

/// <summary>The Service Control Manager, through OpenSCManager, OpenService and ChangeServiceConfig2.</summary>
internal sealed class Win32ServiceFailureActions : IServiceFailureActions
{
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_START = 0x0010; // ChangeServiceConfig2 refuses restart actions without it
    private const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const int SC_ACTION_RESTART = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        public string? lpRebootMsg;
        public string? lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    public void Set(string serviceName, TimeSpan resetPeriod, IReadOnlyList<TimeSpan> restartDelays)
    {
        var manager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed");
        try
        {
            var service = OpenService(manager, serviceName, SERVICE_CHANGE_CONFIG | SERVICE_START);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenService({serviceName}) failed");
            try
            {
                var size = Marshal.SizeOf<SC_ACTION>();
                var actions = Marshal.AllocHGlobal(size * restartDelays.Count);
                try
                {
                    for (int i = 0; i < restartDelays.Count; i++)
                        Marshal.StructureToPtr(new SC_ACTION { Type = SC_ACTION_RESTART, Delay = (uint)restartDelays[i].TotalMilliseconds }, actions + i * size, false);
                    // Null reboot message and command: leave them as they are, as sc.exe failure does.
                    var info = new SERVICE_FAILURE_ACTIONS
                    {
                        dwResetPeriod = (uint)resetPeriod.TotalSeconds,
                        cActions = (uint)restartDelays.Count,
                        lpsaActions = actions,
                    };
                    if (!ChangeServiceConfig2(service, SERVICE_CONFIG_FAILURE_ACTIONS, ref info))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"ChangeServiceConfig2({serviceName}) failed");
                }
                finally
                {
                    Marshal.FreeHGlobal(actions);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref SERVICE_FAILURE_ACTIONS info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
