using System;
using System.Collections.Generic;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>Tests that change process-wide environment variables run one after another (never in parallel with each other).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Review1BEnvironmentCollection
{
    public const string Name = "ProcessEnvironment";
}

/// <summary>Sets environment variables for a test and restores the previous values on dispose.</summary>
internal sealed class EnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _saved = new(StringComparer.Ordinal);

    public EnvironmentScope Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name))
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        foreach (var pair in _saved)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
