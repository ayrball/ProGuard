using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using ProGuard.Core.Native;

namespace ProGuard.Engine.Services;

/// <summary>The system timer's resolutions, in 100-ns units. <paramref name="Minimum"/> is the coarsest, <paramref name="Maximum"/> the finest.</summary>
public readonly record struct TimerResolutions(uint Minimum, uint Maximum, uint Current);

/// <summary>Windows' timer resolution calls and the GlobalTimerResolutionRequests switch. Tests replace it.</summary>
public interface ITimerResolutionNative
{
    TimerResolutions? Query();

    /// <summary>Asks for <paramref name="resolution"/> (or withdraws that request); returns the resolution now in force, or null on failure.</summary>
    uint? Set(uint resolution, bool set);

    /// <summary>Whether this Windows has the switch (Windows 11 / Server 2022 and later).</summary>
    bool GlobalRequestsSupported { get; }
    bool ReadGlobalRequests();
    void WriteGlobalRequests(bool enabled);
}

public sealed class Win32TimerResolution : ITimerResolutionNative
{
    private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string GlobalValue = "GlobalTimerResolutionRequests";

    public TimerResolutions? Query() =>
        Ntdll.NtQueryTimerResolution(out var min, out var max, out var current) == 0 ? new TimerResolutions(min, max, current) : null;

    public uint? Set(uint resolution, bool set) =>
        Ntdll.NtSetTimerResolution(resolution, set, out var current) == 0 ? current : null;

    // Windows Server 2022 is build 20348; Windows 11 starts at 22000. Before them every request was system-wide.
    public bool GlobalRequestsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    public bool ReadGlobalRequests()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(KernelKey);
        return key?.GetValue(GlobalValue) is int value && value != 0;
    }

    public void WriteGlobalRequests(bool enabled)
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).CreateSubKey(KernelKey, writable: true);
        if (enabled) key.SetValue(GlobalValue, 1, RegistryValueKind.DWord);
        else key.DeleteValue(GlobalValue, throwOnMissingValue: false);
    }
}

/// <summary>
/// Holds the system timer resolution Process Lasso's <c>SetTimerResolutionAtStartup</c> asks for, with
/// <c>NtSetTimerResolution</c>, for as long as the engine runs. A finer timer makes sleeps and timer waits more precise
/// (some games and audio software want it) at the cost of more wake-ups and power.
/// <para>
/// The request belongs to the engine's process: Windows drops it when the engine stops, crashed or not, so there is
/// nothing to recover. Since Windows 11 / Server 2022 a request is honoured only for the process that made it, unless the
/// <c>GlobalTimerResolutionRequests</c> registry switch is on (read at boot), which <see cref="SetGlobalRequests"/>
/// sets. On older Windows every request is system-wide.
/// </para>
/// </summary>
public sealed class TimerResolutionService
{
    private readonly ILogger<TimerResolutionService> _logger;
    private readonly ITimerResolutionNative _native;
    private readonly object _sync = new();
    private int _requested;
    private uint _held;

    public TimerResolutionService(ILogger<TimerResolutionService>? logger = null, ITimerResolutionNative? native = null)
    {
        _logger = logger ?? NullLogger<TimerResolutionService>.Instance;
        _native = native ?? new Win32TimerResolution();
    }

    /// <summary>The configured resolution (100 ns), 0 for none.</summary>
    public int Requested
    {
        get { lock (_sync) return _requested; }
    }

    /// <summary>What the engine is asking Windows for now (100 ns), 0 for nothing.</summary>
    public uint Held
    {
        get { lock (_sync) return _held; }
    }

    /// <summary>
    /// Asks for <paramref name="resolution100ns"/>, kept within what the timer can do, or withdraws the request for 0.
    /// The previous request is withdrawn first, so the engine never holds two.
    /// </summary>
    public void Configure(int resolution100ns)
    {
        resolution100ns = Math.Max(0, resolution100ns);
        lock (_sync)
        {
            if (resolution100ns == _requested && (resolution100ns == 0) == (_held == 0)) return;
            _requested = resolution100ns;
            ReleaseLocked();
            if (resolution100ns == 0) return;

            if (_native.Query() is not { } range)
            {
                _logger.LogWarning("Can't read the timer resolution range; not changing the timer");
                return;
            }
            // Finest (smallest interval) is NT's "maximum" resolution, coarsest its "minimum".
            var wanted = (uint)Math.Clamp((long)resolution100ns, range.Maximum, range.Minimum);
            if (_native.Set(wanted, true) is { } current)
            {
                _held = wanted;
                _logger.LogInformation("Timer resolution: asked for {Wanted} ms, now {Current} ms", Ms(wanted), Ms(current));
            }
            else
            {
                _logger.LogWarning("Windows refused timer resolution {Wanted} ms", Ms(wanted));
            }
        }
    }

    /// <summary>Withdraws the engine's request, if it holds one. Run when the engine stops.</summary>
    public void Release()
    {
        lock (_sync) ReleaseLocked();
    }

    private void ReleaseLocked()
    {
        if (_held == 0) return;
        var held = _held;
        _held = 0;
        if (_native.Set(held, false) is { } current)
            _logger.LogInformation("Timer resolution request of {Held} ms withdrawn; now {Current} ms", Ms(held), Ms(current));
    }

    /// <summary>Whether this Windows has the GlobalTimerResolutionRequests switch.</summary>
    public bool GlobalRequestsSupported => _native.GlobalRequestsSupported;

    /// <summary>The GlobalTimerResolutionRequests switch. Takes effect at the next restart.</summary>
    public bool SetGlobalRequests(bool enabled)
    {
        if (!_native.GlobalRequestsSupported) return false;
        _native.WriteGlobalRequests(enabled);
        _logger.LogInformation("GlobalTimerResolutionRequests turned {State}; Windows reads it at the next restart", enabled ? "on" : "off");
        return true;
    }

    public TimerResolutionState GetState()
    {
        var range = _native.Query();
        bool supported = _native.GlobalRequestsSupported;
        bool global = false;
        if (supported)
        {
            try { global = _native.ReadGlobalRequests(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                _logger.LogDebug(ex, "Can't read GlobalTimerResolutionRequests");
            }
        }
        lock (_sync)
            return new TimerResolutionState(range?.Current ?? 0, range?.Minimum ?? 0, range?.Maximum ?? 0, _requested, _held, supported, global);
    }

    private static string Ms(uint hundredNs) => (hundredNs / 10000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>What <see cref="TimerResolutionService.GetState"/> reports; resolutions in 100-ns units.</summary>
public readonly record struct TimerResolutionState(uint Current, uint Minimum, uint Maximum, int Requested, uint Held, bool GlobalRequestsSupported, bool GlobalRequests);
