using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace EArm.Integration.Tests;

/// <summary>Keeps warnings and errors of the production stack (retries, failed deletes) so the cleanup report can include them.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(categoryName, _lines);
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _lines;

        public CapturingLogger(string category, ConcurrentQueue<string> lines)
        {
            _category = category;
            _lines = lines;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Warning;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            _lines.Enqueue($"{logLevel} {_category}: {formatter(state, exception)}");
        }
    }
}
