using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace ProGuard.Engine.Services;

/// <summary>
/// A node of Valve's KeyValues text format (the format of Steam's <c>libraryfolders.vdf</c> and
/// <c>appmanifest_*.acf</c>): keys map to either a string or a nested node. Keys are case-insensitive, as in Steam.
/// </summary>
public sealed class VdfNode
{
    private readonly List<KeyValuePair<string, object>> _entries = new();

    public IReadOnlyList<KeyValuePair<string, object>> Entries => _entries;

    public string? GetString(string key) =>
        _entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase) && e.Value is string).Value as string;

    public VdfNode? GetNode(string key) =>
        _entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase) && e.Value is VdfNode).Value as VdfNode;

    internal void Add(string key, object value) => _entries.Add(new(key, value));

    /// <summary>Parses KeyValues text. Lenient: anything malformed ends the node it's in rather than throwing.</summary>
    public static VdfNode Parse(string text)
    {
        var tokens = Tokenize(text).GetEnumerator();
        return ParseNode(tokens, 0);
    }

    private static VdfNode ParseNode(IEnumerator<(string Text, bool Quoted)> tokens, int depth)
    {
        var node = new VdfNode();
        while (tokens.MoveNext())
        {
            var (key, keyQuoted) = tokens.Current;
            if (!keyQuoted && key == "}") return node;
            if (!keyQuoted && key == "{") return node;   // a block without a key: malformed
            if (!tokens.MoveNext()) return node;

            var (value, valueQuoted) = tokens.Current;
            if (!valueQuoted && value == "{")
            {
                if (depth > 64) return node;    // nothing Steam writes nests this deep
                node.Add(key, ParseNode(tokens, depth + 1));
            }
            else if (!valueQuoted && value == "}")
            {
                return node;
            }
            else
            {
                node.Add(key, value);
            }
        }
        return node;
    }

    private static IEnumerable<(string Text, bool Quoted)> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{' || c == '}') { yield return (c.ToString(), false); i++; continue; }
            if (c == '[')
            {
                // A conditional such as [$WIN32] after a value: not a key or a value, so skip it.
                while (i < text.Length && text[i] != ']') i++;
                i++;
                continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] });
                    }
                    else
                    {
                        sb.Append(text[i]);
                    }
                    i++;
                }
                i++;    // closing quote
                yield return (sb.ToString(), true);
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
            yield return (text[start..i], false);
        }
    }
}

/// <summary>A game installed in a Steam library.</summary>
public sealed record SteamGame(string AppId, string Name, string InstallPath);

/// <summary>
/// The games in this machine's Steam libraries: every library listed in <c>libraryfolders.vdf</c>, and in each one the
/// <c>appmanifest_*.acf</c> files that name a game and its folder under <c>steamapps\common</c>. Matching an exe
/// against these folders is more precise than the <c>\steamapps\common\</c> path heuristic: it skips Steam's own tools
/// and redistributables.
/// </summary>
public sealed class SteamLibraryIndex
{
    public static readonly SteamLibraryIndex Empty = new(Array.Empty<SteamGame>());

    // Steam apps that are tools rather than games.
    private static readonly HashSet<string> NonGameAppIds = new()
    {
        "228980",   // Steamworks Common Redistributables
        "250820",   // SteamVR
        "1070560", "1391110", "1628350",   // Steam Linux Runtime
    };

    public SteamLibraryIndex(IReadOnlyList<SteamGame> games)
    {
        Games = games;
    }

    public IReadOnlyList<SteamGame> Games { get; }

    /// <summary>The game whose install folder contains <paramref name="exePath"/>, if any.</summary>
    public SteamGame? FindGame(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        foreach (var game in Games)
        {
            var folder = game.InstallPath.TrimEnd('\\', '/') + "\\";
            if (exePath.Replace('/', '\\').StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return game;
        }
        return null;
    }

    /// <summary>Library folders listed in <c>libraryfolders.vdf</c>, in both the current format
    /// (<c>"0" { "path" "..." }</c>) and the old one (<c>"1" "D:\\SteamLibrary"</c>).</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string vdfText)
    {
        var root = VdfNode.Parse(vdfText);
        var folders = root.GetNode("libraryfolders") ?? root.GetNode("LibraryFolders") ?? root;
        var paths = new List<string>();
        foreach (var (key, value) in folders.Entries)
        {
            if (!key.All(char.IsDigit)) continue;      // skip "contentstatsid" and friends
            var path = value switch
            {
                VdfNode node => node.GetString("path"),
                string s => s,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
        }
        return paths;
    }

    /// <summary>The app an <c>appmanifest_*.acf</c> describes, or null if it isn't a game with an install folder.</summary>
    public static (string AppId, string Name, string InstallDir)? ParseAppManifest(string acfText)
    {
        var state = VdfNode.Parse(acfText).GetNode("AppState");
        var appId = state?.GetString("appid");
        var installDir = state?.GetString("installdir");
        if (appId == null || string.IsNullOrWhiteSpace(installDir)) return null;
        // installdir is a folder name under steamapps\common; anything else would match outside the library.
        if (installDir.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || installDir.Trim() is "." or "..") return null;
        var name = state!.GetString("name") ?? installDir;
        if (NonGameAppIds.Contains(appId) || name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase))
            return null;
        return (appId, name, installDir);
    }

    /// <summary>Reads the libraries of the Steam installed at <paramref name="steamRoot"/>.</summary>
    public static SteamLibraryIndex Load(string? steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot)) return Empty;

        var libraries = new List<string> { steamRoot };
        foreach (var vdf in new[] { Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"), Path.Combine(steamRoot, "config", "libraryfolders.vdf") })
        {
            try
            {
                if (File.Exists(vdf))
                    foreach (var folder in ParseLibraryFolders(File.ReadAllText(vdf)))
                        if (!libraries.Contains(folder, StringComparer.OrdinalIgnoreCase)) libraries.Add(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var games = new List<SteamGame>();
        foreach (var library in libraries)
        {
            var steamapps = Path.Combine(library, "steamapps");
            IEnumerable<string> manifests;
            try { manifests = Directory.Exists(steamapps) ? Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").ToList() : Enumerable.Empty<string>(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var manifest in manifests)
            {
                try
                {
                    if (ParseAppManifest(File.ReadAllText(manifest)) is { } app)
                        games.Add(new SteamGame(app.AppId, app.Name, Path.Combine(steamapps, "common", app.InstallDir)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return new SteamLibraryIndex(games);
    }

    /// <summary>Where Steam is installed, from the registry (machine-wide, since the engine runs as LocalSystem).</summary>
    public static string? FindSteamRoot()
    {
        foreach (var keyPath in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key?.GetValue("InstallPath") is string path && Directory.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }
}
