using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The pure rules of the STS policies (token lifetime and claims mapping policies, Graph <c>stsPolicy</c>): <c>definition</c> is a string collection
/// that holds ONE escaped JSON document (Learn: tokenlifetimepolicy, claimsmappingpolicy resource pages). A template may declare the document as a
/// string or, as sugar, as a JSON object (serialised compactly before it is sent); either way the document must parse, have the policy's root element
/// (<c>TokenLifetimePolicy</c>, <c>ClaimsMappingPolicy</c>) and <c>"Version": 1</c>. Documents compare by their JSON content (object members sorted,
/// whitespace ignored), so a policy Graph serves reformatted is not drift: when the contents are equal the declared value adopts the live strings.
/// </summary>
public static class StsPolicyRules
{
    public const string DefinitionProperty = "definition";

    public const string DisplayNameProperty = "displayName";

    public const string OrganizationDefaultProperty = "isOrganizationDefault";

    /// <summary>Properties Graph assigns; never sent in a body.</summary>
    public static readonly string[] ServerAssignedProperties = ["id", "deletedDateTime"];

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>
    /// Checks and normalises <c>definition</c> in place (a one-element array of a compact JSON string) and refuses a declared
    /// <c>isOrganizationDefault: true</c> when <paramref name="allowOrganizationDefault"/> is false. <paramref name="checkRoot"/> adds the
    /// policy-specific checks of the root element's object. Returns the problems found.
    /// </summary>
    public static List<string> NormalizeDesired(JsonObject properties, string rootElement, bool allowOrganizationDefault, Func<JsonObject, IEnumerable<string>>? checkRoot)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootElement);
        var problems = new List<string>();
        if (TenantPolicyValues.TryGetDeclared(properties, DefinitionProperty, out var key, out var definition))
        {
            if (definition is not JsonArray array || array.Count != 1)
            {
                problems.Add($"properties.{DefinitionProperty} must be an array of exactly one policy document (a JSON string, or an object with the {rootElement} element)");
            }
            else if (!TenantPolicyValues.IsOpaque(array[0]))
            {
                string? document = DocumentText(array[0], out string? error);
                if (document is null)
                {
                    problems.Add($"properties.{DefinitionProperty}[0] {error}");
                }
                else
                {
                    problems.AddRange(CheckDocument(document, rootElement, checkRoot).Select(p => $"properties.{DefinitionProperty}[0] {p}"));
                    properties[key] = new JsonArray(document);
                }
            }
        }

        if (!allowOrganizationDefault
            && TenantPolicyValues.TryGetDeclared(properties, OrganizationDefaultProperty, out _, out var organizationDefault)
            && organizationDefault is JsonValue flag
            && flag.GetValueKind() == JsonValueKind.True)
        {
            problems.Add($"properties.{OrganizationDefaultProperty} cannot be true: Graph applies this policy to service principals only and cannot set it tenant-wide");
        }

        return problems;
    }

    /// <summary>When the declared and live documents have the same JSON content, the declared <c>definition</c> adopts the live strings (no drift).</summary>
    public static void AdoptLiveDefinition(JsonObject properties, JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(live);
        if (TenantPolicyValues.TryGetDeclared(properties, DefinitionProperty, out var key, out var declared)
            && JsonObjects.TryGetIgnoreCase(live, DefinitionProperty, out var liveDefinition)
            && CanonicalDefinition(declared) is { } left
            && CanonicalDefinition(liveDefinition) is { } right
            && left.SequenceEqual(right, StringComparer.Ordinal))
        {
            properties[key] = JsonValues.Clone(liveDefinition);
        }
    }

    /// <summary>The canonical text of every document of a <c>definition</c> array, or <c>null</c> when one is not a parseable JSON string.</summary>
    public static IReadOnlyList<string>? CanonicalDefinition(JsonNode? definition)
    {
        if (definition is not JsonArray array)
        {
            return null;
        }

        var canonical = new List<string>();
        foreach (var item in array)
        {
            if (!JsonValues.TryGetString(item, out var text) || Parse(text) is not { } parsed)
            {
                return null;
            }

            canonical.Add(Canonical(parsed));
        }

        return canonical;
    }

    /// <summary>The JSON text of a declared document: a string as written (trimmed), an object serialised compactly; <c>null</c> (with the reason) otherwise.</summary>
    private static string? DocumentText(JsonNode? item, out string? error)
    {
        error = null;
        if (JsonValues.TryGetString(item, out var text))
        {
            return text.Trim();
        }

        if (item is JsonObject obj)
        {
            return obj.ToJsonString(Compact);
        }

        error = "must be the policy document as a JSON string or object";
        return null;
    }

    private static IEnumerable<string> CheckDocument(string document, string rootElement, Func<JsonObject, IEnumerable<string>>? checkRoot)
    {
        if (Parse(document) is not JsonObject parsed)
        {
            yield return "is not a JSON object";
            yield break;
        }

        if (parsed.Count != 1 || !JsonObjects.TryGetIgnoreCase(parsed, rootElement, out var rootNode) || rootNode is not JsonObject root)
        {
            yield return $"must have exactly one root element, {rootElement}, holding an object";
            yield break;
        }

        if (!JsonObjects.TryGetIgnoreCase(root, "Version", out var version) || !TenantPolicyValues.IsIntegerInRange(version, 1, 1))
        {
            yield return $"needs \"Version\": 1 in {rootElement}";
        }

        foreach (string problem in checkRoot?.Invoke(root) ?? [])
        {
            yield return problem;
        }
    }

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Compact JSON with object members sorted by name (ordinal), so member order and whitespace do not matter.</summary>
    private static string Canonical(JsonNode? node)
    {
        return node switch
        {
            JsonObject obj => "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
            JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
            null => "null",
            _ => node.ToJsonString(),
        };
    }
}
