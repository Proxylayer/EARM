using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EArm.Integration.Tests;

/// <summary>
/// Derives template variants from the files under <c>Templates/</c>: a Complete-mode run needs the same template
/// without some resources (a <c>condition: false</c> resource still counts as declared, so it cannot be used to drop one).
/// </summary>
internal static class TemplateEditor
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static JsonObject Load(string templateFile)
    {
        return JsonNode.Parse(File.ReadAllText(templateFile)) as JsonObject
            ?? throw new InvalidOperationException($"{templateFile} is not a JSON object.");
    }

    /// <summary>Keeps only the resources (top level and nested) for which <paramref name="keep"/> is true, and drops every output (outputs may reference removed resources).</summary>
    public static JsonObject Retain(JsonObject template, Func<JsonObject, bool> keep)
    {
        Prune(template, keep);
        template["outputs"] = new JsonObject();
        return template;
    }

    /// <summary>The template without any resource.</summary>
    public static JsonObject Empty(JsonObject template)
    {
        return Retain(template, static _ => false);
    }

    /// <summary>The <c>type</c> of a resource declaration (nested children use the short form, for example <c>passwordCredentials</c>).</summary>
    public static string TypeOf(JsonObject resource)
    {
        return (string?)resource["type"] ?? string.Empty;
    }

    public static bool IsType(JsonObject resource, string type)
    {
        return TypeOf(resource).Equals(type, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes the template to <paramref name="directory"/> and returns the path.</summary>
    public static string Write(string directory, string fileName, JsonObject template)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, template.ToJsonString(Indented), new UTF8Encoding(false));
        return path;
    }

    private static void Prune(JsonObject owner, Func<JsonObject, bool> keep)
    {
        if (owner["resources"] is not JsonArray resources)
        {
            return;
        }

        foreach (var resource in resources.OfType<JsonObject>().ToList())
        {
            if (!keep(resource))
            {
                resources.Remove(resource);
            }
            else
            {
                Prune(resource, keep);
            }
        }
    }
}
