using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using ProGuard.Core.Native;
using ProGuard.Shared.Messages;

namespace ProGuard.UI.Services;

/// <summary>
/// Tells the engine which process owns this desktop's foreground window. The engine runs as a service in session 0
/// and can't see the user's desktop, so ProBalance's foreground exemption and Foreground Boost depend on this.
/// Keeps one connection open (the engine drops the report when it closes) and sends a line whenever the
/// foreground process changes.
/// </summary>
public sealed class ForegroundReporter : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    // A write is the only way to notice the engine went away (e.g. restarted), so repeat the report now and then.
    private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly Func<int> _foregroundPid;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ForegroundReporter() : this(PipeNames.Foreground, ReadForegroundPid) { }

    internal ForegroundReporter(string pipeName, Func<int> foregroundPid)
    {
        _pipeName = pipeName;
        _foregroundPid = foregroundPid;
    }

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
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1000, token);
                await ReportChangesAsync(pipe, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
            {
                // Engine not running, restarting or refusing us: try again shortly.
            }

            try { await Task.Delay(ReconnectDelay, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReportChangesAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        int? lastSent = null;
        var lastSentAt = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            var pid = _foregroundPid();
            // Always sent on a new connection: the engine knows nothing about this desktop until we speak.
            if (pid != lastSent || DateTime.UtcNow - lastSentAt >= ResendInterval)
            {
                await writer.WriteLineAsync(Serialize(pid).AsMemory(), token);
                lastSent = pid;
                lastSentAt = DateTime.UtcNow;
            }
            await Task.Delay(PollInterval, token);
        }
    }

    internal static string Serialize(int pid) => JsonSerializer.Serialize(new CommandMessage
    {
        CommandType = CommandTypes.ReportForeground,
        TargetPid = pid
    });

    private static int ReadForegroundPid()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        cts?.Dispose();
    }
}
