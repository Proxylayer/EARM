using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using JsonSchema = global::Json.Schema.JsonSchema;

namespace EArm.Core.Validation;

/// <summary>
/// Default <see cref="ITemplateValidator"/>: loader diagnostics, then the semantic checks of
/// <see cref="SemanticChecker"/>, then JSON Schema findings for the template (and the parameter file when one was
/// supplied). Schema findings are de-duplicated against semantic ones: a schema finding is dropped when a
/// semantic diagnostic already points at the same path, and the <c>required</c> findings of the envelope are dropped
/// because the semantic pass reports every required key the envelope schemas declare with a specific code. A
/// <c>required</c> finding inside a resource's <c>properties</c> (the per-type schemas: a <c>requiredResourceAccess</c>
/// entry without <c>resourceAccess</c>, a <c>directoryRoles</c> entry without a role) has no semantic counterpart and is reported.
/// </summary>
/// <remarks>
/// Checks that need expression evaluation are deliberately NOT here and belong to the expression validators:
/// non-literal resource names, tainted values flowing into plain outputs, <c>dependsOn</c> /
/// <c>reference()</c> target resolution, duplicate ids after <c>copy</c> expansion, <c>memberOf</c> pointing at a
/// dynamic group, and conflicting edge extras.
/// </remarks>
public sealed class TemplateValidator : ITemplateValidator
{
    private readonly ResourceTypeCatalog _catalog;
    private readonly TemplateSchemas _schemas;

    public TemplateValidator()
        : this(ResourceTypeCatalog.Default)
    {
    }

    public TemplateValidator(ResourceTypeCatalog catalog)
        : this(catalog, TemplateSchemas.Default)
    {
    }

    internal TemplateValidator(ResourceTypeCatalog catalog, TemplateSchemas schemas)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(schemas);
        _catalog = catalog;
        _schemas = schemas;
    }

    public IReadOnlyList<Diagnostic> Validate(TemplateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new List<Diagnostic>(document.LoadDiagnostics);
        new SemanticChecker(_catalog, document, diagnostics).Run();
        new FederatedCredentialChecker(document, diagnostics).Run();
        new ConditionalAccessPolicyChecker(document, diagnostics).Run();
        new AuthenticationMethodConfigurationChecker(document, diagnostics).Run();
        new PimScheduleRequestChecker(document, diagnostics, RoleEligibilityScheduleRules.ResourceType, DiagnosticCodes.InvalidRoleEligibilityScheduleRequest, RoleEligibilityScheduleRules.Check).Run();
        new PimScheduleRequestChecker(document, diagnostics, GroupEligibilityScheduleRules.ResourceType, DiagnosticCodes.InvalidGroupEligibilityScheduleRequest, GroupEligibilityScheduleRules.Check).Run();

        var semanticPaths = new HashSet<(string? Source, JsonPath Path)>();
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Path is not null)
            {
                semanticPaths.Add((diagnostic.Source, diagnostic.Path));
            }
        }

        var templateFindings = EvaluateSchema(_schemas.Template, document.RootElement, document.Root, "template", document.SourcePath, diagnostics);
        foreach (var finding in templateFindings)
        {
            if ((finding.Keyword == "required" && !IsInsideResourceProperties(finding.Path)) || semanticPaths.Contains((document.SourcePath, finding.Path)))
            {
                continue;
            }

            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.SchemaViolation,
                FormatSchemaMessage(finding),
                finding.Path,
                document.LocationOf(finding.Path),
                document.SourcePath));
        }

        if (document.SuppliedParameters.RootElement is { } parametersRoot)
        {
            string? parameterSource = document.SuppliedParameters.SourcePath;
            foreach (var finding in EvaluateSchema(_schemas.Parameters, parametersRoot, null, "parameter file", parameterSource, diagnostics))
            {
                if (finding.Keyword == "required" || semanticPaths.Contains((parameterSource, finding.Path)))
                {
                    continue;
                }

                diagnostics.Add(Diagnostic.Error(DiagnosticCodes.SchemaViolation, FormatSchemaMessage(finding), finding.Path, null, parameterSource));
            }
        }

        diagnostics.Sort(DiagnosticOrder.Instance);
        return diagnostics;
    }

    /// <summary>
    /// Runs the JSON Schema pass. A document that still holds a string System.Text.Json cannot read (a lone surrogate escape such as
    /// <c>"\ud800"</c>, normally rewritten and reported at load time) makes JsonSchema.Net throw <see cref="InvalidOperationException"/>;
    /// that becomes an <see cref="DiagnosticCodes.InvalidJson"/> error instead of an unhandled exception.
    /// </summary>
    private static IReadOnlyList<SchemaFinding> EvaluateSchema(JsonSchema schema, JsonElement instance, JsonNode? instanceRoot, string what, string? source, List<Diagnostic> diagnostics)
    {
        try
        {
            return TemplateSchemas.Evaluate(schema, instance, instanceRoot);
        }
        catch (InvalidOperationException ex)
        {
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.InvalidJson,
                $"The {what} contains an invalid Unicode escape (lone surrogate) or another string that cannot be read, so it could not be checked against the schema ({ex.Message})",
                JsonPath.Root,
                null,
                source));
            return [];
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException or JsonException or NotSupportedException)
        {
            // A number beyond the decimal range (1e300) on a property with an enum or const keyword makes the schema library throw while it
            // compares values; whatever the library trips over, the document is reported rather than crashing the command.
            diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.SchemaViolation,
                $"The {what} could not be checked against the schema ({ex.GetType().Name}: {SafeText.Escape(ex.Message)}); a number outside the supported range (for example 1e300) or another value the schema cannot compare is the usual cause.",
                JsonPath.Root,
                null,
                source));
            return [];
        }
    }

    /// <summary>True for a path below <c>resources[n].properties</c> or <c>resources[n].resources[m].properties</c> (the members of a resource's properties, at any depth).</summary>
    private static bool IsInsideResourceProperties(JsonPath path)
    {
        var segments = path.Segments;
        int i = 0;
        for (int level = 0; level < 2; level++)
        {
            if (i + 1 < segments.Count && !segments[i].IsIndex && segments[i].Name == "resources" && segments[i + 1].IsIndex)
            {
                i += 2;
            }
            else
            {
                break;
            }
        }

        return i > 0 && i + 1 < segments.Count && !segments[i].IsIndex && segments[i].Name == "properties";
    }

    private static string FormatSchemaMessage(SchemaFinding finding)
    {
        if (finding.Keyword.Length == 0)
        {
            // JsonSchema.Net reports a property rejected by "additionalProperties: false" as a failure against
            // the "false" schema at the property's own location, with no keyword.
            string property = finding.Path.IsRoot ? "value" : $"'{finding.Path.Last}'";
            return $"Property {property} is not allowed here (schema keyword 'additionalProperties').";
        }

        return $"{finding.Message} (schema keyword '{finding.Keyword}')";
    }
}
