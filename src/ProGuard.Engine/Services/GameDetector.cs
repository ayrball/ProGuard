using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ProGuard.Core.Helpers;
using ProGuard.Core.Models;

namespace ProGuard.Engine.Services;

/// <summary>Settings of automatic Performance Mode (Process Lasso's [GamingMode]).</summary>
public sealed record AutoPerformanceModeOptions
{
    /// <summary>Processes that turn Performance Mode on while they run (AutomaticGamingModeProcessPaths).</summary>
    public IReadOnlyList<string> Processes { get; init; } = Array.Empty<string>();
    /// <summary>Processes that never do (AutomaticGamingModeUserExclusions).</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = Array.Empty<string>();
    /// <summary>Detect games by their install path.</summary>
    public bool DetectGames { get; init; }
    /// <summary>Games in the Steam libraries turn it on (GamingModeEngageForSteam).</summary>
    public bool EngageForSteam { get; init; }

    public bool AnyEnabled => Processes.Count > 0 || DetectGames || EngageForSteam;

    public static AutoPerformanceModeOptions FromSettings(AppSettings s) => new()
    {
        Processes = ParseList(s.AutoPerformanceModeProcesses),
        Exclusions = ParseList(s.AutoPerformanceModeExclusions),
        DetectGames = s.AutoPerformanceModeDetectGames,
        EngageForSteam = s.PerformanceModeEngageForSteam,
    };

