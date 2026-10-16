using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The offline check of <c>Microsoft.Entra/roleEligibilityScheduleRequests</c> declarations (<see cref="DiagnosticCodes.InvalidRoleEligibilityScheduleRequest"/>):
/// the rules of <see cref="RoleEligibilityScheduleRules.Check"/> the JSON Schema cannot express, run on the literal values: an unknown or ARM-only property
/// (<c>condition</c>, <c>conditionVersion</c>, <c>target*ScheduleId</c>), a missing <c>principalId</c> or role, <c>role</c> next to <c>roleDefinitionId</c>,
/// more than one scope, a <c>requestType</c> other than <c>AdminAssign</c>, and the schedule rules (expiration type, the endDateTime / duration each type
/// needs, ISO 8601 values, end after start). The shape findings (types, the GUID and scope patterns) are the schema's
/// (<see cref="DiagnosticCodes.SchemaViolation"/>). Values computed by expressions are skipped here and checked by the provider at plan time.
/// </summary>
internal sealed class RoleEligibilityScheduleRequestChecker
{
    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public RoleEligibilityScheduleRequestChecker(TemplateDocument document, List<Diagnostic> diagnostics)
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
            if (resource.Type is null || !ResourceIdentifier.TypeEquals(resource.Type, RoleEligibilityScheduleRules.ResourceType)
                || resource.Properties is not JsonObject properties)
            {
                continue;
            }

            var propertiesPath = resource.Path.Append("properties");
            foreach (var finding in RoleEligibilityScheduleRules.Check(properties, TemplateJson.IsExpression))
            {
                if (finding.SchemaCovered)
                {
                    // The type's JSON Schema reports it (SchemaViolation) at the same path.
                    continue;
                }

                var path = propertiesPath.Append(finding.Path);
                _diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidRoleEligibilityScheduleRequest,
                    $"The {resource.Describe()}: {finding.Message}.",
                    path,
                    _document.LocationOf(path),
                    _document.SourcePath));
            }
        }
    }
}
