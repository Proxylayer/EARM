using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Engine.Planning.Nested;

/// <summary>
/// The <c>properties</c> of one nested deployment instance, evaluated in the parent template (with the deployment's copy iteration): the
/// template (inline object, or the location of a linked file), the parameters to pass (inline expressions, or a parameter file) and the
/// evaluation scope. The values that decide what is expanded must be known before deployment (<see cref="EngineDiagnosticCodes.NestedDeploymentNotStatic"/>).
/// The literal shape was checked offline (NestedDeploymentChecker); what is read here is what expressions computed.
/// </summary>
internal sealed record DeploymentSettings
{
    public required NestedTemplateScope Scope { get; init; }

    /// <summary>The inline template (shared with the parent template's tree; read-only), or <c>null</c> for a linked template.</summary>
    public JsonObject? InlineTemplate { get; init; }

    /// <summary>The linked template, or <c>null</c> for an inline one.</summary>
    public LinkedLocation? TemplateLocation { get; init; }

    /// <summary><c>templateLink.contentVersion</c>: the linked template's <c>contentVersion</c> must equal it.</summary>
    public string? TemplateContentVersion { get; init; }

    /// <summary>The entries of <c>properties.parameters</c>, unevaluated (each <c>{ "value": ... }</c>).</summary>
    public JsonObject? Parameters { get; init; }

    /// <summary>The parameter file of <c>parametersLink</c>.</summary>
    public LinkedLocation? ParametersLocation { get; init; }

    /// <summary><c>parametersLink.contentVersion</c>: the parameter file's <c>contentVersion</c> must equal it.</summary>
    public string? ParametersContentVersion { get; init; }

    /// <summary>Reads the settings of <paramref name="deployment"/>; problems are reported to <paramref name="diagnostics"/> and yield <c>null</c>.</summary>
    public static DeploymentSettings? Read(ResourceInstance deployment, TemplateScope parent, ExpressionEvaluator evaluator, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new Reader(deployment, parent, evaluator, diagnostics).Read();
    }

    private sealed class Reader
    {
        private readonly ResourceInstance _deployment;
        private readonly TemplateScope _parent;
        private readonly ExpressionEvaluator _evaluator;
        private readonly List<Diagnostic> _diagnostics;
        private readonly JsonPath _path;
        private readonly EvaluationContext _context;
        private bool _failed;

        public Reader(ResourceInstance deployment, TemplateScope parent, ExpressionEvaluator evaluator, List<Diagnostic> diagnostics)
        {
            _deployment = deployment;
            _parent = parent;
            _evaluator = evaluator;
            _diagnostics = diagnostics;
            _path = deployment.Declaration.Path.Append("properties");
            _context = deployment.Context.ForScope(EvaluationScope.Resource);
        }

        public DeploymentSettings? Read()
        {
            // The declaration's properties without the inline template (ResourceReader); the template itself is read from the source tree.
            if (_deployment.Declaration.Properties is not JsonObject properties || _deployment.Declaration.Source["properties"] is not JsonObject source)
            {
                return null;
            }

            string? mode = String(properties["mode"], _path.Append("mode"));
            if (mode is not null && !string.Equals(mode, "Incremental", StringComparison.OrdinalIgnoreCase))
            {
                Error(DiagnosticCodes.InvalidNestedDeployment, $"'mode' evaluates to '{mode}'; a nested or linked deployment can only use Incremental (ARM rule).", _path.Append("mode"));
            }

            var scope = DeploymentResourceType.ScopeOf(source, _parent.Document.UsesSymbolicNames);
            JsonObject? inline = source["template"] as JsonObject;
            LinkedLocation? templateLocation = null;
            string? templateVersion = null;
            if (properties["templateLink"] is JsonObject link)
            {
                var linkPath = _path.Append("templateLink");
                string? relative = String(link["relativePath"], linkPath.Append("relativePath"));
                string? uri = String(link["uri"], linkPath.Append("uri"));
                string? query = String(link["queryString"], linkPath.Append("queryString"));
                templateVersion = String(link["contentVersion"], linkPath.Append("contentVersion"));
                templateLocation = Locate(relative, uri, query, linkPath);
            }

            LinkedLocation? parametersLocation = null;
            string? parametersVersion = null;
            if (properties["parametersLink"] is JsonObject parametersLink)
            {
                var linkPath = _path.Append("parametersLink");
                string? uri = String(parametersLink["uri"], linkPath.Append("uri"));
                parametersVersion = String(parametersLink["contentVersion"], linkPath.Append("contentVersion"));
                if (uri is not null)
                {
                    // ARM takes an absolute URL; EARM also accepts a path relative to the declaring template (a local parameter file next to it).
                    parametersLocation = LinkedTemplateReader.IsUrl(uri) ? Locate(null, uri, null, linkPath) : Locate(uri, null, null, linkPath);
                }
            }

            if (_failed || (inline is null && templateLocation is null))
            {
                return null;
            }

            return new DeploymentSettings
            {
                Scope = scope,
                InlineTemplate = templateLocation is null ? inline : null,
                TemplateLocation = templateLocation,
                TemplateContentVersion = templateVersion,
                Parameters = properties["parameters"] as JsonObject,
                ParametersLocation = parametersLocation,
                ParametersContentVersion = parametersVersion,
            };
        }

        private LinkedLocation? Locate(string? relative, string? uri, string? query, JsonPath path)
        {
            try
            {
                if (relative is not null)
                {
                    return LinkedTemplateReader.ResolveRelative(relative, _parent.Location, query);
                }

                if (uri is not null)
                {
                    return LinkedTemplateReader.ResolveUri(uri, query);
                }
            }
            catch (ArgumentException ex)
            {
                Error(EngineDiagnosticCodes.LinkedTemplateUnavailable, ex.Message, path);
            }

            return null;
        }

        /// <summary>The value of <paramref name="node"/> as a string known before deployment; <c>null</c> when absent (or reported).</summary>
        private string? String(JsonNode? node, JsonPath path)
        {
            if (node is null)
            {
                return null;
            }

            JsonNode? value;
            try
            {
                value = _evaluator.EvaluateTree(node, _context);
            }
            catch (ExpressionException ex)
            {
                Error(ex.Code, ex.MessageWithOffset, path);
                return null;
            }

            if (UnknownJson.IsUnknown(value, out var unknown))
            {
                Error(EngineDiagnosticCodes.NestedDeploymentNotStatic, $"'{path.Last}' of nested deployment '{_deployment.Id.Name}' depends on a value only known at deployment ({unknown.Symbol}); it must be computable from parameters and variables.", path);
                return null;
            }

            if (Core.Secrets.SecretJson.IsSecret(value))
            {
                // A SAS token in a secure parameter is the common case: the value is used, never shown.
                value = Core.Secrets.SecretJson.Reveal(value);
            }

            if (!JsonValues.TryGetString(value, out var text))
            {
                Error(DiagnosticCodes.InvalidNestedDeployment, $"'{path.Last}' must be a string, got {JsonValues.Describe(value)}.", path);
                return null;
            }

            return text;
        }

        private void Error(string code, string message, JsonPath path)
        {
            _failed = true;
            _diagnostics.Add(_parent.Error(code, message, path));
        }
    }
}
