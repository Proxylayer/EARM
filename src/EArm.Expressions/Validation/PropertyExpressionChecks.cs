using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Core.Validation;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;
using EArm.Expressions.Values;

namespace EArm.Expressions.Validation;

/// <summary>
/// The checks <see cref="ExpressionValidator"/> runs over the evaluated properties of every resource after static evaluation: names a
/// property expression cannot resolve, and values of the wrong JSON type for their property.
/// </summary>
internal static class PropertyExpressionChecks
{
    /// <summary>The failures of a property expression that are always an authoring error offline, whatever the deployment supplies.</summary>
    private static readonly HashSet<string> PropertyAuthoringErrors = new(StringComparer.Ordinal)
    {
        DiagnosticCodes.UndefinedVariable,
        DiagnosticCodes.UndefinedParameter,
        DiagnosticCodes.UnknownFunction,
    };

    /// <summary>
    /// Evaluates each expression inside the properties of every resource, offline (validate mode), and reports the ones that name a variable
    /// or parameter the template does not declare, or a function that does not exist: without this a typo in <c>properties</c> only failed
    /// at deployment, because only names, conditions, copy and dependsOn are evaluated statically. Everything else an evaluation can
    /// report (a <c>reference()</c> that is only known at deployment, deploy-time unknown leaves, a value that depends on live data) is
    /// skipped, as <see cref="CheckPropertyTypes"/> skips it; each expression is evaluated on its own so one failure does not hide the next.
    /// </summary>
    internal static void CheckPropertyReferences(TemplateDocument document, StaticContext staticContext, List<Diagnostic> diagnostics)
    {
        var seen = new HashSet<(string Code, JsonPath? Path)>(diagnostics.Select(d => (d.Code, d.Path)));
        foreach (var resource in document.Resources)
        {
            if (resource.Properties is not JsonObject declared || !ContainsExpression(declared))
            {
                continue;
            }

            var context = staticContext.ContextFor(resource);
            CheckPropertyReferences(document, declared, resource.Path.Append("properties"), context, seen, diagnostics);
        }
    }

    private static void CheckPropertyReferences(TemplateDocument document, JsonNode? node, JsonPath path, EvaluationContext context, HashSet<(string Code, JsonPath? Path)> seen, List<Diagnostic> diagnostics)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (PropertyCopyBlock.IsBlock(property.Key, property.Value))
                    {
                        foreach (var entry in PropertyCopyBlock.Entries(obj))
                        {
                            var entryPath = path.Append(property.Key).Append(entry.Index);
                            CheckPropertyReferences(document, entry.Count, entryPath.Append("count"), context, seen, diagnostics);
                            var loop = context.PushCopy(CopyFrame.Unresolved(entry.Name ?? PropertyCopyBlock.Key, null, requiresName: true));
                            CheckPropertyReferences(document, entry.Input, entryPath.Append("input"), loop, seen, diagnostics);
                        }

                        continue;
                    }

                    CheckPropertyReferences(document, property.Value, path.Append(property.Key), context, seen, diagnostics);
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    CheckPropertyReferences(document, array[i], path.Append(i), context, seen, diagnostics);
                }

                break;
            default:
                {
                    if (!TemplateJson.IsExpression(node) || TemplateJson.AsString(node) is not { } text)
                    {
                        return;
                    }

                    try
                    {
                        _ = ExpressionEvaluator.Default.EvaluateString(text, context);
                    }
                    catch (ExpressionException ex) when (PropertyAuthoringErrors.Contains(ex.Code))
                    {
                        if (seen.Add((ex.Code, path)))
                        {
                            diagnostics.Add(Diagnostic.Error(ex.Code, ex.MessageWithOffset, path, document.LocationOf(path), document.SourcePath));
                        }
                    }
                    catch (ExpressionException)
                    {
                        // Syntax errors, limits and deploy-time values are reported by the other checks, or only at deployment.
                    }

                    break;
                }
        }
    }

    /// <summary>
    /// Evaluates the properties of every resource that holds an expression, offline, and reports a value that has the wrong JSON type for its
    /// property (<c>PropertyTypeMismatch</c>, for example <c>"description": "[createArray('a')]"</c> on a group): the template schema cannot judge
    /// an expression, and the value would be refused at deployment. Values that are not known offline (deploy-time unknowns, secure values) are
    /// skipped; an expression that cannot be evaluated is reported by the other checks, not here.
    /// </summary>
    internal static void CheckPropertyTypes(TemplateDocument document, StaticContext staticContext, List<Diagnostic> diagnostics)
    {
        foreach (var resource in document.Resources)
        {
            if (resource.Type is null || resource.Properties is not JsonObject declared || !ContainsExpression(declared))
            {
                continue;
            }

            JsonNode? evaluated;
            try
            {
                evaluated = ExpressionEvaluator.Default.EvaluateTree(declared, staticContext.ContextFor(resource));
            }
            catch (ExpressionException)
            {
                continue;
            }

            var propertiesPath = resource.Path.Append("properties");
            foreach (var finding in PropertyTypeChecker.Check(resource.Type, evaluated, static leaf => UnknownJson.IsUnknown(leaf) || SecretJson.IsSecret(leaf)))
            {
                var declaredPath = NearestDeclaredExpression(declared, finding.Path);
                if (declaredPath is null)
                {
                    // The wrong value sits in plain template text: the template schema reports it.
                    continue;
                }

                string where = declaredPath == finding.Path ? string.Empty : $" (the evaluated value at '{finding.Path}')";
                var path = propertiesPath.Append(declaredPath);
                diagnostics.Add(Diagnostic.Error(
                    DiagnosticCodes.PropertyTypeMismatch,
                    $"The expression evaluates to a value of the wrong type{where}: {finding.Message} (schema keyword 'type'). The value would be refused at deployment.",
                    path,
                    document.LocationOf(path),
                    document.SourcePath));
            }
        }
    }

    private static bool ContainsExpression(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (ContainsExpression(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (ContainsExpression(item))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return TemplateJson.IsExpression(node);
        }
    }

    /// <summary>
    /// Follows <paramref name="evaluatedPath"/> down the declared (unevaluated) properties and returns the path of the expression string it
    /// ends in, or <c>null</c> when the value comes from plain text. A value inside an evaluated array or object lies below the expression
    /// that produced it, so the path of that expression is returned.
    /// </summary>
    private static JsonPath? NearestDeclaredExpression(JsonNode declared, JsonPath evaluatedPath)
    {
        JsonNode? current = declared;
        var walked = JsonPath.Root;
        foreach (var segment in evaluatedPath.Segments)
        {
            if (TemplateJson.IsExpression(current))
            {
                return walked;
            }

            JsonNode? next = null;
            if (!segment.IsIndex && current is JsonObject obj)
            {
                obj.TryGetPropertyValue(segment.Name!, out next);
            }
            else if (segment.IsIndex && current is JsonArray array && segment.Index >= 0 && segment.Index < array.Count)
            {
                next = array[segment.Index];
            }
            else
            {
                return null;
            }

            current = next;
            walked = walked.Append(segment);
        }

        return TemplateJson.IsExpression(current) ? walked : null;
    }
}
