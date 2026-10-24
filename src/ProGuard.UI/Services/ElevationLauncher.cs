using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using ProGuard.UI.Resources;

namespace ProGuard.UI.Services;

public enum ElevationOutcome
{
    /// <summary>The elevated copy is starting; this one should quit.</summary>
    Started,
    /// <summary>The user said no at the UAC prompt; carry on as before.</summary>
    Cancelled,
    /// <summary>Windows couldn't start it; the error says why.</summary>
    Failed,
}

/// <summary>
/// Starts this window again with administrator rights, and keeps the user's choice to always do so. An interface so tests
/// never raise a real UAC prompt or touch the registry.
/// </summary>
public interface IElevationLauncher
{
    /// <summary>Whether this copy already runs with administrator rights.</summary>
    bool IsElevated { get; }

    /// <summary>The user's "Always start as administrator" choice: a copy with standard rights restarts itself at once.</summary>
    bool AlwaysElevate { get; set; }

    /// <summary>Starts an elevated copy of this program with <paramref name="arguments"/> (see <see cref="ElevationLauncher.PageArguments"/>).</summary>
    ElevationOutcome Restart(IReadOnlyList<string> arguments, out string? error);
}

/// <summary>
/// "Restart as administrator": the installed window starts with standard rights, and the engine then refuses what only an
/// administrator may do (Realtime, High I/O priority, Purge now, CPU tuning and so on). This starts the same exe through
/// the UAC prompt. Standard and elevated copies hold different single-instance names (<see cref="SingleInstance.NameFor"/>),
/// so the new copy starts even while this one is still open.
/// </summary>
public sealed class ElevationLauncher : IElevationLauncher
{
    /// <summary>Command-line switch naming the page the new copy opens on.</summary>
    public const string PageArgument = "--page";

    /// <summary>
    /// Where the "Always start as administrator" choice is kept: this user's own under HKCU, like starting at sign-in.
    /// The installer writes the same value under HKLM as the default for every user who hasn't chosen (see
    /// <see cref="AlwaysElevateFrom"/>).
    /// </summary>
    public const string SettingsKeyPath = @"Software\ProGuard";
    public const string AlwaysElevateValue = "StartAsAdministrator";

    /// <summary>The user's own choice wins; without one, the installer's default for the machine; without that, off.</summary>
    public static bool AlwaysElevateFrom(int? user, int? machine) => user is int u ? u != 0 : machine is int m && m != 0;

    private static int? ReadValue(RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(SettingsKeyPath);
            return key?.GetValue(AlwaysElevateValue) is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user declined the UAC prompt

    public bool IsElevated { get; } = CurrentIsElevated();

    public static bool CurrentIsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool AlwaysElevate
    {
        get => AlwaysElevateFrom(ReadValue(Registry.CurrentUser), ReadValue(Registry.LocalMachine));
        set
        {
            // Written either way, so turning it off holds even where the installer made it the default.
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
            key.SetValue(AlwaysElevateValue, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    /// <summary>The arguments that open the new copy on the page titled <paramref name="page"/>.</summary>
    public static string[] PageArguments(string? page) => string.IsNullOrWhiteSpace(page) ? Array.Empty<string>() : new[] { PageArgument, page };

    public ElevationOutcome Restart(IReadOnlyList<string> arguments, out string? error)
    {
        error = null;
        if (Environment.ProcessPath is not { } path)
        {
            error = Strings.Elev_NoPath;
            return ElevationOutcome.Failed;
        }

        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = true, // runas only works through the shell
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            return ElevationOutcome.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return ElevationOutcome.Cancelled;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            error = string.Format(Strings.Elev_Failed, ex.Message);
            return ElevationOutcome.Failed;
        }
    }

    /// <summary>The page named after <see cref="PageArgument"/> on a command line, or null.</summary>
    public static string? PageFrom(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], PageArgument, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }
}
