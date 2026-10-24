using System.IO;
using Microsoft.Win32;
using ProGuard.Shared.Profiles;
using ProGuard.UI.Resources;

namespace ProGuard.UI.Services;

/// <summary>Where config files are exported to and imported from: Windows' file dialogs, or a stand-in in tests.</summary>
public interface IConfigFiles
{
    /// <summary>The path to write an export to, or null when the user cancelled.</summary>
    string? PickSavePath(string suggestedName);

    /// <summary>The file to import, or null when the user cancelled.</summary>
    string? PickOpenPath();
}

/// <summary>The standard Save and Open dialogs, filtered to config files.</summary>
public sealed class ConfigFileDialogs : IConfigFiles
{
    private static string Filter => string.Format(Strings.Config_FileFilter, ConfigDocuments.FileExtension);

    public string? PickSavePath(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.Config_ExportTitle,
            Filter = Filter,
            FileName = suggestedName,
            AddExtension = false,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickOpenPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Config_ImportTitle,
            Filter = Filter,
            CheckFileExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>A file name made from <paramref name="name"/>, with the characters Windows doesn't allow replaced.</summary>
    public static string SuggestName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return "ProGuard - " + (safe.Length == 0 ? "config" : safe) + ConfigDocuments.FileExtension;
    }
}
