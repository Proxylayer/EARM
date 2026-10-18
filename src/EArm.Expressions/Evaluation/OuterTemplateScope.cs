using System;
using System.Collections.Immutable;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// What a nested template evaluated in the <b>outer</b> scope (<c>expressionEvaluationOptions.scope: outer</c>) sees of the template that declares the
/// deployment, besides its parameters, variables and functions: ARM evaluates such a template's expressions in the parent's context
/// (https://learn.microsoft.com/azure/azure-resource-manager/templates/linked-templates#expression-evaluation-scope-in-nested-templates), so
/// <list type="bullet">
/// <item><c>copyIndex()</c> is the iteration of the deployment's own copy loop (<see cref="CopyStack"/>; ARM refuses a copy loop inside such a
/// template, so there is no other resource loop for it to mean);</item>
/// <item><c>reference('name')</c> also finds the parent's resources by name (<see cref="Resources"/>), after the template's own;</item>
/// <item><c>reference()</c> and <c>references()</c> may not be used in its outputs: ARM requires the inner scope (or a linked template) for them
/// (https://learn.microsoft.com/azure/azure-resource-manager/templates/template-functions-resource#reference).</item>
/// </list>
/// </summary>
/// <param name="CopyStack">The copy iteration of the deployment instance in its template (empty when the deployment has no copy loop).</param>
/// <param name="Resources">The resources of the template that declares the deployment.</param>
public sealed record OuterTemplateScope(ImmutableStack<CopyFrame> CopyStack, TemplateResourceIndex Resources)
{
    /// <summary>True when the deployment is an iteration of a copy loop: <c>copyIndex()</c> is legal anywhere in the template's resources and outputs.</summary>
    public bool InCopy => !CopyStack.IsEmpty;

    /// <summary>Adds the parent's resources to <paramref name="index"/> (the template's own come first; an id already there is kept).</summary>
    public void AddParentResources(TemplateResourceIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        foreach (var entry in Resources.Entries)
        {
            index.Add(entry);
        }
    }
}
