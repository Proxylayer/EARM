using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Loading;

namespace EArm.Core;

/// <summary>
/// The parameter values supplied for a deployment: an ARM <c>deploymentParameters</c> file plus inline
/// <c>-p name=value</c> overrides (inline wins). Values are raw until the loader binds them to the template's
/// definitions (type conversion of inline text, <c>securestring</c>/<c>secureObject</c> wrapping).
/// </summary>
public sealed class ParameterSet
{
    private readonly Dictionary<string, ParameterValue> _values;

    private ParameterSet(Dictionary<string, ParameterValue> values, string? schema, string? contentVersion, string? sourcePath, JsonElement? rootElement)
    {
        _values = values;
        Schema = schema;
        ContentVersion = contentVersion;
        SourcePath = sourcePath;
        RootElement = rootElement;
    }

    public static ParameterSet Empty { get; } = new(new Dictionary<string, ParameterValue>(StringComparer.OrdinalIgnoreCase), null, null, null, null);

    /// <summary>The parameter file's <c>$schema</c>, when loaded from a file.</summary>
    public string? Schema { get; }

    public string? ContentVersion { get; }

    /// <summary>The parameter file path, when loaded from a file.</summary>
    public string? SourcePath { get; }

    /// <summary>The parsed parameter file root, for JSON Schema validation; <c>null</c> without a file.</summary>
    public JsonElement? RootElement { get; }

    /// <summary>Values keyed by parameter name (case-insensitive, ARM rule).</summary>
    public IReadOnlyDictionary<string, ParameterValue> Values => _values;

