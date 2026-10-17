using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Loading;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Functions;
using EArm.Expressions.Validation;
using EArm.Expressions.Values;

namespace EArm.Engine.Planning.Nested;

/// <summary>
/// Expands nested and linked deployments (<see cref="DeploymentResourceType"/>) into the resources of their templates, recursively
/// (https://learn.microsoft.com/azure/azure-resource-manager/templates/linked-templates). For every deployment instance whose condition is true:
/// <list type="number">
/// <item>its settings are evaluated in the parent template (<see cref="DeploymentSettings"/>);</item>
/// <item>its template is loaded: the inline <c>template</c> object (locations stay those of the parent file), or the linked file
/// (<see cref="LinkedTemplateReader"/>; a template that links itself, directly or not, is a cycle);</item>
/// <item>its parameters are bound: an inner-scope template gets the values of <c>parameters</c> (evaluated in the parent, with the deployment's
/// copy iteration) or of the <c>parametersLink</c> file; an outer-scope template sees the parent's parameters, variables and functions;</item>
/// <item>the template is validated like a main template (offline checks and static evaluation);</item>
/// <item>its resources are expanded (<see cref="ResourceExpander"/>) with an evaluation context of their own (<see cref="TemplateScope"/>) and
/// planned in the same graph as the parent's.</item>
/// </list>
/// Ids must be unique across the whole expansion: two templates that declare the same object are a <see cref="DiagnosticCodes.DuplicateResourceId"/>
/// error, as two declarations of one template are. Depth and count are bounded (<see cref="DeploymentResourceType.MaxDepth"/>,
/// <see cref="DeploymentResourceType.MaxDeployments"/>).
/// </summary>
internal sealed class NestedDeploymentExpander
{
    private readonly ExpressionEvaluator _evaluator;
    private readonly ResourceTypeCatalog _catalog;
    private readonly FunctionRegistry _functions;
    private readonly LinkedTemplateReader _reader;
    private readonly bool _offline;
    private readonly CancellationToken _cancellationToken;
    private readonly TemplateLoader _loader;
    private int _deployments;
    private int _declarationOffset;

    /// <param name="evaluator">The expression evaluator.</param>
    /// <param name="catalog">The type catalog.</param>
    /// <param name="functions">The function registry the templates are validated against.</param>
    /// <param name="reader">Reads linked templates and parameter files.</param>
    /// <param name="offline">
    /// True for offline validation: an https link the reader cannot fetch is reported as a warning (<see cref="EngineDiagnosticCodes.LinkedTemplateNotFetched"/>)
    /// and its deployment is not expanded; otherwise it is an error.
    /// </param>
    /// <param name="cancellationToken">Cancels downloads.</param>
    public NestedDeploymentExpander(ExpressionEvaluator evaluator, ResourceTypeCatalog catalog, FunctionRegistry functions, LinkedTemplateReader reader, bool offline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(functions);
        ArgumentNullException.ThrowIfNull(reader);
        _evaluator = evaluator;
        _catalog = catalog;
        _functions = functions;
        _reader = reader;
        _offline = offline;
        _cancellationToken = cancellationToken;
        _loader = new TemplateLoader(catalog);
    }

    /// <summary>
    /// Assigns <paramref name="root"/> to the instances of the main template and returns them followed by the instances of every nested template.
    /// Problems are appended to <paramref name="diagnostics"/>; a deployment whose template has errors is not expanded.
    /// </summary>
    public List<ResourceInstance> Expand(TemplateScope root, IReadOnlyList<ResourceInstance> rootInstances, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootInstances);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var all = new List<ResourceInstance>(rootInstances.Count);
        foreach (var instance in rootInstances)
        {
            instance.Scope = root;
            all.Add(instance);
        }

