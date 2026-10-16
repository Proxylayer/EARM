using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The offline check of <c>Microsoft.Entra/authenticationMethodConfigurations</c> declarations
/// (<see cref="DiagnosticCodes.InvalidAuthenticationMethodConfiguration"/>): the cross-field rules the JSON Schema cannot express, run on the literal
/// values of <see cref="AuthenticationMethodConfigurationRules.Check"/>: an <c>@odata.type</c> or <c>id</c> that names another method, a target or
/// passkey profile id listed twice, an enforced FIDO2 allow-list without AAGUIDs, Temporary Access Pass lifetimes out of order (minimum &lt;= default
/// &lt;= maximum). The shape findings (enums, types, bounds, the AAGUID pattern) are the schema's (<see cref="DiagnosticCodes.SchemaViolation"/>). Values computed by
/// expressions are skipped here and checked by the provider at plan time, once they are evaluated. An unsupported name is the name check's
/// (<see cref="DiagnosticCodes.InvalidResourceName"/>, the type's <c>namePattern</c>).
/// </summary>
internal sealed class AuthenticationMethodConfigurationChecker
{
    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public AuthenticationMethodConfigurationChecker(TemplateDocument document, List<Diagnostic> diagnostics)
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
            if (resource.Type is null || !ResourceIdentifier.TypeEquals(resource.Type, AuthenticationMethodConfigurationRules.ResourceType)
                || resource.Properties is not JsonObject properties || !resource.HasLiteralName
                || AuthenticationMethodConfigurationRules.Find(resource.Name) is not { } method)
            {
                continue;
            }

            var propertiesPath = resource.Path.Append("properties");
            foreach (var finding in AuthenticationMethodConfigurationRules.Check(method, properties, TemplateJson.IsExpression))
            {
                if (finding.SchemaCovered)
                {
                    // The type's JSON Schema reports it (SchemaViolation) at the same path.
                    continue;
                }

                var path = propertiesPath.Append(finding.Path);
                _diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.InvalidAuthenticationMethodConfiguration,
                    $"The {resource.Describe()}: {finding.Message}.",
                    path,
                    _document.LocationOf(path),
                    _document.SourcePath));
            }
        }
    }
}