    public static ParameterSet FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = InputFiles.ReadAllBytes(path, "Parameter");
        return Parse(bytes, path);
    }

    public static ParameterSet Parse(string json, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(Encoding.UTF8.GetBytes(json), sourcePath);
    }

    public static ParameterSet Parse(ReadOnlyMemory<byte> utf8, string? sourcePath = null)
    {
        utf8 = LoneSurrogates.Replace(utf8, out var loneSurrogates);
        if (loneSurrogates.Count > 0)
        {
            var first = loneSurrogates[0];
            throw new TemplateLoadException(
                DiagnosticCodes.InvalidJson,
                $"The parameter file contains an invalid Unicode escape (lone surrogate) at {first.Path}; a surrogate must be written as a pair (high then low), or use the character itself.",
                sourcePath,
                first.Location,
                first.Path);
        }

        JsonElement element;
        JsonNode? node;
        try
        {
            (element, node) = TemplateJson.Parse(utf8, DuplicateKeys.ParameterFileNameMaps);
        }
        catch (DuplicateJsonKeyException ex)
        {
            throw new TemplateLoadException(DiagnosticCodes.DuplicateJsonKey, ex.Message, sourcePath, ex.First.Location, ex.First.Path, inner: ex);
        }
        catch (InputTooLargeException ex)
        {
            throw new TemplateLoadException(DiagnosticCodes.InputTooLarge, $"Parameter file is too large: {ex.Message}", sourcePath, inner: ex);
        }
        catch (JsonException ex)
        {
            throw new TemplateLoadException(DiagnosticCodes.InvalidJson, $"Parameter file is not valid JSON: {ex.Message}", sourcePath, TemplateJson.LocationOf(ex), inner: ex);
        }

        if (node is not JsonObject root)
        {
            throw new TemplateLoadException(DiagnosticCodes.InvalidParameterFile, "The parameter file root must be a JSON object.", sourcePath, path: JsonPath.Root);
        }

        // ARM shape: { "$schema", "contentVersion", "parameters": { name: { value | reference } } }.
        // The bare shape { name: { value } } (accepted by the Azure CLI) is tolerated when there is no "parameters" key.
        JsonObject map;
        JsonPath mapPath;
        if (root.TryGetPropertyValue("parameters", out var parametersNode))
        {
            map = parametersNode as JsonObject
                ?? throw new TemplateLoadException(DiagnosticCodes.InvalidParameterFile, "'parameters' must be an object.", sourcePath, path: JsonPath.Of("parameters"));
            mapPath = JsonPath.Of("parameters");
        }
        else
        {
            map = root;
            mapPath = JsonPath.Root;
        }

        var values = new Dictionary<string, ParameterValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in map)
        {
            if (ReferenceEquals(map, root) && (entry.Key == "$schema" || entry.Key == "contentVersion" || entry.Key == "metadata"))
            {
                continue;
            }

            var entryPath = mapPath.Append(entry.Key);
            if (entry.Value is not JsonObject entryObject)
            {
                throw new TemplateLoadException(DiagnosticCodes.InvalidParameterFile, $"Parameter '{entry.Key}' must be an object with a 'value' or 'reference' property.", sourcePath, path: entryPath);
            }

            bool hasValue = entryObject.TryGetPropertyValue("value", out var valueNode);
            bool hasReference = entryObject.TryGetPropertyValue("reference", out var referenceNode);
            if (hasValue == hasReference)
            {
                throw new TemplateLoadException(DiagnosticCodes.InvalidParameterFile, $"Parameter '{entry.Key}' must have exactly one of 'value' or 'reference'.", sourcePath, path: entryPath);
            }

            if (values.ContainsKey(entry.Key))
            {
                throw new TemplateLoadException(DiagnosticCodes.InvalidParameterFile, $"Parameter '{entry.Key}' is defined more than once (names are case-insensitive).", sourcePath, path: entryPath);
            }

            if (hasValue)
            {
                values[entry.Key] = new ParameterValue { Value = valueNode, Source = ParameterValueSource.File, SourcePath = sourcePath };
            }
            else
            {
                values[entry.Key] = new ParameterValue
                {
                    Reference = ParseKeyVaultReference(referenceNode, entry.Key, sourcePath, entryPath.Append("reference"), utf8),
                    Source = ParameterValueSource.File,
                    SourcePath = sourcePath,
                };
            }
        }

        return new ParameterSet(values, TemplateJson.AsString(root["$schema"]), TemplateJson.AsString(root["contentVersion"]), sourcePath, element);
    }

    /// <summary>Returns a copy with an inline <c>name=value</c> override applied (replaces any earlier value for that name).</summary>
    public ParameterSet WithInline(string name, string rawText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(rawText);

        // A command-line value is UTF-16 and may hold an unpaired surrogate (Windows allows it); stored as it is, the first write or schema pass over it throws.
        rawText = LoneSurrogates.Scrub(rawText);

        var values = new Dictionary<string, ParameterValue>(_values, StringComparer.OrdinalIgnoreCase)
        {
            [name] = new ParameterValue { RawText = rawText, Value = JsonValue.Create(rawText), Source = ParameterValueSource.Inline },
        };
        return new ParameterSet(values, Schema, ContentVersion, SourcePath, RootElement);
    }

    /// <summary>Returns a copy with every value of <paramref name="other"/> applied on top of this set.</summary>
    public ParameterSet Merge(ParameterSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var values = new Dictionary<string, ParameterValue>(_values, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in other._values)
        {
            values[pair.Key] = pair.Value;
        }

        return new ParameterSet(values, other.Schema ?? Schema, other.ContentVersion ?? ContentVersion, other.SourcePath ?? SourcePath, other.RootElement ?? RootElement);
    }

    /// <summary>Splits an inline token <c>name=value</c> at the first <c>=</c>.</summary>
    public static bool TryParseInline(string token, out string name, out string rawText)
    {
        name = string.Empty;
        rawText = string.Empty;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        int eq = token.IndexOf('=');
        if (eq <= 0)
        {
            return false;
        }

        name = token[..eq].Trim();
        rawText = token[(eq + 1)..];
        return name.Length > 0;
    }

    private static KeyVaultReference ParseKeyVaultReference(JsonNode? node, string parameterName, string? sourcePath, JsonPath path, ReadOnlyMemory<byte> utf8)
    {
        if (node is not JsonObject reference)
        {
            throw new TemplateLoadException(DiagnosticCodes.InvalidParameterFile, $"Parameter '{parameterName}': 'reference' must be an object.", sourcePath, Locate(utf8, path), path);
        }

        string? vaultId = reference["keyVault"] is JsonObject keyVault ? TemplateJson.AsString(keyVault["id"]) : null;
        string? secretName = TemplateJson.AsString(reference["secretName"]);
        string? secretVersion = TemplateJson.AsString(reference["secretVersion"]);
        if (secretVersion is null && reference["secretVersion"] is not null)
        {
            // A pinned version that is not a string (for example the number 2) must not be dropped silently: the latest version would be
            // resolved instead. Numbers are not coerced; an absent or explicit-null secretVersion means "latest".
            JsonPath versionPath = path.Append("secretVersion");
            throw new TemplateLoadException(DiagnosticCodes.InvalidParameterFile, $"Parameter '{parameterName}': secretVersion must be a string.", sourcePath, Locate(utf8, versionPath), versionPath);
        }

        if (string.IsNullOrWhiteSpace(vaultId) || string.IsNullOrWhiteSpace(secretName))
        {
            throw new TemplateLoadException(DiagnosticCodes.InvalidParameterFile, $"Parameter '{parameterName}': a Key Vault reference needs 'keyVault.id' and 'secretName'.", sourcePath, Locate(utf8, path), path);
        }

        return KeyVaultReference.Create(vaultId, secretName, secretVersion);
    }

    /// <summary>The line/column of a path (or its nearest recorded ancestor); built only on the error path.</summary>
    private static SourceLocation? Locate(ReadOnlyMemory<byte> utf8, JsonPath path)
    {
        return SourceMap.Build(utf8).FindNearest(path);
    }
}
