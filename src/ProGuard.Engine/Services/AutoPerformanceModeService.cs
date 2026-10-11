using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProGuard.Core.Services;

namespace ProGuard.Engine.Services;

public interface IAutoPerformanceMode
{
    void Configure(AutoPerformanceModeOptions options);
}

/// <summary>
/// Automatic Performance Mode: keeps Performance Mode on while a listed process or a detected game runs, and lets it go
/// when none is left (<see cref="IPowerManager.SetAutomaticPerformanceMode"/>; Performance Mode the user turned on
/// themselves stays on). Watches the engine's process list every <see cref="CheckInterval"/>.
/// </summary>
public sealed class AutoPerformanceModeService : IAutoPerformanceMode, IDisposable
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    /// <summary>How long the Steam library index is trusted before it is read again (new installs, new libraries).</summary>
    internal static readonly TimeSpan SteamRescanInterval = TimeSpan.FromMinutes(5);

    private readonly ILogger<AutoPerformanceModeService> _logger;
    private readonly IProcessMonitor _processMonitor;
    private readonly IPowerManager _powerManager;
    private readonly Func<SteamLibraryIndex> _loadSteamLibraries;
    private readonly Func<TimeSpan> _clock;
    private readonly GameDetector _detector;
    private readonly object _sync = new();

    private AutoPerformanceModeOptions _options = new();
    private SteamLibraryIndex? _steam;
    private TimeSpan _steamLoadedAt;
    private int? _engagedPid;
    private string? _engagedReason;
    private Timer? _timer;

    public AutoPerformanceModeService(ILogger<AutoPerformanceModeService> logger, IProcessMonitor processMonitor, IPowerManager powerManager)
        : this(logger, processMonitor, powerManager, null, null)
    {
    }

    internal AutoPerformanceModeService(ILogger<AutoPerformanceModeService>? logger, IProcessMonitor processMonitor,
        IPowerManager powerManager, Func<SteamLibraryIndex>? loadSteamLibraries, Func<TimeSpan>? clock)
    {
        _logger = logger ?? NullLogger<AutoPerformanceModeService>.Instance;
        _processMonitor = processMonitor;
        _powerManager = powerManager;
        _loadSteamLibraries = loadSteamLibraries ?? (() => SteamLibraryIndex.Load(SteamLibraryIndex.FindSteamRoot()));
        _clock = clock ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
        _detector = new GameDetector(SteamLibraries);
    }

    public void Start()
    {
        _timer ??= new Timer(_ => SafeCheck(), null, TimeSpan.Zero, CheckInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    public void Configure(AutoPerformanceModeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_sync)
        {
            if (options.Equals(_options)) return;
            if (options.EngageForSteam != _options.EngageForSteam) _steam = null;
            _options = options;
            _logger.LogInformation(
                "Automatic Performance Mode configured: {Processes} listed, {Exclusions} excluded, DetectGames={Detect}, EngageForSteam={Steam}",
                options.Processes.Count, options.Exclusions.Count, options.DetectGames, options.EngageForSteam);
        }
        // Apply at once rather than at the next tick, so removing the last trigger releases Performance Mode now.
        SafeCheck();
    }

    private void SafeCheck()
    {
        try { Check(); }
        catch (Exception ex) { _logger.LogError(ex, "Automatic Performance Mode check failed"); }
    }

    /// <summary>One check of the running processes. Runs every <see cref="CheckInterval"/>; tests call it directly.</summary>
    internal void Check()
    {
        lock (_sync)
        {
            string? reason = null;
            int? pid = null;
            bool engagedStillRuns = false;
            if (_options.AnyEnabled)
            {
                var processes = _processMonitor.GetAllProcesses().ToList();
                // Keep crediting the process that engaged it while it runs, so the reason doesn't flip between games.
                var engaged = _engagedPid is { } p ? processes.FirstOrDefault(x => x.Pid == p) : null;
                engagedStillRuns = engaged != null;
                if (engaged != null && _detector.Match(engaged, _options) is { } still)
                {
                    reason = still;
                    pid = engaged.Pid;
                }
                else
                {
                    foreach (var process in processes)
                    {
                        if (_detector.Match(process, _options) is not { } match) continue;
                        reason = match;
                        pid = process.Pid;
                        break;
                    }
                }
            }

            if (reason == _engagedReason && pid == _engagedPid) return;
            var released = _engagedReason;
            _engagedReason = reason;
            _engagedPid = pid;
            if (reason != null)
                _powerManager.SetAutomaticPerformanceMode(true, reason);
            else
                _powerManager.SetAutomaticPerformanceMode(false,
                    !_options.AnyEnabled ? "automatic Performance Mode turned off"
                    // Still running, so the settings changed (a game detector or the list switched off).
                    : engagedStillRuns ? released?.Replace(" is running", " no longer turns it on")
                    : released?.Replace(" is running", " exited"));
        }
    }

    private SteamLibraryIndex SteamLibraries()
    {
        // Called by the detector under _sync.
        var now = _clock();
        if (_steam == null || now - _steamLoadedAt >= SteamRescanInterval)
        {
            _steam = _loadSteamLibraries();
            _steamLoadedAt = now;
            _logger.LogDebug("Found {Count} games in the Steam libraries", _steam.Games.Count);
        }
        return _steam;
    }
}
