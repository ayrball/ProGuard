using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ProGuard.Shared.Messages;

namespace ProGuard.Shared.Client;

/// <summary>The engine answered, but refused or failed the command. <see cref="Exception.Message"/> is user-facing.</summary>
public sealed class EngineCommandException : Exception
{
    public EngineCommandException(string message) : base(message) { }

    /// <summary>The engine refused because the caller lacks the rights (see IpcAuthorization).</summary>
    public bool IsAccessDenied => Message.StartsWith("Access denied", StringComparison.Ordinal);
}

/// <summary>The engine isn't running, or stopped answering.</summary>
public sealed class EngineUnavailableException : Exception
{
    public EngineUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Talks to the engine over its command pipe (<see cref="PipeNames.Engine"/>): one JSON <see cref="CommandMessage"/>
/// per line, answered by exactly one <see cref="IpcMessage"/> line. Connects lazily, reconnects after a failure, and
/// sends one request at a time (callers on several threads are queued). Safe to share across the whole app.
/// </summary>
public sealed class EngineClient : IAsyncDisposable
{
    /// <summary>Overrides the pipe name, so tests and the preview tool can run a private engine.</summary>
    public const string PipeNameVariable = "PROGUARD_PIPE";

    public static string DefaultPipeName =>
        Environment.GetEnvironmentVariable(PipeNameVariable) is { Length: > 0 } name ? name : PipeNames.Engine;

    /// <summary>Serializer options for arguments and results. Matches the engine's (case-insensitive on read).</summary>
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public EngineClient(string? pipeName = null, TimeSpan? connectTimeout = null, TimeSpan? requestTimeout = null)
    {
        _pipeName = pipeName ?? DefaultPipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(1.5);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(8);
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>Runs a <see cref="CommandTypes"/> command whose result data is ignored.</summary>
    /// <param name="argument">Sent as-is when a string, as invariant text when a number or bool, otherwise as JSON.</param>
    public async Task CommandAsync(string commandType, int? pid = null, object? argument = null, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(commandType, pid, argument, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a <see cref="CommandTypes"/> command and deserializes its <see cref="CommandResult.Data"/>.</summary>
    public async Task<T> QueryAsync<T>(string commandType, int? pid = null, object? argument = null, CancellationToken cancellationToken = default)
    {
        var result = await SendCommandAsync(commandType, pid, argument, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(result.Data))
            throw new EngineCommandException($"The engine sent no result for '{commandType}'.");

        try
        {
            return JsonSerializer.Deserialize<T>(result.Data, Json)
                ?? throw new EngineCommandException($"The engine sent an empty result for '{commandType}'.");
        }
        catch (JsonException ex)
        {
            // Most likely a UI and engine from different builds.
            throw new EngineCommandException($"The engine's answer to '{commandType}' doesn't match this version of ProGuard. ({ex.Message})");
        }
    }

    private async Task<CommandResult> SendCommandAsync(string commandType, int? pid, object? argument, CancellationToken cancellationToken)
    {
        var request = new CommandMessage { CommandType = commandType, TargetPid = pid, Argument = FormatArgument(argument) };
        var reply = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        CommandResult? result = null;
        if (reply.Type == MessageType.CommandResult)
        {
            try { result = JsonSerializer.Deserialize<CommandResult>(reply.Payload, Json); }
            catch (JsonException) { /* reported below */ }
        }
        if (result == null)
            throw new EngineCommandException($"The engine sent an unexpected reply ({reply.Type}) to '{commandType}'.");
        if (!result.Success)
            throw new EngineCommandException(result.Error ?? $"The engine could not run '{commandType}'.");
        return result;
    }

    /// <summary>Sends any request and returns the engine's reply message.</summary>
    public async Task<IpcMessage> SendAsync(IpcMessage request, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(request, request.GetType());

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);

            // A connection left from earlier may have been closed by the engine (a service restart, say). That only
            // shows when writing, so a failed write on a reused connection gets one retry on a fresh one. Once a
            // request has been written it is never resent: the engine may already have acted on it.
            for (var attempt = 0; ; attempt++)
            {
                var reused = IsConnected;
                try
                {
                    await EnsureConnectedAsync(timeout.Token).ConfigureAwait(false);
                    await _writer!.WriteLineAsync(line.AsMemory(), timeout.Token).ConfigureAwait(false);
                    await _writer.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (IOException) when (reused && attempt == 0)
                {
                    Disconnect();
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw Unavailable(ex);
                }

                try
                {
                    var reply = await _reader!.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                        ?? throw new IOException("The engine closed the connection.");
                    return JsonSerializer.Deserialize<IpcMessage>(reply, Json)
                        ?? throw new IOException("The engine sent an unreadable response.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw Unavailable(ex);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A late reply would be read as the answer to the next request, so this connection can't be reused.
            Disconnect();
            throw new EngineUnavailableException("The engine stopped responding.");
        }
        catch (OperationCanceledException)
        {
            Disconnect();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Exception Unavailable(Exception ex)
    {
        Disconnect();
        return ex switch
        {
            UnauthorizedAccessException => new EngineUnavailableException(
                "Windows refused the connection to the ProGuard engine. Run ProGuard as administrator to connect to it.", ex),
            JsonException => new EngineUnavailableException("The engine sent a reply this version of ProGuard can't read.", ex),
            _ => new EngineUnavailableException("The ProGuard engine isn't running.", ex)
        };
    }

    internal static string? FormatArgument(object? argument) => argument switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        Guid g => g.ToString(),
        IFormattable f when argument is int or long or double or float or decimal or short or byte => f.ToString(null, CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        _ => JsonSerializer.Serialize(argument, argument.GetType(), Json)
    };

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) return;
        Disconnect();

        // A pipe stream can't be reconnected once closed, so every attempt uses a new one.
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)_connectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, Utf8, bufferSize: 4096, leaveOpen: true) { NewLine = "\n" };
    }

    private void Disconnect()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
