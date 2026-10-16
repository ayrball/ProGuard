using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection.PortableExecutable;
using ProGuard.Core.Helpers;

namespace ProGuard.Engine.Services;

/// <summary>
/// What ProBalance falls back on when nobody has said which program the user is using (no session agent or UI
/// reporting): rather than restraining whatever it likes, including the game in use, it leaves alone every process
/// that could be that program.
/// </summary>
public interface IForegroundFallback
{
    /// <summary>True while a foreground report is current, or the engine can see the desktop itself.</summary>
    bool IsForegroundKnown { get; }

    /// <summary>Whether a process in a user session (never asked about session 0) could be the program in use.</summary>
    bool MightBeForeground(int pid, string? imagePath);
}

/// <summary>
/// <see cref="IForegroundFallback"/> for the engine: a process could be the program in use if it is a Windows GUI
/// program (PE subsystem <c>WINDOWS_GUI</c>), the kind that owns the top-level windows people work in.
/// <para>
/// Asking for the visible top-level windows themselves isn't possible from a service: <c>EnumWindows</c> and
/// <c>EnumDesktopWindows</c> only reach window stations in the caller's own session, and session 0 has none of the
/// user's. The subsystem is a property of the program file, so it can be read from anywhere, and it separates what
/// matters here: games, browsers and editors are GUI programs, while compilers, encoders and scripts run as console
/// programs whose window belongs to conhost or the terminal, so ProBalance still restrains them. A console program
/// that opens a window of its own (java.exe running a Swing app, say) isn't covered.
/// </para>
/// <para>
/// A program whose file can't be read counts as possibly in use. Answers are cached per path.
/// </para>
/// </summary>
public sealed class GuiProgramFallback : IForegroundFallback
{
    /// <summary>Plenty for every distinct program a machine runs; past it the cache starts over.</summary>
    private const int MaxCachedPrograms = 4096;

    private readonly ForegroundTracker _tracker;
    private readonly Func<int, string?> _imagePathOf;
    private readonly ConcurrentDictionary<string, bool> _isGui = new(StringComparer.OrdinalIgnoreCase);

    public GuiProgramFallback(ForegroundTracker tracker) : this(tracker, ProcessProbe.TryGetImagePath) { }

    internal GuiProgramFallback(ForegroundTracker tracker, Func<int, string?> imagePathOf)
    {
        _tracker = tracker;
        _imagePathOf = imagePathOf;
    }

    public bool IsForegroundKnown => _tracker.IsForegroundKnown;

    public bool MightBeForeground(int pid, string? imagePath)
    {
        var path = string.IsNullOrEmpty(imagePath) ? _imagePathOf(pid) : imagePath;
        if (string.IsNullOrEmpty(path)) return true;

        if (_isGui.TryGetValue(path, out var known)) return known;
        var isGui = IsGuiProgram(path) ?? true;
        if (_isGui.Count >= MaxCachedPrograms) _isGui.Clear();
        _isGui[path] = isGui;
        return isGui;
    }

    /// <summary>Whether the program file is a Windows GUI program; null when it can't be read.</summary>
    internal static bool? IsGuiProgram(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(file);
            return pe.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsGui;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
        {
            return null;
        }
    }
}