        _declarationOffset = root.Document.Resources.Count;
        var pending = new Queue<ResourceInstance>(rootInstances.Where(i => i.IsDeployment));
        while (pending.Count > 0)
        {
            var deployment = pending.Dequeue();
            int mark = diagnostics.Count;
            var expanded = ExpandDeployment(deployment, root.Context, diagnostics);
            if (_offline)
            {
                // Offline (validate, permissions) a value may be unknown only because a parameter has no value yet: the deployment is not expanded, with a warning.
                for (int i = mark; i < diagnostics.Count; i++)
                {
                    if (diagnostics[i].Code == EngineDiagnosticCodes.NestedDeploymentNotStatic)
                    {
                        diagnostics[i] = diagnostics[i] with { Severity = DiagnosticSeverity.Warning, Message = diagnostics[i].Message + " Offline, the deployment is not expanded; what-if and create expand it." };
                    }
                }
            }

            foreach (var inner in expanded)
            {
                if (all.Count >= EvaluationLimits.MaxInstances)
                {
                    diagnostics.Add(deployment.Scope!.Error(DiagnosticCodes.ExpansionLimitExceeded, $"The template and its nested deployments expand to more than {EvaluationLimits.MaxInstances} resource instances; the rest of nested deployment '{deployment.Id.Name}' is not expanded.", deployment.Declaration.Path));
                    break;
                }

                all.Add(inner);
                if (inner.IsDeployment)
                {
                    pending.Enqueue(inner);
                }
            }
        }

