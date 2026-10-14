using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using Microsoft.Extensions.Logging;

namespace EntraArm.Providers.Tests.Infrastructure;

/// <summary>Every log line the engine components of one <see cref="ProviderHost"/> emitted (level, category, message, exception).</summary>
internal sealed class LogSink
{
    private readonly object _gate = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    public RecordingLogger<T> For<T>()
    {
        return new RecordingLogger<T>(this);
    }

    public bool Contains(string text)
    {
        return Lines.Any(line => line.Contains(text, StringComparison.Ordinal));
    }
}

/// <summary>An <see cref="ILogger{T}"/> at every level that records the formatted lines in a <see cref="LogSink"/> (so "no secret in any log line" can be asserted).</summary>
internal sealed class RecordingLogger<T>(LogSink sink) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        string line = $"{logLevel} {typeof(T).Name}: {formatter(state, exception)}";
        if (exception is not null)
        {
            line += " " + exception;
        }

        sink.Add(line);
    }
}

/// <summary>Counts the delays the retry policy asks for and returns immediately (no real waiting).</summary>
internal sealed class NoDelayProvider : IDelayProvider
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _count);
        return Task.CompletedTask;
    }
}
