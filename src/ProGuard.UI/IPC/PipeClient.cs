using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProGuard.Shared.Messages;

namespace ProGuard.UI.IPC;

/// <summary>
/// Client side of the engine's named pipe. Requests are newline-delimited JSON and the engine answers each one
/// with exactly one line, so only one request is in flight at a time. A broken or timed-out connection is dropped
/// and the next request reconnects.
/// </summary>
public class PipeClient : IDisposable
{
    /// <summary>Environment variable that overrides <see cref="DefaultPipeName"/>.</summary>
    public const string PipeNameVariable = "PROGUARD_PIPE_NAME";

    /// <summary>
    /// The engine's command pipe (<see cref="PipeNames.Engine"/>), unless <see cref="PipeNameVariable"/> names another. The
    /// tests set it to a pipe no engine serves, so a view model they create can never drive an installed engine.
    /// </summary>
    public static string DefaultPipeName =>
        Environment.GetEnvironmentVariable(PipeNameVariable) is { Length: > 0 } name ? name : PipeNames.Engine;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _wasConnected;
    private bool _disposed;

    public PipeClient(string? pipeName = null)
    {
        PipeName = pipeName ?? DefaultPipeName;
    }

    public string PipeName { get; }

    /// <summary>How long to wait for the engine's pipe to accept a connection.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a request may take, from writing it to reading the reply, before the connection is given up on.
    /// Writes count too: the pipe has no buffer, so a write waits until the engine reads it.
    /// </summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>
    /// Whether a server has created the pipe, without connecting to it. Connecting to a pipe that doesn't exist waits
    /// for the whole <see cref="ConnectTimeout"/>; this answers at once.
    /// </summary>
    public bool ServerExists()
    {
        if (IsConnected) return true;
        try
        {
            foreach (var path in Directory.EnumerateFiles(@"\\.\pipe\"))
            {
                if (string.Equals(Path.GetFileName(path), PipeName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // can't tell: let the connection attempt decide
        }
    }

    /// <summary>Raised with the new state whenever the connection to the engine opens or is lost.</summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>Connects if not already connected. Returns false when the engine isn't listening.</summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends one request and returns the engine's reply, or null when the engine couldn't be reached or didn't
    /// answer within <see cref="ResponseTimeout"/>.
    /// </summary>
    /// <remarks>Awaits here don't resume on the caller's context, so a synchronous <see cref="Disconnect"/> on the UI thread can't deadlock with a pending request.</remarks>
    public async Task<IpcMessage?> SendAsync(IpcMessage request, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(request, request.GetType());

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A connection left over from earlier may have been closed by the engine (e.g. a service restart);
            // that only shows up when writing, so a failed write gets one retry on a fresh connection. Once the
            // request has been written it is not resent, because the engine may already have acted on it.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var reused = IsConnected;
                if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false)) return null;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ResponseTimeout);
                try
                {
                    await _writer!.WriteLineAsync(line.AsMemory(), timeout.Token).ConfigureAwait(false);
                }
                catch (IOException) when (reused)
                {
                    DropConnection();
                    continue;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    DropConnection();
                    return null;
                }
                catch (OperationCanceledException)
                {
                    DropConnection();
                    if (cancellationToken.IsCancellationRequested) throw;
                    Debug.WriteLine($"Engine did not accept a request within {ResponseTimeout}");
                    return null;
                }

                return await ReadReplyAsync(timeout.Token, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Disconnect()
    {
        _gate.Wait();
        try
        {
            DropConnection();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<IpcMessage?> ReadReplyAsync(CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _reader!.ReadLineAsync(timeoutToken).ConfigureAwait(false);
            if (reply == null)
            {
                DropConnection(); // engine closed the pipe
                return null;
            }
            return JsonSerializer.Deserialize<IpcMessage>(reply, JsonOptions);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A late reply would be read as the answer to the next request, so the connection can't be reused.
            Debug.WriteLine($"Engine did not reply within {ResponseTimeout}");
            DropConnection();
            return null;
        }
        catch (OperationCanceledException)
        {
            DropConnection();
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            DropConnection();
            return null;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Malformed reply from engine: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected) return true;

        DropConnection();
        // A pipe stream can't be reconnected once it has been closed, so every attempt uses a new one.
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Engine pipe connection failed: {ex.Message}");
            pipe.Dispose();
            return false;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe);
        _writer = new StreamWriter(pipe) { AutoFlush = true };
        SetConnected(true);
        return true;
    }

    private void DropConnection()
    {
        // Disposing the reader and writer would close the pipe twice; the pipe owns the handle.
        try { _pipe?.Dispose(); } catch { /* already broken */ }
        _pipe = null;
        _reader = null;
        _writer = null;
        SetConnected(false);
    }

    private void SetConnected(bool connected)
    {
        if (connected == _wasConnected) return;
        _wasConnected = connected;
        ConnectionChanged?.Invoke(this, connected);
    }
}
