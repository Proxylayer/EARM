using System;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Loading;

/// <summary>
/// The template's <c>languageVersion</c> (https://learn.microsoft.com/azure/azure-resource-manager/templates/syntax#languageversion-20): absent or
/// <c>1.0</c> is the original language; <c>2.0</c> enables symbolic names, <c>existing</c> resources, <c>definitions</c> and the aggregate type
/// constraints. ARM's experimental versions (<c>1.9-experimental</c>, <c>2.1-experimental</c>, ...) are read as 2.0 with a warning, since EARM
/// implements none of their extra features.
/// </summary>
internal static class LanguageVersions
{
    public const string Version2 = "2.0";

    /// <summary>True when the template uses languageVersion 2.0 (or an experimental successor); reports an unknown or non-string value.</summary>
    public static bool IsVersion2(JsonObject root, LoadContext context)
    {
        if (!root.TryGetPropertyValue("languageVersion", out var node))
        {
            return false;
        }

        var path = JsonPath.Of("languageVersion");
        string? text = TemplateJson.AsString(node);
        switch (text)
        {
            case null:
                context.Error(DiagnosticCodes.InvalidLanguageVersion, "'languageVersion' must be a string: \"2.0\" (or \"1.0\", the default).", path);
                return root["resources"] is JsonObject;
            case "1.0":
                return false;
            case Version2:
                return true;
        }

        if (text.EndsWith("-experimental", StringComparison.OrdinalIgnoreCase) && (text.StartsWith("1.9", StringComparison.Ordinal) || text.StartsWith("2.", StringComparison.Ordinal)))
        {
            context.Warning(DiagnosticCodes.InvalidLanguageVersion, $"languageVersion '{SafeText.Escape(text)}' is experimental in ARM; EARM reads the template as languageVersion 2.0 and supports none of the experimental features.", path);
            return true;
        }

        context.Error(DiagnosticCodes.InvalidLanguageVersion, $"languageVersion '{SafeText.Escape(text)}' is not supported; use \"2.0\" (symbolic names, existing resources, definitions) or omit it.", path);
        return root["resources"] is JsonObject;
    }
}
