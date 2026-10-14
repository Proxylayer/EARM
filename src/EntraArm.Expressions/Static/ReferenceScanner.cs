using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using EntraArm.Expressions.Evaluation;
using EntraArm.Expressions.Functions;
using EntraArm.Expressions.Syntax;
using EntraArm.Expressions.Values;

namespace EntraArm.Expressions.Static;

public enum ReferenceOccurrenceKind
{
    /// <summary><c>reference(nameOrId)</c>, or <c>appRole(name, ...)</c>/<c>permissionScope(name, ...)</c> naming a template application or service principal: a same-template resource (implicit dependency).</summary>
    Reference,

    /// <summary><c>reference(id, apiVersion)</c>: an existing resource outside the template (no dependency).</summary>
    ExistingReference,

    /// <summary><c>resourceId(...)</c> naming a template resource, not wrapped in <c>reference()</c>.</summary>
    ResourceId,

    /// <summary>
    /// A <c>reference()</c> whose target depends on <c>copyIndex()</c> (the engine resolves it per iteration) or,
    /// offline only, solely on a parameter that has no value yet (<c>ReferenceDependsOnMissingParameter</c>).
    /// </summary>
    Deferred,
}

/// <summary>One <c>reference()</c>/<c>resourceId()</c>/<c>appRole()</c>/<c>permissionScope()</c> call found in a JSON tree, with its statically resolved target.</summary>
public sealed record ReferenceOccurrence(ReferenceOccurrenceKind Kind, ResourceIdentifier? Target, JsonPath Path, int Offset, string ExpressionText)
{
    public bool IsSameTemplate => Kind is ReferenceOccurrenceKind.Reference or ReferenceOccurrenceKind.ResourceId;
}

public sealed record ReferenceScanResult(IReadOnlyList<ReferenceOccurrence> Occurrences, IReadOnlyList<Diagnostic> Diagnostics)
{
    public static ReferenceScanResult Empty { get; } = new([], []);

    /// <summary>Distinct same-template targets (<see cref="ReferenceOccurrenceKind.Reference"/> and <see cref="ReferenceOccurrenceKind.ResourceId"/>).</summary>
    public IReadOnlySet<ResourceIdentifier> SameTemplateIds => Occurrences.Where(o => o.IsSameTemplate && o.Target is not null).Select(o => o.Target!).ToHashSet();

    /// <summary>Distinct targets of same-template <c>reference()</c> calls only (the ARM implicit-dependency rule).</summary>
    public IReadOnlySet<ResourceIdentifier> ReferencedIds => Occurrences.Where(o => o.Kind == ReferenceOccurrenceKind.Reference && o.Target is not null).Select(o => o.Target!).ToHashSet();
}

