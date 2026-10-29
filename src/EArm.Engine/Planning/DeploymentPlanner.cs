using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Validation;
using EArm.Engine.Execution;
using EArm.Engine.Planning.Nested;
using EArm.Engine.Providers;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Validation;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Planning;

/// <summary>
/// Validation (core + expression validators; a missing parameter value is an error
/// here), expansion, dependency graph, then (in <see cref="PlanMode.WhatIf"/>) per batch and in parallel:
/// <c>ResolveKey</c> → <c>ReadAsync</c> → <c>Plan</c>. Nothing is written. Live objects that exist are published
/// to the resolver so later batches evaluate <c>reference()</c> against them; resources that do not exist yet
/// stay placeholders and surface as unknown (<c>(deploy-time)</c>) values in the rows that use them.
/// </summary>
public sealed class DeploymentPlanner
{
    private readonly ProviderRegistry _registry;
    private readonly IGraphClient _graph;
    private readonly PlannerOptions _options;
    private readonly ILogger _logger;
    private readonly KeyAbsenceCheck _keyAbsenceCheck;
    private readonly GraphCallRetry _retry;

    public DeploymentPlanner(ProviderRegistry registry, IGraphClient graph, PlannerOptions options, ILogger<DeploymentPlanner> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _graph = graph;
        _options = options;
        _logger = logger;
        var check = options.KeyAbsenceCheck ?? KeyAbsenceCheck.Default;
        _keyAbsenceCheck = options.ConfirmKeyAbsence ? check : check with { ExtraReads = 0 };
        _retry = new GraphCallRetry(options.RetryPolicy);
    }

    /// <summary>
    /// The offline part of planning: core and expression validation with the deployment promotions (a missing
    /// parameter value is an error; a Key Vault reference is an error for create, a warning for what-if).
    /// Returns the remaining warnings. The CLI runs this before preflight so a broken template never needs
    /// a sign-in to be reported; <see cref="PlanAsync"/> runs it again (cheap, offline).
    /// </summary>
    /// <exception cref="ValidationFailedException">The template has errors.</exception>
    public IReadOnlyList<Diagnostic> Validate(TemplateDocument document, PlanMode planMode)
    {
        ArgumentNullException.ThrowIfNull(document);
        var (diagnostics, _) = ValidateCore(document, planMode);

        // The templates of nested and linked deployments are checked here too (offline expansion), so a broken nested template is reported before any sign-in.
        if (document.Resources.Any(r => Core.Types.DeploymentResourceType.Is(r.Type)))
        {
            var expansion = Permissions.OfflineTemplateExpansion.Run(document, _options.Catalog, _options.LinkedTemplates, _options.DeploymentName);
            diagnostics.AddRange(expansion.Succeeded ? expansion.Warnings : expansion.Errors);
            ThrowIfErrors(diagnostics);
        }

        return diagnostics;
    }

    private (List<Diagnostic> Diagnostics, ExpressionAnalysis Analysis) ValidateCore(TemplateDocument document, PlanMode planMode)
    {
        var catalog = _options.Catalog;
        var diagnostics = new List<Diagnostic>(new TemplateValidator(catalog).Validate(document));
        // Static evaluation (names, ids, dependsOn) sees the real deployment name: deployment().name is -n or its default here, never the offline 'validate'.
        var analysis = new ExpressionValidator(catalog, _options.Functions, DeploymentInfo.Offline with { Name = _options.DeploymentName }).Analyze(document);
        diagnostics.AddRange(analysis.Diagnostics);
        PromoteForDeployment(document, diagnostics, analysis, planMode);
        ThrowIfErrors(diagnostics);
        return (diagnostics, analysis);
    }

