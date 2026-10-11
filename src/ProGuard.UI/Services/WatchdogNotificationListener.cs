using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using ProGuard.Shared.Contracts;
using ProGuard.UI.IPC;

namespace ProGuard.UI.Services;

/// <summary>
/// Collects Watchdog and Keep Running events from the engine and shows a tray notification for each one a rule asked
/// to be told about. The engine runs in session 0 and can't reach this desktop, so the UI asks it every couple of
/// seconds for whatever happened since the last event it saw.
/// </summary>
public sealed class WatchdogNotificationListener : IHostedService, IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Func<long?, CancellationToken, Task<WatchdogEventList?>> _fetch;
    private readonly Action<WatchdogEventDto> _notify;
    private long? _cursor;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public WatchdogNotificationListener(EngineProxy engine, TrayIconService tray)
        : this(engine.GetWatchdogEventsAsync, e => tray.ShowNotification(e.Title, e.Message)) { }

    internal WatchdogNotificationListener(Func<long?, CancellationToken, Task<WatchdogEventList?>> fetch, Action<WatchdogEventDto> notify)
    {
        _fetch = fetch;
        _notify = notify;
    }

    /// <summary>Raised (on a worker thread) with each batch of new events, oldest first.</summary>
    public event EventHandler<IReadOnlyList<WatchdogEventDto>>? EventsReceived;

    /// <summary>The newest event seen so far, including ones from before the UI started.</summary>
    public WatchdogEventDto? LatestEvent { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try { await PollOnceAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Watchdog event poll failed: {ex.Message}"); }
        }
        while (await WaitAsync(timer, token));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>
    /// One poll. The first successful one fetches the engine's backlog for the activity view without notifying:
    /// those notifications were due while the UI wasn't running.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken token)
    {
        var first = _cursor == null;
        var list = await _fetch(first ? 0 : _cursor, token);
        if (list == null) return; // engine not reachable; ask again from the same place

        _cursor = list.Latest;
        if (list.Events.Count == 0) return;

        var events = list.Events.OrderBy(e => e.Sequence).ToList();
        LatestEvent = events[^1];
        if (!first)
        {
            foreach (var e in events.Where(e => e.Notify))
            {
                try { _notify(e); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Could not show a watchdog notification: {ex.Message}"); }
            }
        }
        EventsReceived?.Invoke(this, events);
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        cts?.Dispose();
    }
}
