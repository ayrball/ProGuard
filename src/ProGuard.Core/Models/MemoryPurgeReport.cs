using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ProGuard.Core.Models;

/// <summary>
/// What one SmartTrim pass or "Purge now" did. Sent to the UI as the data of the PurgeMemory command result.
/// </summary>
public class MemoryPurgeReport
{
    private const double Mb = 1024 * 1024;

    public int ProcessesTrimmed { get; set; }
    public long WorkingSetBytesReclaimed { get; set; }

    public bool StandbyPurged { get; set; }
    /// <summary>Growth of the free + zero page lists across the purge.</summary>
    public long StandbyBytesReclaimed { get; set; }

    public bool FileCachePurged { get; set; }
    /// <summary>Shrinkage of the system file cache across the purge.</summary>
    public long FileCacheBytesReclaimed { get; set; }

    /// <summary>Actions that were attempted and failed, e.g. a missing privilege.</summary>
    public List<string> Errors { get; set; } = new();

    public long TotalBytesReclaimed => WorkingSetBytesReclaimed + StandbyBytesReclaimed + FileCacheBytesReclaimed;

    public bool AnyAction => ProcessesTrimmed > 0 || StandbyPurged || FileCachePurged;

    public static string FormatMb(long bytes) => (bytes / Mb).ToString("0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>One line for logs and the UI, e.g. "Trimmed 4 processes (812 MB); purged standby list (1530 MB)".</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (ProcessesTrimmed > 0)
            parts.Add($"trimmed {ProcessesTrimmed} process{(ProcessesTrimmed == 1 ? "" : "es")} ({FormatMb(WorkingSetBytesReclaimed)})");
        if (StandbyPurged) parts.Add($"purged standby list ({FormatMb(StandbyBytesReclaimed)})");
        if (FileCachePurged) parts.Add($"purged file cache ({FormatMb(FileCacheBytesReclaimed)})");

        var text = parts.Count == 0 ? "No action" : string.Join("; ", parts);
        text = char.ToUpperInvariant(text[0]) + text[1..];
        return Errors.Count == 0 ? text : text + ". Errors: " + string.Join("; ", Errors.Distinct());
    }
}
