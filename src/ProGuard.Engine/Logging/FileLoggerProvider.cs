using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ProGuard.Engine.Logging;

/// <summary>
/// Appends log lines to a text file. A Windows service has no console, so without this the engine's log only
/// reaches the Event Log at Warning and above. When the file passes <see cref="MaxBytes"/> it is renamed to
/// <c>&lt;name&gt;.1.log</c> (replacing the previous one) and a new file is started.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>Default log file: <c>%ProgramData%\ProGuard\logs\engine.log</c>, next to the shared database.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProGuard", "logs", "engine.log");

    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly string _path;
    private readonly LogLevel _minimumLevel;

    public long MaxBytes { get; init; } = 10 * 1024 * 1024;

    // The minimum level defaults to everything: the Logging:LogLevel configuration (appsettings.json) does the filtering,
    // so a category can be turned up to Debug there.
    public FileLoggerProvider(string path, LogLevel minimumLevel = LogLevel.Trace)
    {
        _path = Path.GetFullPath(path);
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() { }

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(' ').Append(LevelName(level))
            .Append(' ').Append(ShortCategory(category))
            .Append(": ").Append(message);
        if (exception != null) line.AppendLine().Append(exception);
        line.AppendLine();

        lock (_lock)
        {
            try
            {
                RollIfNeeded();
                File.AppendAllText(_path, line.ToString());
            }
            catch (IOException)
            {
                // Logging must never take the engine down; a locked or full disk just loses the line.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes) return;
        File.Move(_path, RolledOverPath(_path), overwrite: true);
    }

    /// <summary>Where the log at <paramref name="path"/> goes when it rolls over: <c>engine.log</c> -> <c>engine.1.log</c>.</summary>
    public static string RolledOverPath(string path) => Path.ChangeExtension(path, ".1" + Path.GetExtension(path));

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    // "ProGuard.Engine.Services.ProBalanceEngine" -> "ProBalanceEngine"; keeps lines short.
    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
