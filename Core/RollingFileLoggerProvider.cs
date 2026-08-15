using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NDIJobConfigurator.Core;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const long MaximumFileBytes = 5 * 1024 * 1024;
    private const int RetainedFiles = 10;
    private readonly object _gate = new();
    private readonly string _directory;
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private int _sequence;
    private long _writtenBytes;
    private bool _disposed;
    private int _disabled;

    public RollingFileLoggerProvider(string directory)
    {
        _directory = directory;
        try { Directory.CreateDirectory(_directory); }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            Interlocked.Exchange(ref _disabled, 1);
            Debug.WriteLine($"Kiloview file logging disabled: {exception.Message}");
        }
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        if (Volatile.Read(ref _disabled) == 1) return;
        var timestamp = DateTimeOffset.Now;
        var line = $"{timestamp:O} [{level}] {category}: {message}";
        if (exception is not null) line += Environment.NewLine + exception;

        lock (_gate)
        {
            if (_disposed || Volatile.Read(ref _disabled) == 1) return;
            try
            {
                EnsureWriter(timestamp);
                _writer!.WriteLine(line);
                _writer.Flush();
                _writtenBytes += System.Text.Encoding.UTF8.GetByteCount(line + Environment.NewLine);
            }
            catch (Exception writeException) when (IsFileFailure(writeException))
            {
                Disable(writeException);
            }
        }
    }

    private void Disable(Exception exception)
    {
        Interlocked.Exchange(ref _disabled, 1);
        try { _writer?.Dispose(); }
        catch (Exception disposeException) when (IsFileFailure(disposeException))
        {
            Debug.WriteLine($"Kiloview log writer cleanup failed: {disposeException.Message}");
        }
        _writer = null;
        Debug.WriteLine($"Kiloview file logging disabled: {exception.Message}");
    }

    private static bool IsFileFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        System.Security.SecurityException or
        NotSupportedException;

    private void EnsureWriter(DateTimeOffset timestamp)
    {
        var date = DateOnly.FromDateTime(timestamp.LocalDateTime);
        if (_writer is not null && date == _writerDate && _writtenBytes < MaximumFileBytes) return;

        _writer?.Dispose();
        _writer = null;
        if (date != _writerDate) _sequence = 0;
        else _sequence++;
        _writerDate = date;

        string path;
        do
        {
            var suffix = _sequence == 0 ? string.Empty : $"-{_sequence:00}";
            path = Path.Combine(_directory, $"kiloview-{date:yyyy-MM-dd}{suffix}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < MaximumFileBytes) break;
            _sequence++;
        } while (true);

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        _writtenBytes = stream.Length;
        RemoveExpiredFiles();
    }

    private void RemoveExpiredFiles()
    {
        foreach (var file in new DirectoryInfo(_directory)
                     .EnumerateFiles("kiloview-*.log")
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Skip(RetainedFiles))
        {
            try { file.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Dispose(); }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                Debug.WriteLine($"Kiloview log writer cleanup failed: {exception.Message}");
            }
            _writer = null;
        }
    }

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel)
        {
            var minimum = category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                          || category.StartsWith("System.Net.Http", StringComparison.Ordinal)
                ? LogLevel.Warning
                : LogLevel.Information;
            return logLevel >= minimum;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
