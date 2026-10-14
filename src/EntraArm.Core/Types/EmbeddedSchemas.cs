using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace EntraArm.Core.Types;

/// <summary>
/// Access to the JSON Schema files embedded in this assembly from <c>/schemas</c>. Resource names use forward
/// slashes: <c>schemas/2026-10-01/deploymentTemplate.json</c>.
/// </summary>
public static class EmbeddedSchemas
{
    public const string SchemaVersion = "2026-10-01";
    public const string TemplateSchemaResource = "schemas/" + SchemaVersion + "/deploymentTemplate.json";
    public const string ParametersSchemaResource = "schemas/" + SchemaVersion + "/deploymentParameters.json";
    public const string TypeSchemaPrefix = "schemas/" + SchemaVersion + "/types/" + ResourceIdentifier.ProviderNamespace + "/";

    private static readonly Assembly Assembly = typeof(EmbeddedSchemas).Assembly;

    /// <summary>All embedded schema resource names (normalised to forward slashes), sorted.</summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            var names = new List<string>();
            foreach (var raw in Assembly.GetManifestResourceNames())
            {
                string name = Normalize(raw);
                if (name.StartsWith("schemas/", StringComparison.Ordinal))
                {
                    names.Add(name);
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }

    /// <summary>Resource names under <paramref name="prefix"/>.</summary>
    public static IReadOnlyList<string> NamesUnder(string prefix)
    {
        var result = new List<string>();
        foreach (var name in Names)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>Reads an embedded schema as text.</summary>
    /// <exception cref="FileNotFoundException">No such embedded resource.</exception>
    public static string ReadText(string name)
    {
        using var stream = Open(name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static byte[] ReadBytes(string name)
    {
        using var stream = Open(name);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static Stream Open(string name)
    {
        foreach (var raw in Assembly.GetManifestResourceNames())
        {
            if (Normalize(raw) == name)
            {
                return Assembly.GetManifestResourceStream(raw)
                    ?? throw new FileNotFoundException($"Embedded schema '{name}' could not be opened.", name);
            }
        }

        throw new FileNotFoundException($"Embedded schema '{name}' does not exist.", name);
    }

    private static string Normalize(string resourceName)
    {
        return resourceName.Replace('\\', '/');
    }
}
