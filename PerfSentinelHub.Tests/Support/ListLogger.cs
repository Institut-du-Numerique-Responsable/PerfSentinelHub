using Microsoft.Extensions.Logging;

namespace PerfSentinelHub.Tests.Support;

internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries.Select(entry => entry.Message)];
            }
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message)];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
