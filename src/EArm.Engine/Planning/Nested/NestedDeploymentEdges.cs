using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Engine.Providers;

namespace EArm.Engine.Planning.Nested;

/// <summary>
/// The dependency edges of nested deployments (<see cref="DependencyKind.NestedDeployment"/>): ARM deploys a nested deployment as one resource
/// of its parent, so whatever depends on it waits for everything its template deploys, and everything its template deploys waits for whatever the
/// deployment depends on (https://learn.microsoft.com/azure/azure-resource-manager/templates/linked-templates#dependencies). With the template's
/// resources planned in the same graph as the parent's, that is: the deployment instance depends on every instance of its template (nested
/// deployments included, at any depth), and each of those depends on the dependencies the deployment itself declares or references.
/// </summary>
internal static class NestedDeploymentEdges
{
    /// <summary>
    /// The edges to add, as <c>(dependent, dependency, detail)</c>. <paramref name="dependenciesOf"/> gives the dependencies an instance has before
    /// these edges are added; it is read for every deployment before the list is returned.
    /// </summary>
    public static IReadOnlyList<(ResourceInstance Dependent, ResourceInstance Dependency, string Detail)> Compute(IReadOnlyList<ResourceInstance> instances, Func<ResourceInstance, IReadOnlyList<ResourceInstance>> dependenciesOf)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(dependenciesOf);

        // The deployments' own dependencies, read before any nested edge exists (the deployment's edges to its own resources must not count).
        var own = new Dictionary<ResourceInstance, IReadOnlyList<ResourceInstance>>();
        foreach (var instance in instances)
        {
            if (instance.Definition.IsTemplateLevel)
            {
                own[instance] = dependenciesOf(instance);
            }
        }

        var edges = new List<(ResourceInstance, ResourceInstance, string)>();
        if (own.Count == 0)
        {
            return edges;
        }

        foreach (var instance in instances)
        {
            if (instance.Scope is null)
            {
                continue;
            }

            foreach (var deployment in instance.Scope.EnclosingDeployments)
            {
                edges.Add((deployment, instance, $"nested deployment '{deployment.Id.Name}' contains {instance.Describe()}"));
                if (!own.TryGetValue(deployment, out var dependencies))
                {
                    continue;
                }

                foreach (var dependency in dependencies.Where(d => !ReferenceEquals(d, instance)))
                {
                    edges.Add((instance, dependency, $"{instance.Describe()} is deployed by nested deployment '{deployment.Id.Name}', which depends on {dependency.Describe()}"));
                }
            }
        }

        return edges;
    }
}
