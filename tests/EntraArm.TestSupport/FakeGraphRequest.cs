using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Az;

namespace EntraArm.TestSupport;

/// <summary>One call the <see cref="FakeGraphClient"/> received, for assertions ("no writes in what-if", "PATCH carried X").</summary>
public sealed record FakeGraphRequest(
    string Method,
    string Path,
    GraphApi Api,
    IReadOnlyDictionary<string, string>? UrlParameters,
    IReadOnlyDictionary<string, string>? Headers,
    JsonNode? Body,
    RetryHint Hint)
{
    public bool IsWrite => Method is not "GET";

    public bool HasHeader(string name, string value)
    {
        return Headers is not null && Headers.Any(h => h.Key.Equals(name, System.StringComparison.OrdinalIgnoreCase) && h.Value.Contains(value, System.StringComparison.OrdinalIgnoreCase));
    }

    public override string ToString()
    {
        string query = UrlParameters is { Count: > 0 } ? "?" + string.Join("&", UrlParameters.Select(p => p.Key + "=" + p.Value)) : string.Empty;
        return $"{Method} {Path}{query}";
    }
}