    /// <summary>Splits a list stored as text: entries separated by ';' or new lines (not ',': paths can contain one).</summary>
    public static IReadOnlyList<string> ParseList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // Records compare lists by reference; settings reloads build new lists, so compare their contents.
    public bool Equals(AutoPerformanceModeOptions? other) =>
        other != null && DetectGames == other.DetectGames && EngageForSteam == other.EngageForSteam
        && Processes.SequenceEqual(other.Processes, StringComparer.OrdinalIgnoreCase)
        && Exclusions.SequenceEqual(other.Exclusions, StringComparer.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(DetectGames, EngageForSteam, Processes.Count, Exclusions.Count);
}

/// <summary>
/// Matches a process against a user's list entry. An entry with a path separator is matched against the full exe path
/// (a folder matches everything inside it); anything else against the exe name, with or without ".exe". '*' and '?'
/// are wildcards. Case-insensitive.
/// </summary>
public static class ProcessPattern
{
    public static bool Matches(string pattern, string? name, string? path)
    {
        pattern = pattern.Trim().Trim('"');
        if (pattern.Length == 0) return false;

        if (pattern.IndexOfAny(new[] { '\\', '/' }) >= 0)
        {
            if (string.IsNullOrEmpty(path)) return false;
            path = path.Replace('/', '\\');
            pattern = pattern.Replace('/', '\\');
            if (HasWildcards(pattern)) return Glob(pattern, path);
            return string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(pattern.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        var exeName = ProcessExtensions.NormalizeName(!string.IsNullOrEmpty(name) ? name : Path.GetFileName(path));
        if (exeName.Length == 0) return false;
        var normalized = ProcessExtensions.NormalizeName(pattern);
        return HasWildcards(normalized) ? Glob(normalized, exeName) : exeName == normalized;
    }

    private static bool HasWildcards(string s) => s.IndexOfAny(new[] { '*', '?' }) >= 0;

    private static bool Glob(string pattern, string input) =>
        Regex.IsMatch(input, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}

/// <summary>
/// Decides whether a running process should turn Performance Mode on, and why: the user's list, the Steam libraries
/// (<see cref="SteamLibraryIndex"/>), or the install-path heuristics Process Lasso uses for game launchers. The user's
/// exclusions win over all of them.
/// </summary>
public sealed class GameDetector
{
    /// <summary>Install folders that only hold games (Process Lasso's heuristics), as lower-case path fragments.</summary>
    internal static readonly string[] GamePathMarkers =
    {
        @"\steamapps\common\",
        @"\epic games\",
        @"\gog games\",
        @"\gog galaxy\games\",
        @"\ubisoft\",
        @"\battle.net\",
        @"\ea games\",
        @"\origin games\",
        @"\xboxgames\",
        @"\riot games\",
        @"\rockstar games\",
        @"\amazon games\library\",
    };

    /// <summary>Launchers, helpers and installers that live in those folders but aren't games.</summary>
    internal static readonly HashSet<string> NonGameNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "steamservice", "steamerrorreporter", "gameoverlayui", "gameoverlayui64",
        "epicgameslauncher", "epicwebhelper", "epiconlineservices", "epiconlineserviceshost", "epiconlineservicesuserhelper",
        "galaxyclient", "galaxyclient helper", "galaxycommunication", "gog galaxy notifications renderer",
        "upc", "uplaywebcore", "ubisoftconnect", "ubisoftgamelauncher", "ubisoftgamelauncher64", "upc_crashreporter",
        "battle.net", "battle.net helper", "agent", "blizzard error reporter", "blizzardbrowser",
        "eadesktop", "eabackgroundservice", "ealauncher", "eaconnect_microsoft", "origin", "originwebhelperservice", "originclientservice",
        "riotclientservices", "riotclientux", "riotclientuxrender", "riotclientcrashhandler", "vanguard", "vgtray",
        "launcher", "launcherpatcher", "rockstarservice", "rockstarerrorhandler", "socialclubhelper", "playgtav",
        "amazongames", "amazon games ui",
        "unitycrashhandler64", "unitycrashhandler32", "crashreportclient", "unrealcefsubprocess",
        "easyanticheat", "easyanticheat_eos", "easyanticheat_setup", "easyanticheat_eos_setup", "beservice", "beservice_x64",
        "dxsetup", "dotnetfx", "ue4prereqsetup_x64", "ueprereqsetup_x64",
    };

    /// <summary>Name fragments of helpers that games ship (crash reporters, uninstallers, redistributable installers).</summary>
    private static readonly string[] NonGameNameFragments =
    {
        "crashreport", "crashhandler", "crash_handler", "crashpad", "errorreporter", "unins", "redist", "installer",
        "updater", "webhelper", "cefsubprocess", "anticheat",
    };

    /// <summary>Folders inside game folders that hold installers rather than the game.</summary>
    private static readonly string[] NonGamePathFragments =
    {
        @"\_commonredist\", @"\commonredist\", @"\redist\", @"\redistributables\", @"\directx\", @"\__installer\",
        @"\steamworks shared\", @"\steamvr\",
    };

    private readonly Func<SteamLibraryIndex> _steamLibraries;

    public GameDetector(Func<SteamLibraryIndex> steamLibraries)
    {
        _steamLibraries = steamLibraries;
    }

    /// <summary>Why <paramref name="process"/> should turn Performance Mode on, or null if it shouldn't.</summary>
    public string? Match(ProcessInfo process, AutoPerformanceModeOptions options)
    {
        var name = process.Name;
        var path = process.FilePath;
        if (options.Exclusions.Any(e => ProcessPattern.Matches(e, name, path))) return null;

        var listed = options.Processes.FirstOrDefault(p => ProcessPattern.Matches(p, name, path));
        if (listed != null) return $"{Display(process)} is running (listed)";

        if (string.IsNullOrEmpty(path) || IsHelper(name, path)) return null;

        // Every Steam game lives under <library>\steamapps\common, so skip the library lookup for anything else.
        if (options.EngageForSteam && path.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)
            && _steamLibraries().FindGame(path) is { } game)
            return $"{Display(process)} is running (Steam game \"{game.Name}\")";

        if (options.DetectGames && LooksLikeGame(path))
            return $"{Display(process)} is running (detected game)";

        return null;
    }

    /// <summary>Whether the path is in a launcher's game folder (Process Lasso's heuristics).</summary>
    public static bool LooksLikeGame(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var lower = path.Replace('/', '\\').ToLowerInvariant();
        return GamePathMarkers.Any(lower.Contains) && !IsHelper(Path.GetFileNameWithoutExtension(lower), lower);
    }

    private static bool IsHelper(string? name, string path)
    {
        var exe = ProcessExtensions.NormalizeName(!string.IsNullOrEmpty(name) ? name : Path.GetFileName(path));
        if (NonGameNames.Contains(exe) || NonGameNameFragments.Any(exe.Contains)) return true;
        var lower = path.Replace('/', '\\').ToLowerInvariant();
        return NonGamePathFragments.Any(lower.Contains);
    }

    private static string Display(ProcessInfo p) =>
        string.IsNullOrEmpty(p.Name) ? Path.GetFileName(p.FilePath) ?? $"PID {p.Pid}" : $"{p.Name}.exe";
}
