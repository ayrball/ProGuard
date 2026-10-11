using System.Collections.Generic;

namespace ProGuard.Core.Models;

/// <summary>
/// The values each persistent per-process setting on <see cref="ProcessRule"/> accepts, indexed by the stored number,
/// with the names the UI and the engine's log show for them.
/// </summary>
public static class ProcessRuleOptions
{
    /// <summary>IO_PRIORITY_HINT 0..3. Critical (4) is reserved for the memory manager.</summary>
    public static readonly IReadOnlyList<string> IoPriorities = new[] { "Very Low", "Low", "Normal", "High" };

    /// <summary>Index 0 is unused: MEMORY_PRIORITY_VERY_LOW is 1 and MEMORY_PRIORITY_NORMAL is 5.</summary>
    public static readonly IReadOnlyList<string> MemoryPriorities = new[] { "", "Very Low", "Low", "Medium", "Below Normal", "Normal" };
    public const int MinMemoryPriority = 1;

    /// <summary>D3DKMT_SCHEDULINGPRIORITYCLASS 0..4. Realtime (5) is left out: it can starve the desktop compositor.</summary>
    public static readonly IReadOnlyList<string> GpuPriorities = new[] { "Idle", "Below Normal", "Normal", "Above Normal", "High" };

    /// <summary>The N in the UserGpuPreferences value "GpuPreference=N;".</summary>
    public static readonly IReadOnlyList<string> GpuPreferences = new[] { "Let Windows decide", "Power saving", "High performance" };

    public static bool IsValid(IReadOnlyList<string> options, int value, int min = 0) => value >= min && value < options.Count;

    public static string Label(IReadOnlyList<string> options, int value) =>
        value >= 0 && value < options.Count && options[value].Length > 0 ? options[value] : value.ToString();
}
