using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace EntraArm.Az.Tests.Infrastructure;

/// <summary>One line of the fake's argv file: what az was called with.</summary>
internal sealed record FakeAzRecord(
    IReadOnlyList<string> Argv,
    string? BodyFile,
    string? Body,
    IReadOnlyDictionary<string, string?> Env,
    string Cwd)
{
    public string ArgvText => string.Join(' ', Argv);

    /// <summary>The element following <paramref name="flag"/>, or null.</summary>
    public string? ValueAfter(string flag)
    {
        var index = IndexOf(flag);
        return index >= 0 && index + 1 < Argv.Count ? Argv[index + 1] : null;
    }

    /// <summary>All values following <paramref name="flag"/> up to the next option (<c>--x</c> or <c>-o</c>).</summary>
    public IReadOnlyList<string> Section(string flag)
    {
        var index = IndexOf(flag);
        if (index < 0)
        {
            return [];
        }

        var values = new List<string>();
        for (var i = index + 1; i < Argv.Count; i++)
        {
            var element = Argv[i];
            if (element.StartsWith("--", StringComparison.Ordinal) || element == "-o")
            {
                break;
            }

            values.Add(element);
        }

        return values;
    }

    public JsonNode? BodyJson => Body is null ? null : JsonNode.Parse(Body);

    private int IndexOf(string element)
    {
        for (var i = 0; i < Argv.Count; i++)
        {
            if (string.Equals(Argv[i], element, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public static IReadOnlyList<FakeAzRecord> ReadAll(string path)
    {
        var text = FakeAzFiles.ReadSettledText(path);
        if (text is null)
        {
            return [];
        }

        var records = new List<FakeAzRecord>();
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var json = JsonNode.Parse(line) as JsonObject
                ?? throw new InvalidDataException("fake-az wrote a non-object argv line: " + line);

            var argv = json["argv"] as JsonArray ?? throw new InvalidDataException("fake-az argv line has no argv array: " + line);
            var env = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (json["env"] is JsonObject envObject)
            {
                foreach (var pair in envObject)
                {
                    env[pair.Key] = pair.Value?.GetValue<string>();
                }
            }

            records.Add(new FakeAzRecord(
                argv.Select(static n => n?.GetValue<string>() ?? string.Empty).ToList(),
                json["bodyFile"]?.GetValue<string>(),
                json["body"]?.GetValue<string>(),
                env,
                json["cwd"]?.GetValue<string>() ?? string.Empty));
        }

        return records;
    }
}