    /// <exception cref="ValidationFailedException">The template has errors (validation, expansion or graph).</exception>
    /// <exception cref="UnknownProviderException">A resource type has no registered provider.</exception>
    public async Task<DeploymentPlan> PlanAsync(TemplateDocument document, DeploymentMode mode, PlanMode planMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var catalog = _options.Catalog;
        var evaluator = _options.Evaluator;
        var expandPhase = _options.Timings?.Measure("plan: expand and graph");

        // Secure parameter values are masked from every message from here on (review finding S-02).
        SecretRegistration.RegisterDocument(document);

        // 1. Validate (core + expressions), promote what deployment cannot tolerate.
        var (diagnostics, analysis) = ValidateCore(document, planMode);

        // 2. The deployment-wide evaluation context: live resolver, tenant lookups for appRole()/permissionScope()/
        //    roleTemplate() (cached per run), real deployment info, fresh lazy stores.
        var resolver = new LiveReferenceResolver { CancellationToken = cancellationToken };
        var entraLookup = new GraphEntraLookup(_graph, _options.RetryPolicy) { CancellationToken = cancellationToken };
        var index = new TemplateResourceIndex();
        foreach (var entry in analysis.Static.Index.Entries)
        {
            index.Add(entry);
        }

        foreach (var loop in analysis.Static.Index.CopyLoops)
        {
            index.AddCopyLoop(loop);
        }

        string templateHash = "sha256:" + CanonicalJson.Hash(document.Root);
        var deployment = new DeploymentInfo(_options.DeploymentName, templateHash, _options.TenantId, mode.ToString())
        {
            Tenant = _options.Tenant,
            Deployer = _options.Deployer,
            Environment = _options.Environment,
            Parameters = DeploymentInfo.SuppliedParameters(document.ParameterValues),
        };

        var context = new EvaluationContext
        {
            Parameters = new LazyParameters(document.ParameterValues),
            Variables = new LazyVariables(document.Variables, document.VariableIterations),
            References = resolver,
            EntraLookup = entraLookup,
            Deployment = deployment,
            Functions = _options.Functions,
            Catalog = catalog,
            UserFunctions = document.Functions,
            Resources = index,
            Scope = EvaluationScope.Resource,
        }.Bind(evaluator);

        // 3. Expand.
        var instances = new ResourceExpander(evaluator, catalog).Expand(analysis.Static, context, diagnostics);
        ThrowIfErrors(diagnostics);
        EngineLog.Expanded(_logger, document.Resources.Count, instances.Count);

        // 3b. The static index only knows ids computable without a copy frame; the concrete ids of the copy
        //     iterations join it now (the context shares this index), so the per-iteration reference() rescan of
        //     step 5 and every later evaluation resolve reference(format('g-{0}', copyIndex())) by name.
        foreach (var instance in instances)
        {
            if (instance.Copy is { } copy)
            {
                index.Add(new TemplateResourceIndex.Entry(instance.PlannedId, instance.IsSymbolic, instance.Declaration, copy.Name, copy.Index));
            }
        }

        // 3c. Nested and linked deployments: their templates' resources join the instances (same graph, same batches).
        var rootScope = TemplateScope.Root(document, analysis.Static, context);
        var reader = _options.LinkedTemplates ?? LinkedTemplateReader.LocalOnly;
        instances = new NestedDeploymentExpander(evaluator, catalog, _options.Functions, reader, offline: false, cancellationToken, _options.KeyVaultSecrets).Expand(rootScope, instances, diagnostics);
        ThrowIfErrors(diagnostics);

        // 4. Every type needs a provider (an unregistered type fails here); a nested deployment has none (nothing is written for it).
        _registry.RequireAll(instances.Where(i => !i.IsDeployment).Select(i => i.Type));

        // 5. Graph and batches.
        var graph = DependencyGraph.Build(instances, analysis.Static, evaluator, catalog, diagnostics, ApplyOrder);
        ThrowIfErrors(diagnostics);

        // 5a. Template-level conflicts no single row can see (IDeclarationConflictSource), before any Graph call.
        AddDeclarationConflicts(document, instances, mode, diagnostics);
        ThrowIfErrors(diagnostics);
        EngineLog.GraphBuilt(_logger, graph.Edges.Count, graph.Batches.Count);
        foreach (var batch in graph.Batches)
        {
            EngineLog.BatchContents(_logger, batch.Index, string.Join(", ", batch.Instances.Select(i => i.PlannedId.Id)));
        }

        var permissions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances.Where(i => !i.IsDeployment))
        {
            foreach (var permission in _registry.Get(instance.Type).RequiredPermissions(instance))
            {
                permissions.Add(permission);
            }
        }

        var planOptions = new PlanOptions
        {
            Mode = mode,
            PlanMode = planMode,
            AllowCaEnforce = _options.AllowCaEnforce,
            OnSoftDeletedConflict = _options.OnSoftDeletedConflict,
        };

        var plan = new DeploymentPlan(
            document,
            analysis.Static,
            _options.DeploymentName,
            mode,
            planMode,
            _options.TenantId,
            templateHash,
            instances,
            graph,
            diagnostics.Where(d => !d.IsError).ToList(),
            permissions,
            planOptions,
            context,
            resolver,
            index,
            evaluator,
            catalog,
            _registry,
            Math.Max(1, _options.Parallelism),
            _options.RetryPolicy,
            _options.EdgeKinds,
            _options.SignedInPrincipalId,
            _options.AppOnlyToken);

        // From here on providers may be used: existing-resource lookups go through them.
        resolver.ExistingLookup = LookupExistingAsync;
        expandPhase?.Dispose();

        // 6. What-if: read and plan every batch; nothing is written. Then the edge pass rows
        //    (read-only) and the orphan report rows (read-only; nothing is ever deleted).
        if (planMode == PlanMode.WhatIf)
        {
            await new WhatIfReadPass(_graph, _options.RetryPolicy, _logger, _keyAbsenceCheck, _options.Timings).RunAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        plan.FinishChanges();
        return plan;
    }