/// <summary>
/// Static reference extraction (plan section 2.3): walks a JSON tree, parses every expression, and evaluates each
/// <c>reference()</c> / <c>resourceId()</c> call in the static context, plus every <c>appRole()</c> /
/// <c>permissionScope()</c> whose first argument names a template resource (plan section 1.5, phase 4). A
/// <c>reference()</c> whose target cannot be computed from literals, parameters and variables is reported as
/// <c>ReferenceNotStaticallyResolvable</c> (copy-dependent targets are deferred instead).
/// <para>
/// Calls inside a lambda body are evaluated with the lambda's parameters bound to unknown placeholders
/// (<c>lambdaVariables('x')</c>, origin <see cref="UnknownOrigins.Reference"/>): a target that does not depend on
/// the element resolves as usual, one that does is not statically resolvable.
/// </para>
/// </summary>
public static class ReferenceScanner
{
    public static ReferenceScanResult Scan(ExpressionEvaluator evaluator, JsonNode? node, StaticContext context, EvaluationContext evaluationContext, JsonPath basePath)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evaluationContext);
        ArgumentNullException.ThrowIfNull(basePath);

        var occurrences = new List<ReferenceOccurrence>();
        var diagnostics = new List<Diagnostic>();
        Walk(evaluator, node, context, evaluationContext, basePath, occurrences, diagnostics);
        return new ReferenceScanResult(occurrences, diagnostics);
    }

    private static void Walk(ExpressionEvaluator evaluator, JsonNode? node, StaticContext context, EvaluationContext evaluationContext, JsonPath path, List<ReferenceOccurrence> occurrences, List<Diagnostic> diagnostics)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    Walk(evaluator, property.Value, context, evaluationContext, path.Append(property.Key), occurrences, diagnostics);
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    Walk(evaluator, array[i], context, evaluationContext, path.Append(i), occurrences, diagnostics);
                }

                break;
            default:
                {
                    string? text = TemplateJson.AsString(node);
                    if (text is null || !ExpressionSyntax.IsExpression(text))
                    {
                        return;
                    }

                    ScanExpression(evaluator, text, context, evaluationContext, path, occurrences, diagnostics);
                    break;
                }
        }
    }

    /// <summary>Scans one expression string.</summary>
    public static void ScanExpression(ExpressionEvaluator evaluator, string text, StaticContext context, EvaluationContext evaluationContext, JsonPath path, List<ReferenceOccurrence> occurrences, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evaluationContext);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Expression root;
        try
        {
            root = ExpressionParser.Parse(text);
        }
        catch (ExpressionParseException ex)
        {
            diagnostics.Add(Diagnostic.Error(ex.Code, ex.MessageWithOffset, path, context.Document.LocationOf(path), context.Document.SourcePath));
            return;
        }

        var scan = new ExpressionScan(evaluator, context, evaluationContext, path, occurrences, diagnostics);
        scan.Visit(root, evaluationContext.LambdaScope);
    }

    private sealed class ExpressionScan(ExpressionEvaluator evaluator, StaticContext context, EvaluationContext evaluationContext, JsonPath path, List<ReferenceOccurrence> occurrences, List<Diagnostic> diagnostics)
    {
        public void Visit(Expression node, ImmutableDictionary<string, JsonNode?> lambdaScope)
        {
            if (node is LambdaExpression lambda)
            {
                var inner = lambdaScope;
                foreach (var parameter in lambda.Parameters)
                {
                    inner = inner.SetItem(parameter, UnknownJson.Create($"lambdaVariables({ExpressionSyntax.QuoteLiteral(parameter)})", UnknownOrigins.Reference));
                }

                Visit(lambda.Body, inner);
                return;
            }

            if (node is CallExpression call && !call.IsUserFunction)
            {
                var scoped = ReferenceEquals(lambdaScope, evaluationContext.LambdaScope) ? evaluationContext : evaluationContext with { LambdaScope = lambdaScope };
                if (call.Is("reference"))
                {
                    ScanReference(call, scoped);
                }
                else if (call.Is("resourceId"))
                {
                    ScanResourceId(call, scoped);
                }
                else if (call.Is("appRole") || call.Is("permissionScope"))
                {
                    ScanEntraReference(call, scoped);
                }
            }

            foreach (var child in node.Children)
            {
                Visit(child, lambdaScope);
            }
        }

        private void ScanReference(CallExpression call, EvaluationContext scoped)
        {
            string callText = call.ToExpressionText();
            bool existing = call.Arguments.Count >= 2;
            JsonNode? result;
            try
            {
                result = evaluator.EvaluateCall(call, scoped);
            }
            catch (ExpressionEvaluationException ex)
            {
                Report(Diagnostic.Error(ex.Code, ex.MessageWithOffset, path, context.Document.LocationOf(path), context.Document.SourcePath));
                return;
            }

            if (UnknownJson.IsUnknown(result, out var unknown) && unknown.Source is not null)
            {
                occurrences.Add(new ReferenceOccurrence(existing ? ReferenceOccurrenceKind.ExistingReference : ReferenceOccurrenceKind.Reference, unknown.Source, path, call.Offset, callText));
                return;
            }

            if (unknown is null)
            {
                // A live object came back (a resolver other than the static one): the target was resolvable.
                return;
            }

            if ((unknown.Origins & UnknownOrigins.Copy) != 0)
            {
                occurrences.Add(new ReferenceOccurrence(ReferenceOccurrenceKind.Deferred, null, path, call.Offset, callText));
                return;
            }

            if (ArgumentsDependOnlyOnParameters(call, scoped))
            {
                // The target depends only on a parameter that has no value offline (not supplied, or a pending Key Vault
                // reference): an artefact of validating without values, not an authoring error. Deferred like a
                // copy-dependent target; the engine never sees this state because deployment requires every value.
                occurrences.Add(new ReferenceOccurrence(ReferenceOccurrenceKind.Deferred, null, path, call.Offset, callText));
                Report(Diagnostic.Info(
                    DiagnosticCodes.ReferenceDependsOnMissingParameter,
                    $"The target of {callText} depends on a parameter that has no value offline; it is resolved once the value is supplied. Deploy-time value: {unknown.Symbol}. (at offset {call.Offset})",
                    path,
                    context.Document.LocationOf(path),
                    context.Document.SourcePath));
                return;
            }

            Report(Diagnostic.Error(
                DiagnosticCodes.ReferenceNotStaticallyResolvable,
                $"The target of {callText} cannot be determined before deployment: a reference() argument must be computable from literals, parameters and variables (plus references to resources whose name is computable). Offending value: {unknown.Symbol}. (at offset {call.Offset})",
                path,
                context.Document.LocationOf(path),
                context.Document.SourcePath));
        }

        /// <summary>
        /// True when the arguments of <paramref name="call"/> contain unknown leaves and every one of them has
        /// <see cref="UnknownOrigins.Parameter"/> as its only origin. The <c>Reference</c> origin that <c>reference()</c>
        /// itself adds to its result is deliberately not consulted: a target computed from a missing parameter is an
        /// offline artefact, a target computed from another <c>reference()</c> is a genuine static-resolution failure.
        /// </summary>
        private bool ArgumentsDependOnlyOnParameters(CallExpression call, EvaluationContext scoped)
        {
            var origins = UnknownOrigins.None;
            foreach (var argument in call.Arguments)
            {
                JsonNode? value;
                try
                {
                    value = evaluator.Evaluate(argument, scoped);
                }
                catch (ExpressionException)
                {
                    return false;
                }

                origins |= UnknownJson.OriginsOf(value);
            }

            return origins == UnknownOrigins.Parameter;
        }

        private void ScanResourceId(CallExpression call, EvaluationContext scoped)
        {
            JsonNode? result;
            try
            {
                result = evaluator.EvaluateCall(call, scoped);
            }
            catch (ExpressionEvaluationException)
            {
                // Reported by the validator's expression walk / by the enclosing reference() scan.
                return;
            }

            ResourceIdentifier? id = null;
            if (UnknownJson.IsUnknown(result, out var unknown))
            {
                id = unknown.ResourceId;
            }
            else if (JsonValues.TryGetString(result, out var text))
            {
                context.Context.Catalog.TryParseResourceId(text, out id);
            }

            if (id is not null && context.Index.Contains(id))
            {
                occurrences.Add(new ReferenceOccurrence(ReferenceOccurrenceKind.ResourceId, id, path, call.Offset, call.ToExpressionText()));
            }
        }

        /// <summary>
        /// <c>appRole(ref, value)</c> / <c>permissionScope(ref, value)</c>: when <c>ref</c> evaluates to a string that
        /// is not an appId, the call is evaluated in full (so a name that is neither a GUID nor a template
        /// application/service principal is reported) and a template target becomes an implicit dependency. A GUID
        /// carries no dependency and is not evaluated here, so a live scan never triggers a tenant lookup; an unknown
        /// <c>ref</c> (<c>reference('app').appId</c>) is covered by the nested <c>reference()</c> scan.
        /// </summary>
        private void ScanEntraReference(CallExpression call, EvaluationContext scoped)
        {
            if (call.Arguments.Count < 2)
            {
                return;
            }

            JsonNode? target;
            try
            {
                target = evaluator.Evaluate(call.Arguments[0], scoped);
            }
            catch (ExpressionException)
            {
                return;
            }

            if (!JsonValues.TryGetString(target, out var text) || Guid.TryParse(text, out _))
            {
                return;
            }

            JsonNode? result;
            try
            {
                result = evaluator.EvaluateCall(call, scoped);
            }
            catch (ExpressionEvaluationException ex)
            {
                Report(Diagnostic.Error(ex.Code, ex.MessageWithOffset, path, context.Document.LocationOf(path), context.Document.SourcePath));
                return;
            }

            ResourceIdentifier? id = null;
            if (UnknownJson.IsUnknown(result, out var unknown) && unknown.Source is not null)
            {
                id = unknown.Source;
            }
            else
            {
                // A live resolver answered: resolve the name on its own so the dependency is still recorded.
                try
                {
                    EntraFunctions.TryResolveTemplateResource(scoped, text, out id, out _);
                }
                catch (ExpressionException)
                {
                    id = null;
                }
            }

            if (id is not null && context.Index.Contains(id))
            {
                occurrences.Add(new ReferenceOccurrence(ReferenceOccurrenceKind.Reference, id, path, call.Offset, call.ToExpressionText()));
            }
        }

        private void Report(Diagnostic diagnostic)
        {
            diagnostics.Add(diagnostic);
        }
    }
}
