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
    private readonly ISmartTrimService _smartTrim;
    private readonly IPowerManager _powerManager;
    private readonly IProBalanceEngine _proBalance;

    private readonly object _sync = new();
    private AppSettings? _applied;

    public SettingsApplier(
        ILogger<SettingsApplier> logger,
        IProcessMonitor processMonitor,
        ISmartTrimService smartTrim,
        IPowerManager powerManager,
        IProBalanceEngine proBalance)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _smartTrim = smartTrim;
        _powerManager = powerManager;
        _proBalance = proBalance;
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

            if (all || previous!.SmartTrimEnabled != settings.SmartTrimEnabled
                    || previous.SmartTrimIntervalSeconds != settings.SmartTrimIntervalSeconds
                    || previous.SmartTrimMemoryThresholdMB != settings.SmartTrimMemoryThresholdMB)
                TryApply("SmartTrim", () => _smartTrim.Configure(
                    settings.SmartTrimEnabled,
                    TimeSpan.FromSeconds(settings.SmartTrimIntervalSeconds),
                    settings.SmartTrimMemoryThresholdMB * 1024L * 1024L));

            if (all || previous!.SmartTrimMinimumRamLoadPercent != settings.SmartTrimMinimumRamLoadPercent
                    || previous.SmartTrimExclusions != settings.SmartTrimExclusions
                    || previous.SmartTrimLogNoAction != settings.SmartTrimLogNoAction
                    || previous.StandbyPurgeEnabled != settings.StandbyPurgeEnabled
                    || previous.StandbyPurgeFreeRamThresholdMB != settings.StandbyPurgeFreeRamThresholdMB
                    || previous.StandbyPurgeOnlyInPerformanceMode != settings.StandbyPurgeOnlyInPerformanceMode
                    || previous.FileCachePurgeEnabled != settings.FileCachePurgeEnabled)
                TryApply("SmartTrim policy", () => _smartTrim.ConfigurePolicy(ToSmartTrimPolicy(settings)));

            if (all || previous!.IdleSaverEnabled != settings.IdleSaverEnabled
                    || previous.IdleTimeoutMinutes != settings.IdleTimeoutMinutes
                    || previous.IdlePowerPlanGuid != settings.IdlePowerPlanGuid)
                TryApply("IdleSaver", () => _powerManager.ConfigureIdleSaver(
                    settings.IdleSaverEnabled,
                    Math.Max(1, settings.IdleTimeoutMinutes),
                    Guid.TryParse(settings.IdlePowerPlanGuid, out var plan) ? plan : Powrprof.PowerSaver));

            if (all || previous!.ProBalanceEnabled != settings.ProBalanceEnabled
                    || previous.ProBalanceSensitivity != settings.ProBalanceSensitivity
                    || previous.ProBalanceCpuThreshold != settings.ProBalanceCpuThreshold
                    || previous.ProBalanceRecoveryThreshold != settings.ProBalanceRecoveryThreshold)
                TryApply("ProBalance", () => ApplyProBalanceSettings(settings));

            _applied = settings.Clone();
        }
    }

    /// <summary>
    /// Hook for the ProBalance settings. Only the members <see cref="IProBalanceEngine"/> exposes today are
    /// applied; when the engine grows setters for <see cref="AppSettings.ProBalanceCpuThreshold"/> and
    /// <see cref="AppSettings.ProBalanceRecoveryThreshold"/>, apply them here (change detection already
    /// covers both).
    /// </summary>
    protected virtual void ApplyProBalanceSettings(AppSettings settings)
    {
        _proBalance.SetEnabled(settings.ProBalanceEnabled);
        _proBalance.SetSensitivity(settings.ProBalanceSensitivity);
    }

    internal static SmartTrimPolicy ToSmartTrimPolicy(AppSettings settings) => new()
    {
        MinimumRamLoadPercent = settings.SmartTrimMinimumRamLoadPercent,
        Exclusions = SplitProcessList(settings.SmartTrimExclusions),
        LogNoAction = settings.SmartTrimLogNoAction,
        StandbyPurgeEnabled = settings.StandbyPurgeEnabled,
        StandbyFreeRamThresholdBytes = Math.Max(0, settings.StandbyPurgeFreeRamThresholdMB) * 1024L * 1024L,
        StandbyOnlyInPerformanceMode = settings.StandbyPurgeOnlyInPerformanceMode,
        FileCachePurgeEnabled = settings.FileCachePurgeEnabled,
    };

    internal static string[] SplitProcessList(string? list) =>
        (list ?? "").Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // One failing service must not stop the others from getting their settings.
    private void TryApply(string what, Action apply)
    {
        try { apply(); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to apply {Setting} settings", what); }
    }
}
