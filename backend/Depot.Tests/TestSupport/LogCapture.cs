using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Depot.Tests.TestSupport;

// Keeps every log line, with its exception, so a test can check what reached the logs.
public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
