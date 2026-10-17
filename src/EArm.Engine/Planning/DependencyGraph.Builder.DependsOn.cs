using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Static;
using EArm.Expressions.Values;

namespace EArm.Engine.Planning;

public sealed partial class DependencyGraph
{
    private sealed partial class Builder
    {
        // ---- dependsOn ----

        private void AddDeclaredDependencies(ResourceInstance instance)
        {
            var declaration = instance.Declaration;
            for (int i = 0; i < instance.Resolved.DependsOn.Count; i++)
            {
                var dependency = instance.Resolved.DependsOn[i];
                var kind = dependency.Source == DependencySource.Inferred ? DependencyKind.Parent : DependencyKind.Explicit;
                var path = dependency.Source == DependencySource.Inferred ? declaration.Path : declaration.Path.Append("dependsOn").Append(i);

                if (dependency.Target is { } target)
                {
                    if (_byPlannedId.TryGetValue(target, out var targetInstance))
                    {
                        AddEdge(instance, targetInstance, kind, dependency.Text, path);
                    }
                    else
                    {
                        Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{dependency.Text}' resolves to '{target.Id}', which no resource instance produces after expansion.", path);
                    }

                    continue;
                }

                if (dependency.CopyName is { } copyName)
                {
                    foreach (var iteration in LoopInstances(copyName))
                    {
                        if (!ReferenceEquals(iteration, instance))
                        {
                            AddEdge(instance, iteration, DependencyKind.CopyLoop, dependency.Text, path);
                        }
                    }

                    continue;
                }

                if (dependency.Deferred)
                {
                    foreach (var target2 in ResolveDeferred(instance, dependency.Text, path))
                    {
                        AddEdge(instance, target2, kind, dependency.Text, path);
                    }
                }
            }
        }

        private IEnumerable<ResourceInstance> LoopInstances(string copyName)
        {
            return _instances.Where(i => i.Copy is not null && i.Copy.Name.Equals(copyName, StringComparison.OrdinalIgnoreCase) && InCurrentTemplate(i));
        }

        /// <summary>Resolves a <c>dependsOn</c> entry that depends on <c>copyIndex()</c>, now with the concrete frame.</summary>
        private IEnumerable<ResourceInstance> ResolveDeferred(ResourceInstance instance, string text, JsonPath path)
        {
            JsonNode? value;
            try
            {
                value = _evaluator.EvaluateString(text, instance.Context);
            }
            catch (ExpressionException ex)
            {
                Error(ex.Code, ex.MessageWithOffset, path);
                return [];
            }

            if (UnknownJson.IsUnknown(value, out var unknown))
            {
                if (unknown.ResourceId is { } symbolic && _byPlannedId.TryGetValue(symbolic, out var symbolicTarget))
                {
                    return [symbolicTarget];
                }

                Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{text}' does not resolve to a resource of this template (its value is only known at deployment: {unknown.Symbol}).", path);
                return [];
            }

            if (!JsonValues.TryGetString(value, out var target))
            {
                Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{text}' must evaluate to a string, got {JsonValues.Describe(value)}.", path);
                return [];
            }

            // languageVersion 2.0: a symbolic name, 'loop' (every iteration) or 'loop[i]' (one iteration).
            if (SymbolicReference.TryParse(target, out var symbolicName, out var copyIndex))
            {
                var symbolic = _instances
                    .Where(i => !ReferenceEquals(i, instance) && InCurrentTemplate(i) && string.Equals(i.Declaration.SymbolicName, symbolicName, StringComparison.Ordinal) && (copyIndex is null || i.Copy?.Index == copyIndex))
                    .ToList();
                if (symbolic.Count > 0)
                {
                    return symbolic;
                }

                if (_instances.Any(i => InCurrentTemplate(i) && string.Equals(i.Declaration.SymbolicName, symbolicName, StringComparison.Ordinal)))
                {
                    Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' names no iteration of '{symbolicName}'.", path);
                    return [];
                }
            }

            ResourceIdentifier? id = null;
            if (target.StartsWith(ResourceIdentifier.ProvidersPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!_catalog.TryParseResourceId(target, out id) || id is null)
                {
                    Error(DiagnosticCodes.InvalidResourceId, $"dependsOn entry '{target}' is not a valid resource id.", path);
                    return [];
                }
            }
            else if (!TryParseTypeAndName(target, out id))
            {
                var byName = _instances.Where(i => !i.IsSymbolic && InCurrentTemplate(i) && string.Equals(i.Id.Name, target, StringComparison.Ordinal)).ToList();
                if (byName.Count == 0)
                {
                    byName = _instances.Where(i => !i.IsSymbolic && InCurrentTemplate(i) && string.Equals(i.Id.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (byName.Count == 1)
                {
                    return byName;
                }

                if (byName.Count > 1)
                {
                    Error(DiagnosticCodes.AmbiguousDependsOn, $"dependsOn entry '{target}' names {byName.Count} resources; use a resource id or 'type/name'.", path);
                    return [];
                }

                var loop = LoopInstances(target).Where(i => !ReferenceEquals(i, instance)).ToList();
                if (loop.Count > 0)
                {
                    return loop;
                }

                Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' does not name a resource of this template.", path);
                return [];
            }

            if (id is not null && _byPlannedId.TryGetValue(id, out var found))
            {
                return [found];
            }

            Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' does not name a resource of this template.", path);
            return [];
        }

        private bool TryParseTypeAndName(string text, out ResourceIdentifier? identifier)
        {
            identifier = null;
            string deploymentPrefix = DeploymentResourceType.Type + "/";
            if (text.StartsWith(deploymentPrefix, StringComparison.OrdinalIgnoreCase) && text.Length > deploymentPrefix.Length && !text[deploymentPrefix.Length..].Contains('/'))
            {
                identifier = new ResourceIdentifier(DeploymentResourceType.Type, [text[deploymentPrefix.Length..]]);
                return true;
            }

            if (!text.StartsWith(ResourceIdentifier.ProviderNamespace + "/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var definition in _catalog.Types.OrderByDescending(d => d.Type.Length))
            {
                string prefix = definition.Type + "/";
                if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var segments = text[prefix.Length..].Split('/');
                if (segments.Length == definition.Depth && !segments.Any(string.IsNullOrEmpty))
                {
                    identifier = new ResourceIdentifier(definition.Type, segments);
                    return true;
                }
            }

            return false;
        }
    }
}
