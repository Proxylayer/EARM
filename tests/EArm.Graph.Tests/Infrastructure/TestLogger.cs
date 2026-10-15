using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace EArm.Graph.Tests.Infrastructure;

internal sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception)
{
    /// <summary>Message plus the full exception text, i.e. everything a real sink could print.</summary>
    public string Text => Exception is null ? Message : Message + Environment.NewLine + Exception;
}

/// <summary>Thread-safe collector shared by every <see cref="TestLogger{T}"/> of one test.</summary>
internal sealed class LogSink
{
    private readonly List<LogEntry> _entries = [];
    private readonly object _gate = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public string AllText => string.Join(Environment.NewLine, Entries.Select(static e => $"[{e.Level}] {e.Category}#{e.EventId.Id}: {e.Text}"));

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }
}

/// <summary>An <see cref="ILogger{T}"/> enabled at every level (Trace included) that captures the formatted lines.</summary>
internal sealed class TestLogger<T> : ILogger<T>
{
    private readonly LogSink _sink;

    public TestLogger(LogSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _sink.Add(new LogEntry(typeof(T).Name, logLevel, eventId, formatter(state, exception), exception));
    }
}
