using System;
using System.Collections.Generic;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;

namespace EArm.Engine.Planning.Nested;

/// <summary>
/// One template of a deployment: the main template, or the template of a nested or linked deployment (<see cref="DeploymentResourceType"/>)
/// expanded by <see cref="NestedDeploymentExpander"/>. Every <see cref="ResourceInstance"/> belongs to one scope: its declarations, static analysis,
/// resource index (names and symbolic names resolve within the template, as in ARM) and evaluation context come from it. Scopes of nested
/// deployments chain to their parent through <see cref="Deployment"/>, the instance of the deployment resource that expanded them.
/// </summary>
public sealed class TemplateScope
{
    private TemplateScope(TemplateDocument document, StaticContext staticContext, EvaluationContext context, string? location, TemplateScope? parent, ResourceInstance? deployment, NestedTemplateScope kind, JsonPath pathPrefix)
    {
        PathPrefix = pathPrefix;
        Document = document;
        Static = staticContext;
        Context = context;
        Location = location;
        Parent = parent;
        Deployment = deployment;
        Kind = kind;
        Depth = parent is null ? 0 : parent.Depth + 1;
        var path = new List<string>();
        if (parent is not null)
        {
            path.AddRange(parent.DeploymentPath);
        }

        if (deployment is not null)
        {
            path.Add(deployment.Id.Name);
        }

        DeploymentPath = path;
    }

    /// <summary>
    /// Where the template sits in its file: the root for the main template and a linked template (its own file), the place of the inline
    /// <c>template</c> object for a nested one (<c>resources[0].properties.template</c>). Paths of the document are relative to it; diagnostics
    /// report <c>PathPrefix + path</c>, so they name the place in the file.
    /// </summary>
    public JsonPath PathPrefix { get; }

    /// <summary>The template.</summary>
    public TemplateDocument Document { get; }

    /// <summary>Its static analysis (resolved names, dependsOn, references).</summary>
    public StaticContext Static { get; }

    /// <summary>The base context its resources are evaluated in (resource scope, no copy iteration).</summary>
    public EvaluationContext Context { get; }

    /// <summary>The file path or https URL the template was read from (what a <c>relativePath</c> inside it is relative to); <c>null</c> for a template parsed from text.</summary>
    public string? Location { get; }

    /// <summary>The template that declares <see cref="Deployment"/>; <c>null</c> for the main template.</summary>
    public TemplateScope? Parent { get; }

    /// <summary>The instance of the deployment resource this template is the template of; <c>null</c> for the main template.</summary>
    public ResourceInstance? Deployment { get; }

    /// <summary>Where the template's expressions are evaluated: its own scope, or its parent's (an inline template in the outer scope).</summary>
    public NestedTemplateScope Kind { get; }

    /// <summary>0 for the main template, 1 for the template of a deployment it declares, and so on.</summary>
    public int Depth { get; }

    public bool IsRoot => Parent is null;

    /// <summary>The names of the deployments from the main template down to this one (empty for the main template).</summary>
    public IReadOnlyList<string> DeploymentPath { get; }

    /// <summary><see cref="DeploymentPath"/> joined with <c>/</c>; <c>null</c> for the main template.</summary>
    public string? DeploymentPathText => DeploymentPath.Count == 0 ? null : string.Join('/', DeploymentPath);

    /// <summary>The deployment instances whose templates enclose this one, innermost first.</summary>
    public IEnumerable<ResourceInstance> EnclosingDeployments
    {
        get
        {
            for (var scope = this; scope?.Deployment is not null; scope = scope.Parent)
            {
                yield return scope.Deployment;
            }
        }
    }

    /// <summary>The locations of this template and every template that encloses it (cycle detection of linked templates).</summary>
    public IEnumerable<string> LocationChain
    {
        get
        {
            for (var scope = this; scope is not null; scope = scope.Parent)
            {
                if (scope.Location is not null)
                {
                    yield return scope.Location;
                }
            }
        }
    }

    /// <summary>The scope of the main template.</summary>
    public static TemplateScope Root(TemplateDocument document, StaticContext staticContext, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(staticContext);
        ArgumentNullException.ThrowIfNull(context);
        string? location = document.SourcePath is null ? null : LinkedTemplateReader.IsUrl(document.SourcePath) ? document.SourcePath : System.IO.Path.GetFullPath(document.SourcePath);
        return new TemplateScope(document, staticContext, context, location, null, null, NestedTemplateScope.Inner, JsonPath.Root);
    }

    /// <summary>The scope of the template of <paramref name="deployment"/>, a deployment declared in this template.</summary>
    internal TemplateScope Nest(TemplateDocument document, StaticContext staticContext, EvaluationContext context, string? location, ResourceInstance deployment, NestedTemplateScope kind, JsonPath pathPrefix)
    {
        return new TemplateScope(document, staticContext, context, location, this, deployment, kind, pathPrefix);
    }

    /// <summary>A diagnostic about <paramref name="path"/> of this template: the path reported within the file, the location and the file.</summary>
    public Diagnostic Error(string code, string message, JsonPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Diagnostic.Error(code, message, PathPrefix.Append(path), Document.LocationOf(path), Document.SourcePath);
    }

    /// <inheritdoc cref="Error"/>
    public Diagnostic Warning(string code, string message, JsonPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Diagnostic.Warning(code, message, PathPrefix.Append(path), Document.LocationOf(path), Document.SourcePath);
    }

    /// <summary>
    /// <paramref name="diagnostic"/> of this template's document with its path reported within the file (<see cref="PathPrefix"/>); a diagnostic
    /// about another file (a parameter file) is returned as it is.
    /// </summary>
    public Diagnostic InFile(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (PathPrefix.IsRoot || diagnostic.Path is null || !string.Equals(diagnostic.Source, Document.SourcePath, StringComparison.Ordinal))
        {
            return diagnostic;
        }

        return diagnostic with { Path = PathPrefix.Append(diagnostic.Path) };
    }
}
