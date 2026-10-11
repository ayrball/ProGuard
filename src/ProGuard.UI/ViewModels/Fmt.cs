using System.Diagnostics;

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
        ProcessPriorityClass.Idle => "IDLE",
        ProcessPriorityClass.BelowNormal => "LOW",
        ProcessPriorityClass.Normal => "NORMAL",
        ProcessPriorityClass.AboveNormal => "ABOVE",
        ProcessPriorityClass.High => "HIGH",
        ProcessPriorityClass.RealTime => "REALTIME",
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
        return allowed == 0 || allowed >= logical ? "ALL" : $"{allowed}/{logical}";
    }

    public static string Exe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
}
