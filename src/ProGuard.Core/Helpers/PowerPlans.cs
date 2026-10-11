using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ProGuard.Core.Native;

namespace ProGuard.Core.Helpers;

/// <summary>A power plan installed on this machine.</summary>
public readonly record struct PowerPlanInfo(Guid Id, string Name);

/// <summary>
/// Lists the power plans actually installed (<c>PowerEnumerate</c> + <c>PowerReadFriendlyName</c>), as Process Lasso
/// does, rather than assuming the three Windows defaults exist: OEM images often drop High Performance or Power Saver,
/// and tools add their own (Ultimate Performance, Bitsum Highest Performance). Works unelevated, so the UI and the
/// engine share it.
/// </summary>
public static class PowerPlans
{
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>The installed plans, in the order Windows lists them. Falls back to the Windows defaults if the
    /// enumeration fails, so a picker is never empty.</summary>
    public static IReadOnlyList<PowerPlanInfo> Enumerate()
    {
        try
        {
            var plans = Enumerate(EnumerateSchemeIds(), ReadFriendlyName);
            if (plans.Count > 0) return plans;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
        }
        return new[]
        {
            new PowerPlanInfo(Powrprof.Balanced, WellKnownName(Powrprof.Balanced)!),
            new PowerPlanInfo(Powrprof.HighPerformance, WellKnownName(Powrprof.HighPerformance)!),
            new PowerPlanInfo(Powrprof.PowerSaver, WellKnownName(Powrprof.PowerSaver)!),
        };
    }

    /// <summary>Names each plan, falling back to its well-known name or its GUID when Windows has none.</summary>
    public static IReadOnlyList<PowerPlanInfo> Enumerate(IEnumerable<Guid> ids, Func<Guid, string?> readName)
    {
        var plans = new List<PowerPlanInfo>();
        var seen = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (id == Guid.Empty || !seen.Add(id)) continue;
            var name = readName(id);
            plans.Add(new PowerPlanInfo(id, string.IsNullOrWhiteSpace(name) ? WellKnownName(id) ?? id.ToString() : name.Trim()));
        }
        return plans;
    }

    /// <summary>The plan's name, for log messages: its friendly name if installed, else a well-known name or the GUID.</summary>
    public static string Describe(Guid id)
    {
        if (id == Guid.Empty) return "(none)";
        string? name = null;
        try { name = ReadFriendlyName(id); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException) { }
        return string.IsNullOrWhiteSpace(name) ? WellKnownName(id) ?? id.ToString() : name.Trim();
    }

    public static string? WellKnownName(Guid id) =>
        id == Powrprof.Balanced ? "Balanced"
        : id == Powrprof.HighPerformance ? "High Performance"
        : id == Powrprof.PowerSaver ? "Power Saver"
        : id == UltimatePerformance ? "Ultimate Performance"
        : null;

    private static IEnumerable<Guid> EnumerateSchemeIds()
    {
        var ids = new List<Guid>();
        for (uint index = 0; ; index++)
        {
            var id = Guid.Empty;
            uint size = (uint)Marshal.SizeOf<Guid>();
            var result = Powrprof.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Powrprof.AccessScheme, index, ref id, ref size);
            if (result == Powrprof.ErrorNoMoreItems) break;
            if (result != 0) break;     // a partial list is still better than none
            ids.Add(id);
            if (index > 1024) break;    // no machine has this many plans; don't loop forever on a broken API
        }
        return ids;
    }

    private static string? ReadFriendlyName(Guid id)
    {
        uint size = 0;
        var result = Powrprof.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if ((result != 0 && result != Powrprof.ErrorMoreData) || size == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Powrprof.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return null;
            return Marshal.PtrToStringUni(buffer, (int)size / 2).TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
