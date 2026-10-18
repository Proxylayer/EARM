using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace EArm.Tests.Engine;

/// <summary>
/// Records every log event (its name and formatted message). With <c>throwOn</c>, the event of that name throws: an engine-level step that fails
/// outside any per-resource catch.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly string? _throwOn;
    private readonly List<(string Name, string Message)> _events = [];

    public RecordingLogger(string? throwOn = null)
    {
        _throwOn = throwOn;
    }

    public IReadOnlyList<(string Name, string Message)> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        lock (_events)
        {
            _events.Add((eventId.Name ?? string.Empty, formatter(state, exception)));
        }

        if (_throwOn is not null && eventId.Name == _throwOn)
        {
            throw new InvalidOperationException("a logging sink failed");
        }
    }
}