    private static void PromoteForDeployment(TemplateDocument document, List<Diagnostic> diagnostics, ExpressionAnalysis analysis, PlanMode planMode)
    {
        for (int i = 0; i < diagnostics.Count; i++)
        {
            var diagnostic = diagnostics[i];
            if (diagnostic.Code == DiagnosticCodes.MissingParameterValue && !diagnostic.IsError)
            {
                diagnostics[i] = diagnostic with { Severity = DiagnosticSeverity.Error, Message = diagnostic.Message + " Supply it with -p name=value or a parameter file." };
            }
        }

        foreach (var name in analysis.Static.Parameters.PendingReferenceNames)
        {
            var definition = document.Parameters.TryGetValue(name, out var found) ? found : null;
            var path = definition?.Path;
            string message = $"Parameter '{name}' is supplied as a Key Vault reference that has not been resolved. The CLI resolves it from the vault before planning (KeyVaultSecretResolver); otherwise pass the value with -p {name}=<value>.";
            diagnostics.Add(planMode == PlanMode.Create
                ? Diagnostic.Error(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, message, path, document.LocationOf(path), document.SourcePath)
                : Diagnostic.Warning(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, message + " In what-if its value is shown as a deploy-time placeholder.", path, document.LocationOf(path), document.SourcePath));
        }

        diagnostics.Sort(DiagnosticOrder.Instance);
    }

    /// <summary>The instances the provider of <paramref name="instance"/> applies after it (<see cref="IApplyOrderSource"/>); none for most types.</summary>
    private IEnumerable<ResourceInstance> ApplyOrder(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared)
    {
        return !instance.IsDeployment && _registry.Get(instance.Type) is IApplyOrderSource source ? source.AppliesBefore(instance, declared) : [];
    }

    /// <summary>The conflicts the providers that implement <see cref="IDeclarationConflictSource"/> find among the expanded instances, as errors (or warnings, by the conflict's severity).</summary>
    private void AddDeclarationConflicts(TemplateDocument document, IReadOnlyList<ResourceInstance> instances, DeploymentMode mode, List<Diagnostic> diagnostics)
    {
        foreach (var instance in instances)
        {
            if (instance.IsIgnored || instance.IsDeployment || _registry.Get(instance.Type) is not IDeclarationConflictSource source)
            {
                continue;
            }

            foreach (var conflict in source.DescribeConflicts(instance, instances, mode))
            {
                // A conflict found in a nested template points into that template (its file, or its place in the parent's file).
                bool warning = conflict.Severity == DiagnosticSeverity.Warning;
                diagnostics.Add((instance.Scope, warning) switch
                {
                    ({ IsRoot: false } scope, true) => scope.Warning(conflict.Code, conflict.Message, conflict.Path),
                    ({ IsRoot: false } scope, false) => scope.Error(conflict.Code, conflict.Message, conflict.Path),
                    (_, true) => Diagnostic.Warning(conflict.Code, conflict.Message, conflict.Path, document.LocationOf(conflict.Path), document.SourcePath),
                    _ => Diagnostic.Error(conflict.Code, conflict.Message, conflict.Path, document.LocationOf(conflict.Path), document.SourcePath),
                });
            }
        }
    }

    private static void ThrowIfErrors(List<Diagnostic> diagnostics)
    {
        if (diagnostics.Any(d => d.IsError))
        {
            diagnostics.Sort(DiagnosticOrder.Instance);
            throw new ValidationFailedException(diagnostics.ToList());
        }
    }

    /// <summary>The message of <see cref="EngineDiagnosticCodes.ExistingResourceNotFound"/>.</summary>
    internal static string ExistingNotFound(ResourceInstance instance)
    {
        return $"{instance.Describe()} is declared \"existing\": true but does not exist; an existing resource is only read. Create it first, or remove \"existing\" to let the template create it.";
    }

    /// <summary>The refusal text when the old object of a recreate may not be deleted (nothing is created or changed in that case).</summary>
    internal static string RecreateRefusal(ResourceInstance instance, string refusal)
    {
        return $"{instance.Describe()}: an immutable property changed, so the object must be deleted and created again, but {refusal} Nothing was changed.";
    }

    // ---- existing-resource lookups ----

    private async Task<ExistingLookupResult> LookupExistingAsync(ResourceIdentifier id, string apiVersion, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(id.Type, out var provider))
        {
            EngineLog.ExistingLookupUnresolved(_logger, id.Id, apiVersion, "no provider is registered for the type");
            return ExistingLookupResult.Unresolvable($"no provider is registered for '{id.Type}'");
        }

        if (provider is not IExistingResourceLookup lookup)
        {
            EngineLog.ExistingLookupUnresolved(_logger, id.Id, apiVersion, "the provider does not support existing-resource lookups");
            return ExistingLookupResult.Unresolvable($"the provider for '{id.Type}' does not support existing-resource lookups");
        }

        try
        {
            var found = await _retry.WithRetryAsync(ct => lookup.ReadExistingAsync(id, apiVersion, _graph, ct), RetryHint.Default, cancellationToken).ConfigureAwait(false);
            return ExistingLookupResult.Of(found);
        }
        catch (ProviderException ex)
        {
            EngineLog.ExistingLookupUnresolved(_logger, id.Id, apiVersion, ex.Message);
            return ExistingLookupResult.Unresolvable(ex.Message);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException)
        {
            EngineLog.ExistingLookupUnresolved(_logger, id.Id, apiVersion, ex.Message);
            return ExistingLookupResult.Unresolvable(ex.Message);
        }
    }
}
