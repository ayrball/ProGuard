using System.Text.Json;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.UI.Services;

/// <summary>
/// Collects Watchdog and Keep Running events from the engine and passes each one a rule asked to be told about to
/// <c>notify</c> (the tray balloon). The engine runs in session 0 and can't reach this desktop, so the UI asks it every
/// couple of seconds for whatever happened since the last event it saw.
/// </summary>
/// <remarks>
/// Cursor: the first request has <see cref="WatchdogEventQuery.After"/> = null, which returns only
/// <see cref="WatchdogEventList.Latest"/>, so a freshly started UI doesn't replay old notifications. Every later request
/// asks for events after the newest sequence number seen. Sequence numbers restart from 1 when the engine restarts; a
/// <c>Latest</c> below the cursor means that happened, and the new engine's events are fetched from the start.
/// </remarks>
public sealed class WatchdogNotificationFeed : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Func<WatchdogEventQuery, CancellationToken, Task<WatchdogEventList?>> _fetch;
    private readonly Action<WatchdogEventDto> _notify;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <summary>The newest sequence number seen; null until the first reply.</summary>
    public long? Cursor { get; private set; }

    public WatchdogNotificationFeed(EngineClient client, Action<WatchdogEventDto> notify)
        : this(FetchWith(client), notify) { }

    internal WatchdogNotificationFeed(Func<WatchdogEventQuery, CancellationToken, Task<WatchdogEventList?>> fetch, Action<WatchdogEventDto> notify)
    {
        _fetch = fetch;
        _notify = notify;
    }

    private static Func<WatchdogEventQuery, CancellationToken, Task<WatchdogEventList?>> FetchWith(EngineClient client) =>
        async (query, token) =>
        {
            try
            {
                var request = new IpcMessage { Type = MessageType.GetLogs, Payload = JsonSerializer.Serialize(query) };
                var reply = await client.SendAsync(request, token).ConfigureAwait(false);
                return reply.Type == MessageType.LogList
                    ? JsonSerializer.Deserialize<WatchdogEventList>(reply.Payload, EngineClient.Json)
                    : null;
            }
            catch (Exception ex) when (ex is EngineUnavailableException or JsonException)
            {
                return null; // engine not reachable (or from another build); ask again from the same place
            }
        };

    public void Start()
    {
        if (_loop != null) return;
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                try { await PollOnceAsync(token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"Watchdog event poll failed: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One poll: fetch what is new and notify for each event that asks for it, oldest first.</summary>
    internal async Task PollOnceAsync(CancellationToken token = default)
    {
        var list = await _fetch(new WatchdogEventQuery { After = Cursor }, token).ConfigureAwait(false);
        if (list == null) return;

        if (Cursor == null)
        {
            Cursor = list.Latest; // the baseline: whatever happened before now isn't news
            return;
        }

        if (list.Latest < Cursor)
        {
            // The engine restarted and counts from 1 again.
            Cursor = 0;
            list = await _fetch(new WatchdogEventQuery { After = 0 }, token).ConfigureAwait(false);
            if (list == null) return;
        }

        long newest = Cursor.Value;
        foreach (var e in list.Events.Where(e => e.Sequence > Cursor.Value).OrderBy(e => e.Sequence))
        {
            newest = Math.Max(newest, e.Sequence);
            if (!e.Notify) continue;
            try { _notify(e); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Could not show a watchdog notification: {ex.Message}"); }
        }
        // Advance only as far as the events actually received, in case a reply is capped.
        Cursor = list.Events.Count > 0 ? newest : Math.Max(Cursor.Value, list.Latest);
    }

    public void Dispose()
    {
        var stop = Interlocked.Exchange(ref _stop, null);
        stop?.Cancel();
        stop?.Dispose();
    }
}
