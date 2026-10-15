using System.Text.Json;

namespace EArm.Cli.Output;

/// <summary>
/// Serializer settings of the JSON output (<c>-o json</c>). A value of the template may nest 64 levels (<see cref="EArm.Core.Json.ValueGuard.MaxDepth"/>);
/// the report wraps it in several more (resource, change, properties), and the serializer's default depth limit of 64 would turn such a value
/// into an exception instead of output.
/// </summary>
internal static class JsonOutput
{
    /// <summary>The writer depth the JSON output allows: the deepest value plus generous room for the structure around it.</summary>
    public const int MaxDepth = 256;

    public static JsonSerializerOptions Options(bool indented)
    {
        return new JsonSerializerOptions { WriteIndented = indented, MaxDepth = MaxDepth };
    }
}
