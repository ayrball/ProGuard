using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ProGuard.Core.Helpers;
using ProGuard.Engine.Services;
using ProGuard.Engine.Services.ProcessRules;

namespace ProGuard.Engine;

/// <summary>
/// <c>ProGuard.Engine.exe --cleanup-uninstall</c>: what the MSI runs, as LocalSystem, when ProGuard is removed (not
/// upgraded), to take out what the engine and UI put outside the folders Windows Installer knows about:
/// <list type="bullet">
/// <item>the Image File Execution Options <c>PerfOptions</c> the engine wrote for launch-time priorities (the keys marked
/// <see cref="RegistryLaunchPriorityRegistry.MarkerValue"/>), which would otherwise keep changing those programs'
/// priorities at every start;</item>
/// <item>the graphics preferences rules wrote to users' <c>UserGpuPreferences</c> (from the engine's record of them),
/// in every user hive that is loaded;</item>
/// <item>every loaded user hive's <c>Run</c> value that starts this copy's ProGuard.UI.exe;</item>
/// <item>the per-user sign-in tasks in Task Scheduler's <c>\ProGuard\</c> folder;</item>
/// <item>any hard CPU cap a crashed engine left on a process that is still running.</item>
/// </list>
/// Each step on its own: one that fails doesn't stop the rest. It always exits 0, so a cleanup problem never fails or
/// rolls back an uninstall. Users who aren't signed in during the uninstall keep their Run value and graphics
/// preferences: their hives aren't loaded, and are left alone rather than loaded behind their back.
/// </summary>
internal sealed class UninstallCleanup
{
    public const string Argument = "--cleanup-uninstall";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ProGuard.UI";

    public static bool IsRequested(string[] args) => args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));

    public required ILogger Logger { get; init; }
    public required ILaunchPriorityRegistry LaunchPriorities { get; init; }
    public required GpuPreferenceService GpuPreferences { get; init; }
    /// <summary>HKEY_USERS; a test key in tests.</summary>
    public required RegistryKey Users { get; init; }
    /// <summary>The folder ProGuard.UI.exe was installed to: only Run values that start it are removed.</summary>
    public required string InstallDirectory { get; init; }
    public required ISignInTaskFolder Tasks { get; init; }
    public CpuLimiter? CpuCaps { get; init; }

    /// <summary>The real thing, for <c>Main</c>. Always returns 0.</summary>
    public static int Run()
    {
        using var factory = LoggerFactory.Create(builder => builder.AddSimpleConsole());
        var logger = factory.CreateLogger("ProGuard.Engine.UninstallCleanup");
        try
        {
            var cleanup = new UninstallCleanup
            {
                Logger = logger,
                LaunchPriorities = new RegistryLaunchPriorityRegistry(),
                GpuPreferences = new GpuPreferenceService(new FileGpuPreferenceLedger(logger), logger: logger),
                Users = Registry.Users,
                InstallDirectory = AppContext.BaseDirectory,
                Tasks = new ComSignInTaskFolder(),
                CpuCaps = new CpuLimiter(factory.CreateLogger<CpuLimiter>(), new FileCpuCapRecoveryStore(logger)),
            };
            cleanup.RunAll();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Uninstall cleanup failed");
        }
        return 0;
    }

    /// <summary>Every step, each on its own.</summary>
    public void RunAll()
    {
        Step("launch priorities in Image File Execution Options", () => RemoveLaunchPriorities());
        Step("graphics preferences", () => GpuPreferences.RemoveAll());
        Step("start-at-sign-in Run values", () => RemoveRunValues());
        Step("sign-in tasks", () => Tasks.RemoveAll());
        Step("CPU limits left by a crash", () => CpuCaps?.RecoverFromLastRun() ?? 0);
    }

    private void Step(string what, Func<int> step)
    {
        try
        {
            Logger.LogInformation("Removed {Count} {What}", step(), what);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not remove the {What}; carrying on", what);
        }
    }

    /// <summary>Every PerfOptions key ProGuard marked as its own, whatever the rules say now.</summary>
    public int RemoveLaunchPriorities()
    {
        var removed = 0;
        foreach (var image in LaunchPriorities.ReadManaged().Keys)
        {
            try
            {
                LaunchPriorities.Remove(image);
                removed++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Logger.LogWarning(ex, "Could not remove the launch priorities for {Image}", image);
            }
        }
        return removed;
    }

    /// <summary>The <c>ProGuard.UI</c> Run value of every loaded user hive, where it starts this copy of ProGuard.UI.exe.</summary>
    public int RemoveRunValues()
    {
        var installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallDirectory)) + Path.DirectorySeparatorChar;
        var removed = 0;
        foreach (var sid in Users.GetSubKeyNames().Where(IsUserHive))
        {
            try
            {
                using var run = Users.OpenSubKey($@"{sid}\{RunKeyPath}", writable: true);
                if (run?.GetValue(RunValueName) is not string command) continue;
                // A portable copy elsewhere uses the same value name; it isn't this installation's to remove.
                if (!WatchdogArguments.TrySplitCommandLine(command, out var program, out _)) continue;
                if (!Path.IsPathRooted(program) || !Path.GetFullPath(program).StartsWith(installed, StringComparison.OrdinalIgnoreCase)) continue;
                run.DeleteValue(RunValueName, throwOnMissingValue: false);
                removed++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
            {
                Logger.LogWarning(ex, "Could not remove the start-at-sign-in value of {Sid}", sid);
            }
        }
        return removed;
    }

    /// <summary>A loaded user profile's hive: a local or domain account (S-1-5-21-…) or a Microsoft Entra one (S-1-12-1-…), not its _Classes twin.</summary>
    internal static bool IsUserHive(string name) =>
        (name.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase))
        && !name.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Task Scheduler's <c>\ProGuard\</c> folder, where the UI keeps each user's sign-in task (<c>SignInStartup</c>).</summary>
internal interface ISignInTaskFolder
{
    /// <summary>Deletes every task in the folder, then the folder. Returns how many tasks went.</summary>
    int RemoveAll();
}

/// <summary>
/// Through Task Scheduler's own COM interface (<c>Schedule.Service</c>), late-bound, so neither schtasks.exe nor
/// PowerShell is started and no interop assembly is needed.
/// </summary>
internal sealed class ComSignInTaskFolder : ISignInTaskFolder
{
    private const string FolderName = "ProGuard";
    private const int TASK_ENUM_HIDDEN = 1;

    public int RemoveAll()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            dynamic folder;
            try { folder = service.GetFolder(@"\" + FolderName); }
            catch (COMException) { return 0; } // no such folder: nothing to remove

            var names = new List<string>();
            foreach (dynamic task in folder.GetTasks(TASK_ENUM_HIDDEN)) names.Add((string)task.Name);
            foreach (var name in names) folder.DeleteTask(name, 0);
            service.GetFolder(@"\").DeleteFolder(FolderName, 0);
            return names.Count;
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }
}
