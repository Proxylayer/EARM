using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Engine.Planning.Nested;

/// <summary>
/// Binds the values an inner-scope template receives from the deployment's <c>properties.parameters</c>
/// (https://learn.microsoft.com/azure/azure-resource-manager/templates/linked-templates#parameters-for-linked-template):
/// <list type="bullet">
/// <item><c>{ "value": ... }</c>: the parent's expression evaluated now; a value that depends on a resource the parent deploys is a deploy-time
/// placeholder here and is evaluated again when the template's resources are planned and applied (<see cref="NestedParameters"/>).</item>
/// <item><c>{ "reference": { "keyVault": { "id" }, "secretName", "secretVersion" } }</c>: a Key Vault secret, as ARM allows for the parameters of a
/// nested deployment (https://learn.microsoft.com/azure/azure-resource-manager/templates/key-vault-parameter#reference-secrets-with-dynamic-id). The
/// vault id, secret name and version may be expressions of the parent (a dynamic id); they are evaluated now and the parameter is bound to the
/// pending reference, resolved later (<see cref="NestedKeyVaultSecrets"/>). Offline, coordinates that are not known yet bind a placeholder reference
/// (the value stays unknown); otherwise they must be known before deployment (<see cref="EngineDiagnosticCodes.NestedDeploymentNotStatic"/>).</item>
/// </list>
/// </summary>
internal sealed class NestedParameterBinder
{
    /// <summary>The vault and secret name of a reference whose coordinates are not known offline.</summary>
    internal const string NotKnownOffline = "(known at deployment)";

    private readonly ExpressionEvaluator _evaluator;
    private readonly bool _offline;

    public NestedParameterBinder(ExpressionEvaluator evaluator, bool offline)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
        _offline = offline;
    }

    /// <summary>
    /// Returns <paramref name="document"/> with the values of <paramref name="settings"/>' parameters bound; <paramref name="passed"/> receives the
    /// unevaluated <c>value</c> of each entry (re-evaluated by <see cref="NestedParameters"/>). Problems are reported to <paramref name="diagnostics"/>.
    /// </summary>
    public TemplateDocument Bind(ResourceInstance deployment, TemplateScope parent, TemplateDocument document, DeploymentSettings settings, ParameterSet fileParameters, Dictionary<string, JsonNode?> passed, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(passed);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (settings.Parameters is not { } map)
        {
            return document;
        }

        var bound = new Dictionary<string, BoundParameter>(document.ParameterValues, StringComparer.OrdinalIgnoreCase);
        var parametersPath = deployment.Declaration.Path.Append("properties").Append("parameters");
        var context = deployment.Context.ForScope(EvaluationScope.Resource);
        foreach (var (name, entry) in map)
        {
            var entryPath = parametersPath.Append(name);
            if (!document.Parameters.TryGetValue(name, out var definition))
            {
                diagnostics.Add(parent.Error(DiagnosticCodes.UnknownParameter, $"Nested deployment '{deployment.Id.Name}' passes a value for '{name}', but its template declares no such parameter.", entryPath));
                continue;
            }

            if (entry is JsonObject { } entryObject && entryObject["reference"] is JsonObject reference)
            {
                bound[definition.Name] = ReadReference(deployment, parent, name, reference, context, entryPath.Append("reference"), diagnostics) is { } pending
                    ? new BoundParameter { Name = definition.Name, Definition = definition, PendingReference = pending, Source = ParameterValueSource.File }
                    : new BoundParameter
                    {
                        // The reference is reported; the parameter is passed (not "missing"), with a value nobody can know.
                        Name = definition.Name,
                        Definition = definition,
                        Value = UnknownJson.Create($"parameters('{definition.Name}')", UnknownOrigins.Parameter, tainted: true),
                        Source = ParameterValueSource.File,
                        IsDeployTimeValue = true,
                    };
                continue;
            }

            var node = (entry as JsonObject)?["value"];
            JsonNode? value;
            try
            {
                value = _evaluator.EvaluateTree(node, context);
            }
            catch (ExpressionException ex)
            {
                diagnostics.Add(parent.Error(ex.Code, ex.MessageWithOffset, entryPath.Append("value")));
                continue;
            }

            passed[definition.Name] = node;
            bound[definition.Name] = new BoundParameter
            {
                Name = definition.Name,
                Definition = definition,
                Value = definition.IsSecure ? SecretJson.Taint(value) : value,
                Source = ParameterValueSource.File,
                IsDeployTimeValue = UnknownJson.ContainsUnknown(value),
            };
        }

        return document with { ParameterValues = bound, SuppliedParameters = fileParameters };
    }

    /// <summary>The Key Vault reference of one entry, its coordinates evaluated in the parent; <c>null</c> when it cannot be built (reported).</summary>
    private KeyVaultReference? ReadReference(ResourceInstance deployment, TemplateScope parent, string name, JsonObject reference, EvaluationContext context, JsonPath path, List<Diagnostic> diagnostics)
    {
        bool unknown = false;
        bool failed = false;
        string? Text(JsonNode? node, JsonPath at)
        {
            if (node is null)
            {
                return null;
            }

            JsonNode? value;
            try
            {
                value = _evaluator.EvaluateTree(node, context);
            }
            catch (ExpressionException ex)
            {
                failed = true;
                diagnostics.Add(parent.Error(ex.Code, ex.MessageWithOffset, at));
                return null;
            }

            if (UnknownJson.IsUnknown(value, out var placeholder))
            {
                if (!_offline)
                {
                    failed = true;
                    diagnostics.Add(parent.Error(EngineDiagnosticCodes.NestedDeploymentNotStatic, $"The Key Vault reference of parameter '{name}' of nested deployment '{deployment.Id.Name}' depends on a value only known at deployment ({placeholder.Symbol}); its vault, secret name and version must be computable from parameters and variables.", at));
                }

                unknown = true;
                return null;
            }

            if (!JsonValues.TryGetString(SecretJson.Reveal(value), out var text))
            {
                failed = true;
                diagnostics.Add(parent.Error(DiagnosticCodes.InvalidNestedDeployment, $"Parameter '{name}': '{at.Last}' of a Key Vault reference must be a string, got {JsonValues.Describe(value)}.", at));
                return null;
            }

            return text;
        }

        string? vault = Text((reference["keyVault"] as JsonObject)?["id"], path.Append("keyVault").Append("id"));
        string? secret = Text(reference["secretName"], path.Append("secretName"));
        string? version = Text(reference["secretVersion"], path.Append("secretVersion"));
        if (failed)
        {
            return null;
        }

        if (unknown)
        {
            // Offline only: the secret is needed, but where it is depends on a value that is not supplied yet; the parameter stays unknown (tainted).
            return KeyVaultReference.Create(vault ?? NotKnownOffline, secret ?? NotKnownOffline, null);
        }

        if (string.IsNullOrWhiteSpace(vault) || string.IsNullOrWhiteSpace(secret))
        {
            diagnostics.Add(parent.Error(DiagnosticCodes.InvalidNestedDeployment, $"Parameter '{name}': a Key Vault reference needs a non-empty 'keyVault.id' and 'secretName'.", path));
            return null;
        }

        return KeyVaultReference.Create(vault, secret, version);
    }
}
