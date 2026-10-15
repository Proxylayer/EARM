using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EntraArm.Az.Tests.Infrastructure;

/// <summary>A fixture document for <c>fake-az.ps1</c>: an ordered list of responses (see tests/fake-az/README.md).</summary>
internal sealed class FakeScenario
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    [JsonPropertyName("responses")]
    public List<FakeResponse> Responses { get; } = [];

    public FakeScenario Add(FakeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Responses.Add(response);
        return this;
    }

    public FakeScenario AddRange(IEnumerable<FakeResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        Responses.AddRange(responses);
        return this;
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, SerializerOptions);
    }
}

/// <summary>Optional selector of a <see cref="FakeResponse"/>; every present field must fit the call.</summary>
internal sealed class FakeMatch
{
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("urlContains")]
    public string? UrlContains { get; set; }

    [JsonPropertyName("argvContains")]
    public string? ArgvContains { get; set; }

    [JsonPropertyName("bodyContains")]
    public string? BodyContains { get; set; }
}

/// <summary>One scripted az reply.</summary>
internal sealed class FakeResponse
{
    [JsonPropertyName("match")]
    public FakeMatch? Match { get; set; }

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("stdoutJson")]
    public JsonNode? StdoutJson { get; set; }

    [JsonPropertyName("stdout")]
    public string? Stdout { get; set; }

    [JsonPropertyName("stderr")]
    public string? Stderr { get; set; }

    [JsonPropertyName("sleepSeconds")]
    public double? SleepSeconds { get; set; }

    [JsonPropertyName("barrier")]
    public FakeBarrier? Barrier { get; set; }

    /// <summary>Exit 0 with the JSON text on stdout.</summary>
    public static FakeResponse Json(string json)
    {
        return new FakeResponse { StdoutJson = JsonNode.Parse(json) };
    }

    public static FakeResponse Json(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new FakeResponse { StdoutJson = node.DeepClone() };
    }

    /// <summary>Exit 0 and nothing on stdout (what az prints for a 204).</summary>
    public static FakeResponse Empty()
    {
        return new FakeResponse();
    }

    /// <summary>A failure: the stderr text and a non-zero exit code.</summary>
    public static FakeResponse Error(string stderr, int exitCode = 1)
    {
        return new FakeResponse { ExitCode = exitCode, Stderr = stderr };
    }

    /// <summary>A failure replaying one of the canonical stderr shapes in <c>Fixtures/graph-errors.json</c>.</summary>
    public static FakeResponse GraphError(string fixtureName)
    {
        return Error(GraphErrorFixtures.Get(fixtureName));
    }

    public FakeResponse When(string? method = null, string? urlContains = null, string? argvContains = null, string? bodyContains = null)
    {
        Match = new FakeMatch
        {
            Method = method,
            UrlContains = urlContains,
            ArgvContains = argvContains,
            BodyContains = bodyContains,
        };
        return this;
    }

    public FakeResponse WithStderr(string stderr)
    {
        Stderr = stderr;
        return this;
    }

    public FakeResponse Sleeping(double seconds)
    {
        SleepSeconds = seconds;
        return this;
    }

    /// <summary>
    /// Holds the first <paramref name="parties"/> fake processes until that many are inside the replay window at the same
    /// time (exit 97 after <paramref name="timeoutSeconds"/> otherwise): a deterministic proof of real concurrency.
    /// </summary>
    public FakeResponse WithBarrier(int parties, double timeoutSeconds = 30)
    {
        Barrier = new FakeBarrier { Parties = parties, TimeoutSeconds = timeoutSeconds };
        return this;
    }
}

/// <summary>Cross-process rendezvous scripted into a <see cref="FakeResponse"/>.</summary>
internal sealed class FakeBarrier
{
    [JsonPropertyName("parties")]
    public int Parties { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public double TimeoutSeconds { get; set; }
}

/// <summary>Snapshot of the fake's concurrency gauge file.</summary>
internal readonly record struct FakeAzGauge(int Current, int MaxConcurrent, int Arrivals);
