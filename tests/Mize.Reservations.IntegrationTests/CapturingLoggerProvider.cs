using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Mize.Reservations.IntegrationTests;

internal sealed record LogLine(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>Collects every log line the host writes, so tests can assert on what operators would actually see.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<LogLine> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Lines);
    public void Dispose() { }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true; // the host's configured filters decide what reaches a provider
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(new LogLine(category, logLevel, formatter(state, exception), exception));
    }
}
