using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProGuard.Shared.Messages;

namespace ProGuard.UI.Services;

/// <summary>
/// Tells the engine which process owns this desktop's foreground window. The engine runs as a service in session 0
/// and can't see the user's desktop, so ProBalance's foreground exemption and Foreground Boost depend on this.
/// Keeps one connection open (the engine drops the report when it closes) and sends a line whenever the
/// foreground process changes.
/// <para>
/// Each report also carries how long this desktop has had no keyboard or mouse input, for IdleSaver: the engine
/// can't call <c>GetLastInputInfo</c> for the user's session from session 0. The engine ages the last report by
/// the time since it arrived, so reports while the user is away just confirm it; what matters is telling the
/// engine promptly when they come back, so a report that drops the idle time from a long value is sent at once.
/// </para>
/// </summary>
public sealed class ForegroundReporter : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    // A write is the only way to notice the engine went away (e.g. restarted), so repeat the report now and then.
    private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(5);
    // Input after the engine was told we'd been idle this long is reported immediately. Below it, the engine's aged
    // estimate is at most this plus ResendInterval too high until the next report, far under any IdleSaver timeout.
    private static readonly TimeSpan ReturnReportThreshold = ResendInterval;

    private readonly string _pipeName;
    private readonly Func<int> _foregroundPid;
    private readonly Func<TimeSpan?> _idleTime;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ForegroundReporter() : this(PipeNames.Foreground, ReadForegroundPid, ReadIdleTime) { }

    internal ForegroundReporter(string pipeName, Func<int> foregroundPid, Func<TimeSpan?>? idleTime = null)
    {
        _pipeName = pipeName;
        _foregroundPid = foregroundPid;
        _idleTime = idleTime ?? (() => null);
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
        TimeSpan? lastSentIdle = null;
        var lastSentAt = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            var pid = _foregroundPid();
            var idle = _idleTime();
            // Always sent on a new connection: the engine knows nothing about this desktop until we speak.
            if (ShouldSend(pid, idle, lastSent, lastSentIdle, DateTime.UtcNow - lastSentAt))
            {
                await writer.WriteLineAsync(Serialize(pid, idle).AsMemory(), token);
                lastSent = pid;
                lastSentIdle = idle;
                lastSentAt = DateTime.UtcNow;
            }
            await Task.Delay(PollInterval, token);
        }
    }

    internal static bool ShouldSend(int pid, TimeSpan? idle, int? lastSentPid, TimeSpan? lastSentIdle, TimeSpan sinceLastSend)
    {
        if (pid != lastSentPid || sinceLastSend >= ResendInterval) return true;
        // Idle time only ever grows, so a smaller value means the user is back.
        return lastSentIdle >= ReturnReportThreshold && (idle == null || idle < lastSentIdle);
    }

    internal static string Serialize(int pid, TimeSpan? idle = null) => JsonSerializer.Serialize(new CommandMessage
    {
        CommandType = CommandTypes.ReportForeground,
        TargetPid = pid,
        Argument = idle is { } t ? ((long)t.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : null
    });

    private static int ReadForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    /// <summary>Time since the last keyboard or mouse input on this desktop; null if Windows won't say.</summary>
    private static TimeSpan? ReadIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return null;
        // Both are milliseconds since boot in 32 bits; unsigned subtraction handles the 49.7-day wrap.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    // The UI's own desktop only: reading the foreground window and input time touches no other process.
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        cts?.Dispose();
    }
}
