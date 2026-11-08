using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StackInspection.Application.Abstractions;

namespace StackInspection.Infrastructure.Logging;

/// <summary>
/// Logging provider yang menyalin log ke penangkapan yang sedang aktif di alur async yang sama (<see cref="AsyncLocal{T}"/>),
/// sehingga request lain yang berjalan bersamaan tidak ikut tercampur. Di luar penangkapan, provider ini tidak melakukan
/// apa pun; provider lain (console) tetap menulis log seperti biasa. Filter level dari konfigurasi Logging berlaku.
/// </summary>
public sealed class ProcessingLogCollector : ILoggerProvider, IProcessingLog
{
    /// <summary>Batas baris per penangkapan supaya response tidak membengkak.</summary>
    public const int MaxEntries = 200;

    private static readonly AsyncLocal<Capture?> Current = new();

    public IProcessingLogCapture Begin()
    {
        Capture capture = new(Current.Value);
        Current.Value = capture;
        return capture;
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(ShortName(categoryName));

    public void Dispose()
    {
    }

    private static string ShortName(string category) => category[(category.LastIndexOf('.') + 1)..];

    private sealed class Capture(Capture? previous) : IProcessingLogCapture
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<ProcessingLogEntry> _entries = [];

        public IReadOnlyList<ProcessingLogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public void Add(LogLevel level, string source, string message)
        {
            lock (_entries)
            {
                if (_entries.Count < MaxEntries)
                {
                    _entries.Add(new ProcessingLogEntry(_clock.ElapsedMilliseconds, level.ToString(), source, message));
                }
            }
        }

        public void Dispose() => Current.Value = previous;
    }

    private sealed class CapturingLogger(string source) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => Current.Value is not null && logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (Current.Value is Capture capture && logLevel >= LogLevel.Information)
            {
                string message = formatter(state, exception);
                capture.Add(logLevel, source, exception is null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})");
            }
        }
    }
}
