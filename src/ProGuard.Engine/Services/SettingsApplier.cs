using System;
using Microsoft.Extensions.Logging;
using ProGuard.Core.Models;
using ProGuard.Core.Native;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

/// <summary>
/// Pushes <see cref="AppSettings"/> into the running engine services. Called at startup and whenever the
/// settings change (the UI saved them to the shared database, or an IPC command changed them).
/// Each group of settings is only re-applied when one of its values changed, so a reload caused by an
/// unrelated write (e.g. a rule edit) does not reset service state such as timers.
/// </summary>
public class SettingsApplier
{
    private readonly ILogger<SettingsApplier> _logger;
    private readonly IProcessMonitor _processMonitor;
    private readonly IMemoryTrimService _memoryTrim;
    private readonly IPowerManager _powerManager;
    private readonly IResponsivenessGuardEngine _responsivenessGuard;
    private readonly IAutoPerformanceMode? _autoPerformanceMode;
    private readonly ForegroundBoostService? _foregroundBoost;
    private readonly TimerResolutionService? _timerResolution;
    private readonly CoreParkingService? _coreParking;

    private readonly object _sync = new();
    private AppSettings? _applied;

    public SettingsApplier(
        ILogger<SettingsApplier> logger,
        IProcessMonitor processMonitor,
        IMemoryTrimService memoryTrim,
        IPowerManager powerManager,
        IResponsivenessGuardEngine responsivenessGuard,
        IAutoPerformanceMode? autoPerformanceMode = null,
        ForegroundBoostService? foregroundBoost = null,
        TimerResolutionService? timerResolution = null,
        CoreParkingService? coreParking = null)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _memoryTrim = memoryTrim;
        _powerManager = powerManager;
        _responsivenessGuard = responsivenessGuard;
        _autoPerformanceMode = autoPerformanceMode;
        _foregroundBoost = foregroundBoost;
        _timerResolution = timerResolution;
        _coreParking = coreParking;
    }

    /// <summary>The settings most recently applied, or null before the first <see cref="Apply"/>.</summary>
    public AppSettings? Current
    {
        get { lock (_sync) return _applied?.Clone(); }
    }

    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            var previous = _applied;
            var all = previous == null;

            if (all || previous!.PollingIntervalMs != settings.PollingIntervalMs)
                TryApply("polling interval", () => _processMonitor.SetPollingInterval(TimeSpan.FromMilliseconds(settings.PollingIntervalMs)));

            if (_foregroundBoost != null && (all || previous!.ForegroundBoostEnabled != settings.ForegroundBoostEnabled))
                TryApply("Foreground Boost", () => _foregroundBoost.IsEnabled = settings.ForegroundBoostEnabled);

            if (all || previous!.MemoryTrimEnabled != settings.MemoryTrimEnabled
                    || previous.MemoryTrimIntervalSeconds != settings.MemoryTrimIntervalSeconds
                    || previous.MemoryTrimMemoryThresholdMB != settings.MemoryTrimMemoryThresholdMB)
                TryApply("Memory Trim", () => _memoryTrim.Configure(
                    settings.MemoryTrimEnabled,
                    TimeSpan.FromSeconds(settings.MemoryTrimIntervalSeconds),
                    settings.MemoryTrimMemoryThresholdMB * 1024L * 1024L));

            if (all || previous!.MemoryTrimMinimumRamLoadPercent != settings.MemoryTrimMinimumRamLoadPercent
                    || previous.MemoryTrimExclusions != settings.MemoryTrimExclusions
                    || previous.MemoryTrimLogNoAction != settings.MemoryTrimLogNoAction
                    || previous.StandbyPurgeEnabled != settings.StandbyPurgeEnabled
                    || previous.StandbyPurgeFreeRamThresholdMB != settings.StandbyPurgeFreeRamThresholdMB
                    || previous.StandbyPurgeOnlyInPerformanceMode != settings.StandbyPurgeOnlyInPerformanceMode
                    || previous.FileCachePurgeEnabled != settings.FileCachePurgeEnabled)
                TryApply("Memory Trim policy", () => _memoryTrim.ConfigurePolicy(ToMemoryTrimPolicy(settings)));

            if (all || previous!.AwayModeEnabled != settings.AwayModeEnabled
                    || previous.IdleTimeoutMinutes != settings.IdleTimeoutMinutes
                    || previous.IdlePowerPlanGuid != settings.IdlePowerPlanGuid)
                TryApply("Away Mode", () => _powerManager.ConfigureAwayMode(
                    settings.AwayModeEnabled,
                    Math.Max(1, settings.IdleTimeoutMinutes),
                    Guid.TryParse(settings.IdlePowerPlanGuid, out var plan) ? plan : Powrprof.PowerSaver));

            if (all || previous!.PerformanceModePlanGuid != settings.PerformanceModePlanGuid)
                TryApply("Performance Mode", () => _powerManager.ConfigurePerformanceMode(ParsePlan(settings.PerformanceModePlanGuid)));

            if (all || previous!.PerformanceModeSetBestPerformance != settings.PerformanceModeSetBestPerformance)
                TryApply("Performance Mode power mode", () => _powerManager.ConfigurePerformanceModePowerMode(settings.PerformanceModeSetBestPerformance));

            if (_autoPerformanceMode != null
                && (all || !AutoPerformanceModeOptions.FromSettings(previous!).Equals(AutoPerformanceModeOptions.FromSettings(settings))))
                TryApply("automatic Performance Mode", () => _autoPerformanceMode.Configure(AutoPerformanceModeOptions.FromSettings(settings)));

            if (all || LoadSwitcherOptions.FromSettings(previous!) != LoadSwitcherOptions.FromSettings(settings))
                TryApply("load switcher", () => _powerManager.ConfigureLoadSwitcher(LoadSwitcherOptions.FromSettings(settings)));

            if (all || previous!.StartWithPowerPlanGuid != settings.StartWithPowerPlanGuid)
                TryApply("startup power plan", () => _powerManager.ConfigureStartupPlan(ParsePlan(settings.StartWithPowerPlanGuid)));

            if (all || previous!.ResponsivenessGuardEnabled != settings.ResponsivenessGuardEnabled
                    || previous.ResponsivenessGuardSensitivity != settings.ResponsivenessGuardSensitivity
                    || previous.ResponsivenessGuardCpuThreshold != settings.ResponsivenessGuardCpuThreshold
                    || previous.ResponsivenessGuardRecoveryThreshold != settings.ResponsivenessGuardRecoveryThreshold)
                TryApply("Responsiveness Guard", () => ApplyResponsivenessGuardSettings(settings));

            if (all || ResponsivenessGuardRestraintOptions.FromSettings(previous!) != ResponsivenessGuardRestraintOptions.FromSettings(settings))
                TryApply("Responsiveness Guard restraint actions", () => _responsivenessGuard.ConfigureRestraint(ResponsivenessGuardRestraintOptions.FromSettings(settings)));

            if (_coreParking != null && (all || previous!.ResponsivenessGuardDisableCoreParking != settings.ResponsivenessGuardDisableCoreParking))
                TryApply("core parking", () => _coreParking.Configure(settings.ResponsivenessGuardDisableCoreParking));

            if (_timerResolution != null && (all || previous!.TimerResolutionRequest != settings.TimerResolutionRequest))
                TryApply("timer resolution", () => _timerResolution.Configure(settings.TimerResolutionRequest));

            _applied = settings.Clone();
        }
    }

    /// <summary>
    /// Hook for the Responsiveness Guard settings. Only the members <see cref="IResponsivenessGuardEngine"/> exposes today are
    /// applied; when the engine grows setters for <see cref="AppSettings.ResponsivenessGuardCpuThreshold"/> and
    /// <see cref="AppSettings.ResponsivenessGuardRecoveryThreshold"/>, apply them here (change detection already
    /// covers both).
    /// </summary>
    protected virtual void ApplyResponsivenessGuardSettings(AppSettings settings)
    {
        _responsivenessGuard.SetEnabled(settings.ResponsivenessGuardEnabled);
        _responsivenessGuard.SetSensitivity(settings.ResponsivenessGuardSensitivity);
    }

    internal static MemoryTrimPolicy ToMemoryTrimPolicy(AppSettings settings) => new()
    {
        MinimumRamLoadPercent = settings.MemoryTrimMinimumRamLoadPercent,
        Exclusions = SplitProcessList(settings.MemoryTrimExclusions),
        LogNoAction = settings.MemoryTrimLogNoAction,
        StandbyPurgeEnabled = settings.StandbyPurgeEnabled,
        StandbyFreeRamThresholdBytes = Math.Max(0, settings.StandbyPurgeFreeRamThresholdMB) * 1024L * 1024L,
        StandbyOnlyInPerformanceMode = settings.StandbyPurgeOnlyInPerformanceMode,
        FileCachePurgeEnabled = settings.FileCachePurgeEnabled,
    };

    internal static string[] SplitProcessList(string? list) =>
        (list ?? "").Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Guid ParsePlan(string? plan) => Guid.TryParse(plan, out var id) ? id : Guid.Empty;

    // One failing service must not stop the others from getting their settings.
    private void TryApply(string what, Action apply)
    {
        try { apply(); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to apply {Setting} settings", what); }
    }
}
