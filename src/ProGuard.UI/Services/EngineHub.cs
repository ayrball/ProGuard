using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;

namespace ProGuard.UI.Services;

/// <summary>
/// The UI's single line to the engine. A background loop fetches a snapshot about once a second and hands
/// only the newest one to the UI thread (a slow frame never builds a queue). Commands go through
/// <see cref="RunAsync"/>, which reports the outcome as a toast and triggers an early refresh so the effect
/// shows up immediately.
/// </summary>
public sealed partial class EngineHub : ObservableObject, IAsyncDisposable
{
    public const int HistoryLength = 90;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OfflineRetry = TimeSpan.FromSeconds(2);

    private readonly EngineClient _client;
    private readonly Dispatcher _dispatcher;
    private readonly Toaster _toaster;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    private readonly object _handoffLock = new();
    private EngineSnapshot? _pending;
    private bool _handoffQueued;
    private bool? _pendingOnline;

    // NaN = no sample yet, so the charts grow in from the right instead of starting as a flat line at zero.
    private readonly double[] _cpu = Enumerable.Repeat(double.NaN, HistoryLength).ToArray();
    private readonly double[] _memory = Enumerable.Repeat(double.NaN, HistoryLength).ToArray();

    [ObservableProperty] private bool isOnline;

    /// <summary>True only once a connection attempt has actually failed, so startup doesn't flash "offline".</summary>
    [ObservableProperty] private bool isOffline;
    [ObservableProperty] private bool hasConnected;
    [ObservableProperty] private EngineSnapshot? snapshot;
    [ObservableProperty] private double[] cpuHistory = Array.Empty<double>();
    [ObservableProperty] private double[] memoryHistory = Array.Empty<double>();

    /// <summary>Raised on the UI thread with every snapshot that reaches the UI.</summary>
    public event Action<EngineSnapshot>? SnapshotApplied;

    public EngineHub(EngineClient client, Dispatcher dispatcher, Toaster toaster)
    {
        _client = client;
        _dispatcher = dispatcher;
        _toaster = toaster;
    }

    public void Start() => _ = Task.Run(PollLoopAsync);

    /// <summary>Fetch the next snapshot now instead of waiting for the next tick.</summary>
    public void RefreshNow()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
        }
    }

    /// <summary>Runs a <see cref="CommandTypes"/> command and reports the outcome. Returns false (after showing why) when it failed.</summary>
    public async Task<bool> RunAsync(string command, int? pid = null, object? argument = null, string? success = null)
    {
        try
        {
            await _client.CommandAsync(command, pid, argument, _stop.Token);
            if (success != null) _toaster.Show(success);
            RefreshNow();
            return true;
        }
        catch (EngineCommandException ex)
        {
            _toaster.Show(ex.Message, isError: true);
        }
        catch (EngineUnavailableException ex)
        {
            _toaster.Show(ex.Message, isError: true);
        }
        catch (OperationCanceledException) { }
        return false;
    }

    /// <summary>Runs a <see cref="CommandTypes"/> command that returns a value. Returns default (after showing why) when it failed.</summary>
    public async Task<T?> QueryAsync<T>(string command, int? pid = null, object? argument = null, bool quiet = false)
    {
        try
        {
            return await _client.QueryAsync<T>(command, pid, argument, _stop.Token);
        }
        catch (EngineCommandException ex)
        {
            if (!quiet) _toaster.Show(ex.Message, isError: true);
        }
        catch (EngineUnavailableException ex)
        {
            if (!quiet) _toaster.Show(ex.Message, isError: true);
        }
        catch (OperationCanceledException) { }
        return default;
    }

    private async Task PollLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                var snapshot = await _client.QueryAsync<EngineSnapshot>(CommandTypes.GetSnapshot, cancellationToken: token);
                HandOff(snapshot, online: true);
                wait = PollInterval;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is EngineUnavailableException or EngineCommandException)
            {
                HandOff(null, online: false);
                wait = OfflineRetry;
            }

            try
            {
                await _wake.WaitAsync(wait, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void HandOff(EngineSnapshot? snapshot, bool online)
    {
        lock (_handoffLock)
        {
            if (snapshot != null) _pending = snapshot;
            _pendingOnline = online;
            if (_handoffQueued) return;
            _handoffQueued = true;
        }

        // Background priority: input and rendering always go first, so a busy refresh can't make clicks lag.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, Apply);
    }

    private void Apply()
    {
        EngineSnapshot? snapshot;
        bool? online;
        lock (_handoffLock)
        {
            snapshot = _pending;
            online = _pendingOnline;
            _pending = null;
            _pendingOnline = null;
            _handoffQueued = false;
        }

        if (online.HasValue)
        {
            IsOnline = online.Value;
            IsOffline = !online.Value;
        }
        if (snapshot == null) return;

        HasConnected = true;
        Push(_cpu, snapshot.CpuPercent);
        Push(_memory, snapshot.MemoryPercent);
        CpuHistory = (double[])_cpu.Clone();
        MemoryHistory = (double[])_memory.Clone();

        Snapshot = snapshot;
        SnapshotApplied?.Invoke(snapshot);
    }

    private static void Push(double[] ring, double value)
    {
        Array.Copy(ring, 1, ring, 0, ring.Length - 1);
        ring[^1] = value;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _client.DisposeAsync();
        _stop.Dispose();
    }
}
