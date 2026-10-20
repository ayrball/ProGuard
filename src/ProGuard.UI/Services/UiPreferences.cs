using System.IO;
using Microsoft.Win32;

namespace ProGuard.UI.Services;

/// <summary>
/// This user's own choices about how the window behaves, kept in their part of the registry like Start with Windows.
/// An interface so tests never touch the real registry.
/// </summary>
public interface IUiPreferences
{
    /// <summary>
    /// Priorities, efficiency mode, cores and CPU caps set from the Processes page are also kept in the program's rule,
    /// so they come back whenever it starts. On unless the user turns it off.
    /// </summary>
    bool RememberPriorityChanges { get; set; }
}

/// <summary>Kept in memory only: the default for tests and the preview tool.</summary>
public sealed class MemoryUiPreferences : IUiPreferences
{
    public bool RememberPriorityChanges { get; set; } = true;
}

/// <summary>Kept under <c>HKCU\Software\ProGuard</c>.</summary>
public sealed class RegistryUiPreferences : IUiPreferences
{
    public const string RememberValue = "RememberPriorityChanges";

    public bool RememberPriorityChanges
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ElevationLauncher.SettingsKeyPath);
                return key?.GetValue(RememberValue) is not int on || on != 0; // missing means on
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return true;
            }
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(ElevationLauncher.SettingsKeyPath, writable: true);
            key.SetValue(RememberValue, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
