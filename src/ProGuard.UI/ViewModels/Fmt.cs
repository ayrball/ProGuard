using System.Diagnostics;
using ProGuard.UI.Resources;

namespace ProGuard.UI.ViewModels;

/// <summary>Display formatting shared by every page. Terse, uppercase where it is a label.</summary>
internal static class Fmt
{
    public static string Bytes(long bytes)
    {
        const double K = 1024, M = K * 1024, G = M * 1024;
        return bytes switch
        {
            >= (long)G => $"{bytes / G:0.0} GB",
            >= (long)M => $"{bytes / M:0} MB",
            >= (long)K => $"{bytes / K:0} KB",
            _ => $"{bytes} B"
        };
    }

    public static string Priority(ProcessPriorityClass? priority) => priority switch
    {
        ProcessPriorityClass.Idle => Strings.Idle2,
        ProcessPriorityClass.BelowNormal => Strings.Low2,
        ProcessPriorityClass.Normal => Strings.Normal2,
        ProcessPriorityClass.AboveNormal => Strings.Above,
        ProcessPriorityClass.High => Strings.High2,
        ProcessPriorityClass.RealTime => Strings.Realtime2,
        _ => "—"
    };

    public static string Uptime(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
        : $"{span.Minutes}m {span.Seconds:00}s";

    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalSeconds < 60 ? $"{Math.Max(0, (int)span.TotalSeconds)}s"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours}h"
            : $"{(int)span.TotalDays}d";
    }

    /// <summary>"ALL" when every logical processor is allowed, otherwise "6/16".</summary>
    public static string Affinity(long mask, int logical)
    {
        if (logical <= 0) return "—";
        int allowed = 0;
        for (int i = 0; i < Math.Min(logical, 64); i++)
        {
            if ((mask & (1L << i)) != 0) allowed++;
        }
        return allowed == 0 || allowed >= logical ? Strings.All : $"{allowed}/{logical}";
    }

    /// <summary>The engine's one-line summary of a purge, plus the first thing that went wrong, if anything did.</summary>
    public static string PurgeSummary(ProGuard.Shared.Contracts.MemoryPurgeResultDto result)
    {
        var text = string.IsNullOrWhiteSpace(result.Summary) ? Strings.Fmt_MemoryPurged : result.Summary.Trim();
        return result.Errors.Count == 0
            ? text
            : string.Format(result.Errors.Count == 1 ? Strings.Fmt_PurgeOneProblem : Strings.Fmt_PurgeProblems, text, result.Errors.Count, result.Errors[0]);
    }

    /// <summary>Who set the active power plan, as the engine names it, in words.</summary>
    public static string PlanOwner(string? owner) => owner switch
    {
        "User" => Strings.Fmt_SetByYou,
        "PerformanceMode" => Strings.Fmt_SetByPerformanceMode,
        "IdleSaver" => Strings.Fmt_SetByIdleSaver,
        "LoadSwitcher" => Strings.Fmt_SetByLoadSwitcher,
        "StartWith" => Strings.Fmt_SetWhenEngineStarted,
        null or "" => "—",
        _ => string.Format(Strings.Fmt_SetBy, owner.ToUpperInvariant())
    };

    public static string IoPriority(int? hint) => hint switch
    {
        0 => Strings.VeryLow2,
        1 => Strings.Low2,
        2 => Strings.Normal2,
        3 => Strings.High2,
        _ => "—"
    };

    public static string MemoryPriority(int? level) => level switch
    {
        1 => Strings.VeryLow2,
        2 => Strings.Low2,
        3 => Strings.Medium2,
        4 => Strings.BelowNormalUpper,
        5 => Strings.Normal2,
        _ => "—"
    };

    public static string Exe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
}
