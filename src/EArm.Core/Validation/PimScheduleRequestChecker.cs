using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The offline check of one PIM schedule request type: the rules of its <c>Check</c> function (<see cref="RoleEligibilityScheduleRules.Check"/>,
/// <see cref="GroupEligibilityScheduleRules.Check"/>) the JSON Schema cannot express, run on the literal values and reported under the type's code
/// (<see cref="DiagnosticCodes.InvalidRoleEligibilityScheduleRequest"/>, <see cref="DiagnosticCodes.InvalidGroupEligibilityScheduleRequest"/>): unknown
/// properties, missing or exclusive members, a <c>requestType</c> other than <c>AdminAssign</c>, and the schedule rules (expiration type, the endDateTime /
/// duration each type needs, ISO 8601 values, end after start). The shape findings (types, the GUID and scope patterns) are the schema's
/// (<see cref="DiagnosticCodes.SchemaViolation"/>). Values computed by expressions are skipped here and checked by the provider at plan time.
/// </summary>
internal sealed class PimScheduleRequestChecker
{
    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;
    private readonly string _resourceType;
    private readonly string _code;
    private readonly Func<JsonObject, Func<JsonNode?, bool>, IReadOnlyList<PimRequestFinding>> _check;

    public PimScheduleRequestChecker(TemplateDocument document, List<Diagnostic> diagnostics, string resourceType, string code, Func<JsonObject, Func<JsonNode?, bool>, IReadOnlyList<PimRequestFinding>> check)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(check);
        _document = document;
        _diagnostics = diagnostics;
        _resourceType = resourceType;
        _code = code;
        _check = check;
    }

    public void Run()
    {
        foreach (var resource in _document.Resources)
        {
            if (resource.Type is null || !ResourceIdentifier.TypeEquals(resource.Type, _resourceType) || resource.Properties is not JsonObject properties)
            {
                continue;
            }

            var propertiesPath = resource.Path.Append("properties");
            foreach (var finding in _check(properties, TemplateJson.IsExpression))
            {
                if (finding.SchemaCovered)
                {
                    // The type's JSON Schema reports it (SchemaViolation) at the same path.
                    continue;
                }

                var path = propertiesPath.Append(finding.Path);
                _diagnostics.Add(Diagnostic.Error(_code, $"The {resource.Describe()}: {finding.Message}.", path, _document.LocationOf(path), _document.SourcePath));
            }
        }
    }
}