        return RemoveDuplicates(all, diagnostics);
    }

    private IReadOnlyList<ResourceInstance> ExpandDeployment(ResourceInstance deployment, EvaluationContext rootContext, List<Diagnostic> diagnostics)
    {
        var parent = deployment.Scope!;
        var path = deployment.Declaration.Path;
        if (deployment.IsIgnored)
        {
            // ARM: a deployment whose condition is false is not deployed; its template is not read.
            return [];
        }

        if (deployment.Condition is null)
        {
            diagnostics.Add(parent.Error(EngineDiagnosticCodes.NestedDeploymentNotStatic, $"The condition of nested deployment '{deployment.Id.Name}' depends on a value only known at deployment; it must be computable from parameters and variables, since it decides which resources the template contains.", path.Append("condition")));
            return [];
        }

        if (deployment.IsSymbolic)
        {
            diagnostics.Add(parent.Error(EngineDiagnosticCodes.NestedDeploymentNotStatic, $"The name of a nested deployment must be computable from parameters and variables ({deployment.PlannedId.NameLeaf} is only known at deployment).", path.Append("name")));
            return [];
        }

        if (parent.Depth + 1 > DeploymentResourceType.MaxDepth)
        {
            diagnostics.Add(parent.Error(EngineDiagnosticCodes.NestedDeploymentLimitExceeded, $"Nested deployment '{deployment.Id.Name}' is more than {DeploymentResourceType.MaxDepth} levels deep (deployments: {string.Join(" > ", parent.DeploymentPath.Append(deployment.Id.Name))}); it is not expanded.", path));
            return [];
        }

        if (++_deployments > DeploymentResourceType.MaxDeployments)
        {
            diagnostics.Add(parent.Error(EngineDiagnosticCodes.NestedDeploymentLimitExceeded, $"The template expands more than {DeploymentResourceType.MaxDeployments} nested deployments (ARM's what-if limit); '{deployment.Id.Name}' is not expanded.", path));
            return [];
        }

        var settings = DeploymentSettings.Read(deployment, parent, _evaluator, diagnostics);
        if (settings is null)
        {
            return [];
        }

        var loaded = Load(deployment, parent, settings, diagnostics);
        if (loaded is null)
        {
            return [];
        }

        var (document, location, prefix, fileParameters) = loaded.Value;
        var kind = settings.TemplateLocation is null ? settings.Scope : NestedTemplateScope.Inner;
        var found = new List<Diagnostic>();
        var passed = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        TemplateDocument analyzed;
        if (kind == NestedTemplateScope.Inner)
        {
            document = BindParameters(deployment, parent, document, settings, fileParameters, passed, diagnostics);
            found.AddRange(new TemplateValidator(_catalog).Validate(document));
            analyzed = document;
        }
        else
        {
            // Outer scope: the template's own parameters, variables and functions are ignored (NestedDeploymentChecker warns); the parent's are used.
            found.AddRange(new TemplateValidator(_catalog).Validate(document));
            var parentDocument = parent.Document;
            analyzed = document with
            {
                Parameters = parentDocument.Parameters,
                ParameterValues = parentDocument.ParameterValues,
                Variables = parentDocument.Variables,
                VariableIterations = parentDocument.VariableIterations,
                Functions = parentDocument.Functions,
                Definitions = parentDocument.Definitions,
            };
        }

        // deployment() in an inner-scope template is the nested deployment (its name); in the outer scope it is the parent's.
        var info = kind == NestedTemplateScope.Inner
            ? rootContext.Deployment with
            {
                Name = deployment.Id.Name,
                TemplateHash = "sha256:" + CanonicalJson.Hash(analyzed.Root),
                Mode = "Incremental",
                Parameters = DeploymentInfo.SuppliedParameters(analyzed.ParameterValues),
            }
            : null;
        var analysis = new ExpressionValidator(_catalog, _functions, info ?? parent.Context.Deployment).Analyze(analyzed);
        found.AddRange(kind == NestedTemplateScope.Inner ? analysis.Diagnostics : analysis.Diagnostics.Where(d => IsUnder(d, "resources") || IsUnder(d, "outputs")));

        var scope = parent.Nest(analyzed, analysis.Static, CreateContext(deployment, parent, analyzed, analysis.Static, info, passed, rootContext), location, deployment, kind, prefix);
        bool failed = false;
        foreach (var finding in found)
        {
            // A parameter the parent must pass and does not is an error for a nested template, offline too (the parent template is complete).
            var diagnostic = finding.Code == DiagnosticCodes.MissingParameterValue && !finding.IsError
                ? finding with { Severity = DiagnosticSeverity.Error, Message = finding.Message + $" Pass it in the 'parameters' of nested deployment '{deployment.Id.Name}'." }
                : finding;
            failed |= diagnostic.IsError;
            diagnostics.Add(scope.InFile(diagnostic));
        }

        if (failed)
        {
            return [];
        }

        var expansion = new List<Diagnostic>();
        var instances = new ResourceExpander(_evaluator, _catalog) { DeclarationOffset = _declarationOffset }.Expand(analysis.Static, scope.Context, expansion);
        _declarationOffset += analyzed.Resources.Count;
        diagnostics.AddRange(expansion.Select(scope.InFile));
        if (expansion.Any(d => d.IsError))
        {
            return [];
        }

        foreach (var instance in instances)
        {
            instance.Scope = scope;
            if (instance.Copy is { } copy)
            {
                // As the planner does for the main template: the concrete ids of copy iterations join the template's index.
                scope.Context.Resources.Add(new TemplateResourceIndex.Entry(instance.PlannedId, instance.IsSymbolic, instance.Declaration, copy.Name, copy.Index));
            }
        }

        deployment.NestedTemplate = scope;
        return instances;
    }

    /// <summary>Loads the template (and the parameter file) of the deployment; <c>null</c> when it cannot (reported).</summary>
    private (TemplateDocument Document, string? Location, JsonPath Prefix, ParameterSet Parameters)? Load(ResourceInstance deployment, TemplateScope parent, DeploymentSettings settings, List<Diagnostic> diagnostics)
    {
        var path = deployment.Declaration.Path.Append("properties");
        var fileParameters = ParameterSet.Empty;
        if (settings.ParametersLocation is { } parametersLocation)
        {
            var linkPath = path.Append("parametersLink");
            byte[]? bytes = ReadLinked(parentScope: parent, parametersLocation, "Linked parameter file", deployment, linkPath, diagnostics);
            if (bytes is null)
            {
                return null;
            }

            try
            {
                fileParameters = ParameterSet.Parse(bytes, parametersLocation.Display);
            }
            catch (TemplateLoadException ex)
            {
                diagnostics.Add(ex.ToDiagnostic());
                return null;
            }

            if (settings.ParametersContentVersion is { } expected && !string.Equals(expected, fileParameters.ContentVersion, StringComparison.Ordinal))
            {
                diagnostics.Add(parent.Error(EngineDiagnosticCodes.LinkedTemplateContentVersionMismatch, $"parametersLink.contentVersion is '{expected}' but the parameter file {parametersLocation.Display} has contentVersion '{fileParameters.ContentVersion ?? "(none)"}'.", linkPath.Append("contentVersion")));
                return null;
            }

            if (fileParameters.Values.Values.Any(v => v.IsKeyVaultReference))
            {
                diagnostics.Add(parent.Error(DiagnosticCodes.NestedDeploymentFeatureNotSupported, $"The linked parameter file {parametersLocation.Display} holds a Key Vault reference, which is not supported for a nested deployment; pass the secret from a secure parameter of the parent template with 'parameters'.", linkPath));
                return null;
            }
        }

        if (settings.InlineTemplate is { } inline)
        {
            var prefix = deployment.Declaration.Path.Append("properties").Append("template");
            var parentDocument = parent.Document;
            var document = _loader.LoadNested(inline, parentDocument.SourcePath, parentDocument.SourceMap.Rebase(prefix), fileParameters);
            return (document, parent.Location, parent.PathPrefix.Append(prefix), fileParameters);
        }

        var location = settings.TemplateLocation!;
        var templateLinkPath = path.Append("templateLink");
        var chain = parent.LocationChain.Select(LinkedTemplateReader.NormalizeLocation).ToList();
        if (chain.Contains(location.Key, StringComparer.Ordinal))
        {
            diagnostics.Add(parent.Error(EngineDiagnosticCodes.LinkedTemplateCycle, $"Nested deployment '{deployment.Id.Name}' links {location.Display}, which is already being deployed by an enclosing template (deployments: {string.Join(" > ", parent.DeploymentPath.Append(deployment.Id.Name))}); a linked template cannot deploy itself.", templateLinkPath));
            return null;
        }

        byte[]? templateBytes = ReadLinked(parent, location, "Linked template", deployment, templateLinkPath, diagnostics);
        if (templateBytes is null)
        {
            return null;
        }

        TemplateDocument linked;
        try
        {
            linked = _loader.Parse(templateBytes, location.Display, fileParameters) with { IsNested = true };
        }
        catch (TemplateLoadException ex)
        {
            diagnostics.Add(ex.ToDiagnostic());
            return null;
        }

        if (settings.TemplateContentVersion is { } version && !string.Equals(version, linked.ContentVersion, StringComparison.Ordinal))
        {
            diagnostics.Add(parent.Error(EngineDiagnosticCodes.LinkedTemplateContentVersionMismatch, $"templateLink.contentVersion is '{version}' but {location.Display} has contentVersion '{linked.ContentVersion ?? "(none)"}'.", templateLinkPath.Append("contentVersion")));
            return null;
        }

        return (linked, location.Value, JsonPath.Root, fileParameters);
    }

    private byte[]? ReadLinked(TemplateScope parentScope, LinkedLocation location, string kind, ResourceInstance deployment, JsonPath path, List<Diagnostic> diagnostics)
    {
        if (!_reader.CanFetch(location))
        {
            diagnostics.Add(_offline
                ? parentScope.Warning(EngineDiagnosticCodes.LinkedTemplateNotFetched, $"{kind} {location.Display} of nested deployment '{deployment.Id.Name}' is an https URL; offline validation does not download it, so its resources are checked by what-if and create.", path)
                : parentScope.Error(EngineDiagnosticCodes.LinkedTemplateUnavailable, $"{kind} {location.Display} of nested deployment '{deployment.Id.Name}' is an https URL and downloads are not available here.", path));
            return null;
        }

        try
        {
            return _reader.Read(location, kind, _cancellationToken);
        }
        catch (TemplateLoadException ex)
        {
            diagnostics.Add(parentScope.Error(ex.Code == DiagnosticCodes.InputTooLarge ? ex.Code : EngineDiagnosticCodes.LinkedTemplateUnavailable, $"Nested deployment '{deployment.Id.Name}': {ex.Message}", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            diagnostics.Add(parentScope.Error(EngineDiagnosticCodes.LinkedTemplateUnavailable, $"Nested deployment '{deployment.Id.Name}': {kind} {location.Display} could not be read: {ex.Message}", path));
        }

        return null;
    }

    /// <summary>
    /// Binds the values an inner-scope template receives from <c>properties.parameters</c>: each is the parent's expression evaluated now (a value that
    /// depends on a resource the parent deploys is a deploy-time placeholder here and is evaluated again when the template's resources are planned and
    /// applied, see <see cref="NestedParameters"/>).
    /// </summary>
    private TemplateDocument BindParameters(ResourceInstance deployment, TemplateScope parent, TemplateDocument document, DeploymentSettings settings, ParameterSet fileParameters, Dictionary<string, JsonNode?> passed, List<Diagnostic> diagnostics)
    {
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

    /// <summary>The base context of the template's resources: its own (inner scope, <paramref name="info"/> set) or the parent's stores (outer scope).</summary>
    private EvaluationContext CreateContext(ResourceInstance deployment, TemplateScope parent, TemplateDocument document, Expressions.Static.StaticContext staticContext, DeploymentInfo? info, Dictionary<string, JsonNode?> passed, EvaluationContext rootContext)
    {
        // Names and symbolic names resolve within the template (ARM: reference('name') and dependsOn name resources of the same template).
        var index = new TemplateResourceIndex();
        foreach (var entry in staticContext.Index.Entries)
        {
            index.Add(entry);
        }

        foreach (var loop in staticContext.Index.CopyLoops)
        {
            index.AddCopyLoop(loop);
        }

        if (info is null)
        {
            // Outer scope: the parent's parameters, variables, functions and deployment(); its stores are already bound to its context.
            return parent.Context with { Resources = index, CopyStack = [], Scope = EvaluationScope.Resource };
        }

        return new EvaluationContext
        {
            Parameters = new NestedParameters(document.ParameterValues, passed, deployment.Context),
            Variables = new LazyVariables(document.Variables, document.VariableIterations),
            References = rootContext.References,
            EntraLookup = rootContext.EntraLookup,
            Deployment = info,
            Functions = rootContext.Functions,
            Catalog = rootContext.Catalog,
            UserFunctions = document.Functions,
            Resources = index,
            Scope = EvaluationScope.Resource,
            Budget = rootContext.Budget,
        }.Bind(_evaluator);
    }

    private static bool IsUnder(Diagnostic diagnostic, string section)
    {
        return diagnostic.Path is { IsRoot: false } path && !path.Segments[0].IsIndex && string.Equals(path.Segments[0].Name, section, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every id once across the whole expansion: the first instance (main template first, then the nested templates in expansion order) wins, a later
    /// one is reported as <see cref="DiagnosticCodes.DuplicateResourceId"/> and dropped. Duplicates inside one template were already reported by its expansion.
    /// </summary>
    private static List<ResourceInstance> RemoveDuplicates(List<ResourceInstance> instances, List<Diagnostic> diagnostics)
    {
        var seen = new Dictionary<ResourceIdentifier, ResourceInstance>();
        var kept = new List<ResourceInstance>(instances.Count);
        foreach (var instance in instances)
        {
            if (seen.TryGetValue(instance.PlannedId, out var first))
            {
                string rendered = instance.IsSymbolic ? $"{instance.Type} with deploy-time name {instance.PlannedId.Name}" : $"'{instance.PlannedId.Id}'";
                diagnostics.Add(instance.Scope!.Error(DiagnosticCodes.DuplicateResourceId, $"Resource id {rendered} is declared by {Where(instance)} and by {Where(first)}; one object can be declared only once across a template and its nested deployments.", instance.Declaration.Path.Append("name")));
                continue;
            }

            seen[instance.PlannedId] = instance;
            kept.Add(instance);
        }

        return kept;
    }

    private static string Where(ResourceInstance instance)
    {
        return instance.DeploymentPath is { } path ? $"nested deployment '{path}'" : "the main template";
    }
}
