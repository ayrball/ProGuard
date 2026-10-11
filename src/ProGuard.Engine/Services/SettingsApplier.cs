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
    private readonly IMemoryTrimService _memory trim;
    private readonly IPowerManager _powerManager;
    private readonly IResponsivenessGuardEngine _responsiveness guard;

    private readonly object _sync = new();
    private AppSettings? _applied;

    public SettingsApplier(
        ILogger<SettingsApplier> logger,
        IProcessMonitor processMonitor,
        IMemoryTrimService memory trim,
        IPowerManager powerManager,
        IResponsivenessGuardEngine responsiveness guard)
    {
        _logger = logger;
        _processMonitor = processMonitor;
        _memory trim = memory trim;
        _powerManager = powerManager;
        _responsiveness guard = responsiveness guard;
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

            if (all || previous!.MemoryTrimEnabled != settings.MemoryTrimEnabled
                    || previous.MemoryTrimIntervalSeconds != settings.MemoryTrimIntervalSeconds
                    || previous.MemoryTrimMemoryThresholdMB != settings.MemoryTrimMemoryThresholdMB)
                TryApply("MemoryTrim", () => _memory trim.Configure(
                    settings.MemoryTrimEnabled,
                    TimeSpan.FromSeconds(settings.MemoryTrimIntervalSeconds),
                    settings.MemoryTrimMemoryThresholdMB * 1024L * 1024L));

            if (all || previous!.AwayModeEnabled != settings.AwayModeEnabled
                    || previous.IdleTimeoutMinutes != settings.IdleTimeoutMinutes
                    || previous.IdlePowerPlanGuid != settings.IdlePowerPlanGuid)
                TryApply("AwayMode", () => _powerManager.ConfigureAwayMode(
                    settings.AwayModeEnabled,
                    Math.Max(1, settings.IdleTimeoutMinutes),
                    Guid.TryParse(settings.IdlePowerPlanGuid, out var plan) ? plan : Powrprof.PowerSaver));

            if (all || previous!.ResponsivenessGuardEnabled != settings.ResponsivenessGuardEnabled
                    || previous.ResponsivenessGuardSensitivity != settings.ResponsivenessGuardSensitivity
                    || previous.ResponsivenessGuardCpuThreshold != settings.ResponsivenessGuardCpuThreshold
                    || previous.ResponsivenessGuardRecoveryThreshold != settings.ResponsivenessGuardRecoveryThreshold)
                TryApply("ResponsivenessGuard", () => ApplyResponsivenessGuardSettings(settings));

            _applied = settings.Clone();
        }
    }

    /// <summary>
    /// Hook for the ResponsivenessGuard settings. Only the members <see cref="IResponsivenessGuardEngine"/> exposes today are
    /// applied; when the engine grows setters for <see cref="AppSettings.ResponsivenessGuardCpuThreshold"/> and
    /// <see cref="AppSettings.ResponsivenessGuardRecoveryThreshold"/>, apply them here (change detection already
    /// covers both).
    /// </summary>
    protected virtual void ApplyResponsivenessGuardSettings(AppSettings settings)
    {
        _responsiveness guard.SetEnabled(settings.ResponsivenessGuardEnabled);
        _responsiveness guard.SetSensitivity(settings.ResponsivenessGuardSensitivity);
    }

    // One failing service must not stop the others from getting their settings.
    private void TryApply(string what, Action apply)
    {
        try { apply(); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to apply {Setting} settings", what); }
    }
}
