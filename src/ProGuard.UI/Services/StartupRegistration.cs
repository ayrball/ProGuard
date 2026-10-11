using System.IO;
using Microsoft.Win32;

namespace ProGuard.UI.Services;

/// <summary>
/// Whether ProGuard's window starts when this user signs in to Windows. This is the user's own setting for their own
/// account, so it lives in the UI (not the engine): the engine is a service and can't start a program on a desktop.
/// An interface so tests never touch the real registry.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>True when this user's startup list launches this copy of ProGuard.UI.exe.</summary>
    bool IsEnabled { get; }

    void Enable();
    void Disable();
}

/// <summary>Starts ProGuard.UI.exe through <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.</summary>
public sealed class RunKeyStartup : IStartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ProGuard.UI";

    /// <summary>Started at sign-in, ProGuard goes straight to the tray instead of opening its window.</summary>
    public const string TrayArgument = "--tray";

    private readonly string _exePath;

    public RunKeyStartup(string? exePath = null)
    {
        _exePath = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("The path of ProGuard.UI.exe is unknown.");
    }

    /// <summary>The Run value's data: the executable path in quotes (so a path with spaces still launches) and the tray flag.</summary>
    public static string CommandFor(string exePath) => $"\"{exePath}\" {TrayArgument}";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                // A value that points somewhere else (an older install, a moved folder) doesn't start this copy.
                return key?.GetValue(ValueName) is string command
                    && string.Equals(command, CommandFor(_exePath), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, CommandFor(_exePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
