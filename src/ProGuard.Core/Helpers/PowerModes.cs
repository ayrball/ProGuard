using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ProGuard.Core.Native;

namespace ProGuard.Core.Helpers;

/// <summary>A Windows power mode: a position of the Windows 11 power-mode slider (an overlay on the active plan).</summary>
public readonly record struct PowerModeInfo(Guid Id, string Name);

/// <summary>
/// The Windows 10/11 power-mode slider, through <c>PowerGetActualOverlayScheme</c> / <c>PowerSetActiveOverlayScheme</c>
/// as Process Lasso uses them. The mode is an overlay on the active power plan: Windows applies it while the Balanced
/// plan is active and ignores it under plans such as High Performance. The calls aren't in the SDK headers, so on
/// Windows without them (or where they fail) the slider is reported as unavailable.
/// </summary>
public static class PowerModes
{
    public static readonly Guid BestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    /// <summary>"Balanced" (Windows 11) or "Recommended" (Windows 10): no overlay.</summary>
    public static readonly Guid Balanced = Guid.Empty;
    /// <summary>Windows 10's "Better performance"; Windows 11 no longer offers it.</summary>
    public static readonly Guid BetterPerformance = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    /// <summary>The modes Windows 11's slider offers, from the most frugal to the fastest.</summary>
    public static readonly IReadOnlyList<PowerModeInfo> Offered = new[]
    {
        new PowerModeInfo(BestPowerEfficiency, "Best power efficiency"),
        new PowerModeInfo(Balanced, "Balanced"),
        new PowerModeInfo(BestPerformance, "Best performance"),
    };

    public static string Describe(Guid id) =>
        id == BestPowerEfficiency ? "Best power efficiency"
        : id == Balanced ? "Balanced"
        : id == BetterPerformance ? "Better performance"
        : id == BestPerformance ? "Best performance"
        : id.ToString();

    /// <summary>The mode in force, or null where Windows has no power-mode slider.</summary>
    public static Guid? ReadActual()
    {
        try
        {
            return Powrprof.PowerGetActualOverlayScheme(out var id) == 0 ? id : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return null;
        }
    }

    public static bool Write(Guid id)
    {
        try
        {
            return Powrprof.PowerSetActiveOverlayScheme(id) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return false;
        }
    }
}
