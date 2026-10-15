using System;
using System.Diagnostics;

namespace ProGuard.Core.Helpers;

public static class ProcessExtensions
{
    public static string SafeGetProcessName(this Process process)
    {
        try { return process.ProcessName; } catch { return string.Empty; }
    }

    public static string? SafeGetFilePath(this Process process)
    {
        try { return process.MainModule?.FileName; } catch { return null; }
    }

    public static DateTime SafeGetStartTime(this Process process)
    {
        try { return process.StartTime; } catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// Canonical form used to compare process names. The UI stores rules as "chrome.exe" while
    /// <see cref="Process.ProcessName"/> (and therefore the engine's snapshots) is just "chrome".
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Rules are looked up by <see cref="NormalizeName"/> of the program's file name and nothing else, so a folder or
    /// a wildcard in a rule's name never matches a process. Null when <paramref name="name"/> is a plain file name,
    /// otherwise why it isn't, worded for the person who typed it.
    /// </summary>
    public static string? ProgramNameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null; // "needs a name" is reported separately
        if (name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            return $"Rules match a program's file name, like chrome.exe, not a folder or path: \"{name.Trim()}\" would never match anything.";
        if (name.IndexOfAny(new[] { '*', '?' }) >= 0)
            return $"Rules match one program's exact file name, like chrome.exe; wildcards aren't supported, so \"{name.Trim()}\" would never match anything.";
        return null;
    }

    public static string ToReadableSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double len = bytes;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    public static bool IsSystemProcess(this Process process)
    {
        string name = process.SafeGetProcessName().ToLowerInvariant();
        return name is "idle" or "system" or "svchost" or "smss" or "csrss" or "wininit" or "services" or "lsass";
    }

    public static int SafeGetParentPid(this Process process)
    {
        try
        {
            var pbi = new Native.PROCESS_BASIC_INFORMATION();
            int status = Native.Ntdll.NtQueryInformationProcess(process.Handle, 0, ref pbi, System.Runtime.InteropServices.Marshal.SizeOf(pbi), out _);
            if (status == 0)
            {
                return pbi.InheritedFromUniqueProcessId.ToInt32();
            }
        }
        catch { }
        return 0;
    }

    public static int SafeGetThreadCount(this Process process)
    {
        try { return process.Threads.Count; } catch { return 1; }
    }

    public static int SafeGetHandleCount(this Process process)
    {
        try { return process.HandleCount; } catch { return 0; }
    }
}
