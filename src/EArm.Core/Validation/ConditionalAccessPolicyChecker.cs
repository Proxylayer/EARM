using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The offline check of Conditional Access policy declarations (<c>Microsoft.Entra/conditionalAccess/policies</c>) for the authoring mistake Graph
/// refuses with <c>400 1012: 'applications' condition cannot include both 'includeApplications' and
/// 'includeUserActions'/'includeAuthenticationContextClassReferences'</c>: a <c>conditions.applications</c> that declares a non-empty
/// <c>includeApplications</c> together with a non-empty <c>includeUserActions</c> or <c>includeAuthenticationContextClassReferences</c>
/// (<see cref="DiagnosticCodes.ConflictingApplicationIncludes"/>). Arrays computed whole by an expression are left to the provider's plan-time check.
/// </summary>
internal sealed class ConditionalAccessPolicyChecker
{
    internal const string PoliciesType = "Microsoft.Entra/conditionalAccess/policies";

    private static readonly string[] ExclusiveIncludes = ["includeUserActions", "includeAuthenticationContextClassReferences"];

    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public ConditionalAccessPolicyChecker(TemplateDocument document, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _document = document;
        _diagnostics = diagnostics;
    }

    public void Run()
    {
        foreach (var resource in _document.Resources)
        {
            if (resource.Type is null || !ResourceIdentifier.TypeEquals(resource.Type, PoliciesType) || resource.Properties is not JsonObject properties
                || Property(properties, "conditions") is not (string conditionsKey, JsonObject conditions)
                || Property(conditions, "applications") is not (string applicationsKey, JsonObject applications))
            {
                continue;
            }

            if (Property(applications, "includeApplications") is not (string appsKey, var apps) || !HasEntries(apps))
            {
                continue;
            }

            foreach (string include in ExclusiveIncludes)
            {
                if (Property(applications, include) is (string key, var value) && HasEntries(value))
                {
                    var path = resource.Path.Append("properties").Append(conditionsKey).Append(applicationsKey).Append(key);
                    Error($"The {resource.Describe()} declares both '{appsKey}' and '{key}' in conditions.applications; Graph refuses the policy (1012: 'applications' condition cannot include both 'includeApplications' and 'includeUserActions'/'includeAuthenticationContextClassReferences'). Target applications, user actions or authentication contexts: remove one of them.", path);
                    break;
                }
            }
        }
    }

    /// <summary>True for an array holding at least one entry that is not a blank literal string (expression entries count).</summary>
    private static bool HasEntries(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return false;
        }

        foreach (var item in array)
        {
            if (item is null)
            {
                continue;
            }

            if (TemplateJson.IsExpression(item) || TemplateJson.LiteralString(item) is not { } text || !string.IsNullOrWhiteSpace(text))
            {
                return true;
            }
        }

        return false;
    }

    private static (string Key, JsonNode? Value)? Property(JsonObject properties, string name)
    {
        foreach (var (key, value) in properties)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return (key, value);
            }
        }

        return null;
    }

    private void Error(string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(DiagnosticCodes.ConflictingApplicationIncludes, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
