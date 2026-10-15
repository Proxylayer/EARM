using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace EArm.Az.Tests.Infrastructure;

/// <summary>The canonical az stderr shapes from <c>Fixtures/graph-errors.json</c>, shared by parser tests and fake scenarios.</summary>
internal static class GraphErrorFixtures
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Entries = new(Load);

    public static IEnumerable<string> Names => Entries.Value.Keys;

    public static string Get(string name)
    {
        return Entries.Value.TryGetValue(name, out var text)
            ? text
            : throw new KeyNotFoundException($"No '{name}' entry in Fixtures/graph-errors.json.");
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        var path = Path.Combine(FakeAzContext.FixturesRoot, "graph-errors.json");
        var json = JsonNode.Parse(File.ReadAllText(path, FakeAzContext.Utf8NoBom)) as JsonObject
            ?? throw new InvalidDataException("graph-errors.json is not a JSON object.");

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in json)
        {
            entries[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
        }

        return entries;
    }
}
