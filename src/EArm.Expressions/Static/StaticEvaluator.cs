using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Functions;
using EArm.Expressions.Values;

namespace EArm.Expressions.Static;

/// <summary>
/// Evaluates, offline, everything of a template that can be evaluated before deployment (
/// phases 2-4): parameter defaults (non-deterministic functions permitted), variables (lazily), and for every
/// resource its <c>name</c>, <c>apiVersion</c> (must be literal), <c>condition</c>, <c>copy.count</c> and
/// <c>dependsOn</c> entries, plus the <c>reference()</c> occurrences inside <c>properties</c> and the outputs.
/// <para>
/// Resource names are evaluated to a fixpoint: a name may contain <c>reference()</c> of another template resource
/// (the sample's service principal is named <c>[reference(variables('apiName')).appId]</c>), which yields a symbolic
/// id; the index of known ids is rebuilt and the names re-evaluated until it stops changing, then a final strict
/// pass reports the remaining errors.
/// </para>
/// </summary>
public sealed class StaticEvaluator
{
    private readonly ExpressionEvaluator _evaluator;
    private readonly FunctionRegistry _functions;
    private readonly ResourceTypeCatalog _catalog;
    private readonly DeploymentInfo _deployment;
    private readonly IReferenceResolver _resolver;

    public StaticEvaluator()
        : this(null, null, null, null, null)
    {
    }

    public StaticEvaluator(ExpressionEvaluator? evaluator, FunctionRegistry? functions, ResourceTypeCatalog? catalog, DeploymentInfo? deployment, IReferenceResolver? resolver)
    {
        _evaluator = evaluator ?? ExpressionEvaluator.Default;
        _functions = functions ?? FunctionRegistry.Default;
        _catalog = catalog ?? ResourceTypeCatalog.Default;
        _deployment = deployment ?? DeploymentInfo.Offline;
        _resolver = resolver ?? StaticReferenceResolver.Instance;
    }

    public StaticContext Evaluate(TemplateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var run = new Run(this, document);
        return run.Execute();
    }

    private sealed class Run
    {
        private readonly StaticEvaluator _owner;
        private readonly TemplateDocument _document;
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly LazyParameters _parameters;
        private readonly LazyVariables _variables;
        private EvaluationContext _context;

        public Run(StaticEvaluator owner, TemplateDocument document)
        {
            _owner = owner;
            _document = document;
            _parameters = new LazyParameters(document.ParameterValues);
            _variables = new LazyVariables(document.Variables);
            _context = new EvaluationContext
            {
                Parameters = _parameters,
                Variables = _variables,
                References = owner._resolver,
                Deployment = owner._deployment,
                Functions = owner._functions,
                Catalog = owner._catalog,
                UserFunctions = document.Functions,
                Resources = BuildInitialIndex(document),
                Scope = EvaluationScope.Resource,
            }.Bind(owner._evaluator);
        }

        public StaticContext Execute()
        {
            EvaluateParameters();
            EvaluateVariables();

            var index = ResolveNamesToFixpoint();
            _context = _context with { Resources = index };

            var resources = new List<ResolvedResource>();
            foreach (var declaration in _document.Resources)
            {
                resources.Add(EvaluateResource(declaration, strict: true));
            }

            ReportDuplicateIds(resources);
            ReportInstanceLimit(resources);

            var withDependencies = new List<ResolvedResource>();
            foreach (var resource in resources)
            {
                withDependencies.Add(resource with
                {
                    DependsOn = EvaluateDependsOn(resource),
                    References = ScanProperties(resource),
                });
            }

            var outputs = EvaluateOutputs();

            // Defence in depth (see SecureTextMask): every message above was built from redacted forms; this backstop
            // guarantees the published list cannot carry a secure parameter value even if a path were missed.
            SecureTextMask.Apply(_parameters, _diagnostics);
            return new StaticContext(_document, _context, _parameters, _variables, index, withDependencies, outputs, _diagnostics);
        }

        // ---- parameters and variables ----

        private void EvaluateParameters()
        {
            foreach (var name in _document.ParameterValues.Keys)
            {
                var definition = _document.ParameterValues[name].Definition;
                try
                {
                    _parameters.Get(name);
                }
                catch (ExpressionException ex)
                {
                    Report(ex, definition.Path.Append("defaultValue"));
                }
            }
        }

        private void EvaluateVariables()
        {
            foreach (var name in _variables.Names)
            {
                try
                {
                    _variables.Get(name);
                }
                catch (ExpressionException ex)
                {
                    Report(ex, JsonPath.Of("variables", name));
                }
            }
        }

        // ---- resource names ----

        private static TemplateResourceIndex BuildInitialIndex(TemplateDocument document)
        {
            var index = new TemplateResourceIndex();
            foreach (var declaration in document.Resources)
            {
                AddCopyLoop(index, declaration);
                if (declaration.StaticId is { } id)
                {
                    index.Add(new TemplateResourceIndex.Entry(id, false, declaration, CopyName(declaration)));
                }
            }

            return index;
        }

        private TemplateResourceIndex ResolveNamesToFixpoint()
        {
            var index = _context.Resources;
            int maxPasses = _document.Resources.Count + 2;
            for (int pass = 0; pass < maxPasses; pass++)
            {
                var next = new TemplateResourceIndex();
                foreach (var declaration in _document.Resources)
                {
                    AddCopyLoop(next, declaration);
                    var resolved = EvaluateResource(declaration, strict: false);
                    if (resolved.Id is { } id)
                    {
                        next.Add(new TemplateResourceIndex.Entry(id, resolved.NameKind == ResolvedNameKind.Symbolic, declaration, CopyName(declaration)));
                    }
                }

                bool stable = next.Ids.SetEquals(index.Ids);
                index = next;
                _context = _context with { Resources = index };
                if (stable)
                {
                    break;
                }
            }

            return index;
        }

        private static string? CopyName(ResourceDeclaration declaration)
        {
            return declaration.Copy is { } copy ? TemplateJson.LiteralString(copy.Name) : null;
        }

        /// <summary>
        /// Registers the declaration's copy loop by its literal name, so <c>dependsOn: ["loopName"]</c> resolves even
        /// when the iteration names depend on <c>copyIndex()</c> and no iteration has an id before expansion.
        /// </summary>
        private static void AddCopyLoop(TemplateResourceIndex index, ResourceDeclaration declaration)
        {
            if (CopyName(declaration) is { } copyName)
            {
                index.AddCopyLoop(new TemplateResourceIndex.CopyLoop(copyName, declaration));
            }
        }

        /// <summary>Evaluates name, apiVersion, condition and copy.count; in strict mode failures are reported.</summary>
        private ResolvedResource EvaluateResource(ResourceDeclaration declaration, bool strict)
        {
            var context = StaticContext.ContextFor(_context, declaration);
            var namePath = declaration.Path.Append("name");
            var result = new ResolvedResource { Declaration = declaration, NameKind = ResolvedNameKind.Invalid, CopyName = CopyName(declaration) };

            if (strict && declaration.ApiVersion is not null && ExpressionSyntax.IsExpression(declaration.ApiVersion))
            {
                Error(DiagnosticCodes.ApiVersionMustBeLiteral, $"apiVersion must be a literal version string, not an expression ('{declaration.ApiVersion}').", declaration.Path.Append("apiVersion"));
            }

            var definition = _owner._catalog.Find(declaration.Type);
            if (declaration.Name is not null && definition is not null)
            {
                result = EvaluateName(declaration, definition, context, namePath, strict, result);
            }

            if (strict)
            {
                result = EvaluateCondition(declaration, context, result);
                result = EvaluateCopy(declaration, context, result);
            }

            return result;
        }

        private ResolvedResource EvaluateName(ResourceDeclaration declaration, ResourceTypeDefinition definition, EvaluationContext context, JsonPath namePath, bool strict, ResolvedResource result)
        {
            JsonNode? value;
            try
            {
                value = _owner._evaluator.EvaluateString(declaration.Name!, context);
            }
            catch (ExpressionException ex)
            {
                if (strict)
                {
                    Report(ex, namePath);
                }

                return result;
            }

            if (UnknownJson.ContainsTaint(value))
            {
                if (strict)
                {
                    Error(DiagnosticCodes.SecureValueInResourceKey, "A secure value flows into the resource name; names are written to state and logs and cannot be secret.", namePath);
                }

                return result with { NameValue = value };
            }

            if (UnknownJson.IsUnknown(value, out var unknown))
            {
                if ((unknown.Origins & UnknownOrigins.Copy) != 0)
                {
                    return result with { NameKind = ResolvedNameKind.CopyDependent, NameValue = value };
                }

                var symbolic = new ResourceIdentifier(definition.Type, [unknown.Symbol]);
                return result with { NameKind = ResolvedNameKind.Symbolic, Id = symbolic, NameValue = value };
            }

            if (!JsonValues.TryGetString(value, out var name))
            {
                if (strict)
                {
                    Error(DiagnosticCodes.ResourceNameNotString, $"The resource name must evaluate to a string, got {JsonValues.Describe(value)}.", namePath);
                }

                return result with { NameValue = value };
            }

            var segments = name.Split('/');
            if (segments.Length != definition.Depth || segments.Any(string.IsNullOrEmpty))
            {
                if (strict)
                {
                    string expected = definition.Depth == 1 ? "a single segment without '/'" : $"{definition.Depth} segments ('parent/child')";
                    Error(DiagnosticCodes.InvalidResourceName, $"Name '{name}' (evaluated) is not valid for type '{definition.Type}': expected {expected}.", namePath);
                }

                return result with { NameValue = value };
            }

            // A literal name was checked by the core validator; a computed one is checked here, with the same rules.
            if (ExpressionSyntax.IsExpression(declaration.Name!) && ResourceNameRules.Validate(definition, name) is { } nameProblem)
            {
                if (strict)
                {
                    Error(DiagnosticCodes.InvalidResourceName, nameProblem, namePath);
                }

                return result with { NameValue = value };
            }

            return result with
            {
                NameKind = ResolvedNameKind.Static,
                Id = new ResourceIdentifier(definition.Type, segments),
                Name = name,
                NameValue = value,
            };
        }

        private ResolvedResource EvaluateCondition(ResourceDeclaration declaration, EvaluationContext context, ResolvedResource result)
        {
            if (declaration.Condition is null)
            {
                return result;
            }

            var path = declaration.Path.Append("condition");
            JsonNode? value;
            try
            {
                value = _owner._evaluator.EvaluateTree(declaration.Condition, context);
            }
            catch (ExpressionException ex)
            {
                Report(ex, path);
                return result;
            }

            if (UnknownJson.ContainsTaint(value))
            {
                Error(DiagnosticCodes.SecureValueInResourceKey, "A secure value flows into the resource condition; conditions are logged and cannot be secret.", path);
                return result;
            }

            if (UnknownJson.IsUnknown(value))
            {
                return result with { ConditionIsDeployTime = true };
            }

            if (JsonValues.TryGetBool(value, out bool condition))
            {
                return result with { Condition = condition };
            }

            Error(DiagnosticCodes.ConditionNotBoolean, $"condition must evaluate to a boolean, got {JsonValues.Describe(value)}.", path);
            return result;
        }

        private ResolvedResource EvaluateCopy(ResourceDeclaration declaration, EvaluationContext context, ResolvedResource result)
        {
            if (declaration.Copy is not { } copy)
            {
                return result;
            }

            var path = copy.Path.Append("count");
            if (copy.Count is null)
            {
                return result;
            }

            JsonNode? value;
            try
            {
                value = _owner._evaluator.EvaluateTree(copy.Count, context);
            }
            catch (ExpressionException ex)
            {
                Report(ex, path);
                return result;
            }

            if (UnknownJson.ContainsTaint(value))
            {
                Error(DiagnosticCodes.SecureValueInResourceKey, "A secure value flows into copy.count; the loop count is logged and cannot be secret.", path);
                return result;
            }

            if (UnknownJson.IsUnknown(value, out var unknown))
            {
                if ((unknown.Origins & UnknownOrigins.Copy) != 0)
                {
                    Error(DiagnosticCodes.InvalidCopy, "copy.count cannot depend on copyIndex().", path);
                }

                return result;
            }

            if (!JsonValues.TryGetWholeNumber(value, out long count) || count < 0)
            {
                Error(DiagnosticCodes.InvalidCopy, $"copy.count must evaluate to a non-negative integer, got {JsonValues.Render(value)}.", path);
                return result;
            }

            if (count > EvaluationLimits.MaxCopyCount)
            {
                Error(DiagnosticCodes.ExpansionLimitExceeded, $"copy.count is {count}, more than the limit of {EvaluationLimits.MaxCopyCount} iterations per resource (the ARM limit); nothing is expanded.", path);
                return result;
            }

            return result with { CopyCount = count };
        }

        /// <summary>The expansion of the whole template may not exceed <see cref="EvaluationLimits.MaxInstances"/> instances; reported before anything is expanded.</summary>
        private void ReportInstanceLimit(IReadOnlyList<ResolvedResource> resources)
        {
            long total = 0;
            foreach (var resource in resources)
            {
                total += resource.Declaration.Copy is null ? 1 : resource.CopyCount ?? 0;
                if (total > EvaluationLimits.MaxInstances)
                {
                    Error(DiagnosticCodes.ExpansionLimitExceeded, $"The template expands to more than {EvaluationLimits.MaxInstances} resource instances (all copy loops together); nothing is expanded.", resource.Declaration.Path);
                    return;
                }
            }
        }

        private void ReportDuplicateIds(IReadOnlyList<ResolvedResource> resources)
        {
            var seen = new Dictionary<ResourceIdentifier, ResolvedResource>();
            foreach (var resource in resources)
            {
                if (resource.Id is not { } id)
                {
                    continue;
                }

                if (!seen.TryGetValue(id, out var first))
                {
                    seen[id] = resource;
                    continue;
                }

                // Pairs of literal names are already reported by the core validator.
                if (first.Declaration.StaticId is not null && resource.Declaration.StaticId is not null)
                {
                    continue;
                }

                string rendered = resource.NameKind == ResolvedNameKind.Symbolic ? $"{id.Type} with deploy-time name {id.Name}" : $"'{id.Id}'";
                Error(DiagnosticCodes.DuplicateResourceId, $"Resource id {rendered} is declared more than once after evaluating names (also at {first.Declaration.Path}).", resource.Declaration.Path.Append("name"));
            }
        }

        // ---- dependsOn ----

        private IReadOnlyList<ResolvedDependency> EvaluateDependsOn(ResolvedResource resource)
        {
            var declaration = resource.Declaration;
            var context = StaticContext.ContextFor(_context, declaration);
            var dependencies = new List<ResolvedDependency>();

            for (int i = 0; i < declaration.DependsOn.Count; i++)
            {
                dependencies.Add(ResolveDependency(declaration.DependsOn[i], DependencySource.Explicit, context, declaration.Path.Append("dependsOn").Append(i)));
            }

            foreach (var inferred in declaration.InferredDependsOn)
            {
                dependencies.Add(ResolveDependency(inferred, DependencySource.Inferred, context, declaration.Path));
            }

            return dependencies;
        }

        private ResolvedDependency ResolveDependency(string text, DependencySource source, EvaluationContext context, JsonPath path)
        {
            var unresolved = new ResolvedDependency(text, source, null, null, false);
            JsonNode? value;
            try
            {
                value = _owner._evaluator.EvaluateString(text, context);
            }
            catch (ExpressionException ex)
            {
                Report(ex, path);
                return unresolved;
            }

            if (UnknownJson.ContainsTaint(value))
            {
                Error(DiagnosticCodes.SecureValueInResourceKey, "A secure value flows into dependsOn; dependencies are logged and cannot be secret.", path);
                return unresolved;
            }

            if (UnknownJson.IsUnknown(value, out var unknown))
            {
                if (unknown.ResourceId is { } symbolic && _context.Resources.Contains(symbolic))
                {
                    return unresolved with { Target = symbolic };
                }

                if ((unknown.Origins & UnknownOrigins.Copy) != 0)
                {
                    return unresolved with { Deferred = true };
                }

                if (unknown.Origins == UnknownOrigins.Parameter)
                {
                    // Only a parameter value is missing offline: an artefact of validating without values, deferred
                    // like a copy-dependent entry rather than reported as an authoring error.
                    Info(DiagnosticCodes.ReferenceDependsOnMissingParameter, $"dependsOn entry '{text}' depends on a parameter that has no value offline; it is resolved once the value is supplied. Deploy-time value: {unknown.Symbol}.", path);
                    return unresolved with { Deferred = true };
                }

                Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{text}' does not resolve to a resource of this template (its value is only known at deployment: {unknown.Symbol}).", path);
                return unresolved;
            }

            if (!JsonValues.TryGetString(value, out var target))
            {
                Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{text}' must evaluate to a string, got {JsonValues.Describe(value)}.", path);
                return unresolved;
            }

            var index = _context.Resources;
            if (target.StartsWith(ResourceIdentifier.ProvidersPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!_owner._catalog.TryParseResourceId(target, out var id) || id is null)
                {
                    Error(DiagnosticCodes.InvalidResourceId, $"dependsOn entry '{target}' is not a valid resource id.", path);
                    return unresolved;
                }

                if (!index.Contains(id))
                {
                    Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' does not name a resource of this template.", path);
                    return unresolved;
                }

                return unresolved with { Target = id };
            }

            if (DeploymentFunctions.TryParseTypeAndName(_owner._catalog, target, out var typed))
            {
                if (!index.Contains(typed))
                {
                    Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' does not name a resource of this template.", path);
                    return unresolved;
                }

                return unresolved with { Target = typed };
            }

            var byName = index.FindByName(target);
            if (byName.Count == 1)
            {
                return unresolved with { Target = byName[0].Id };
            }

            if (byName.Count > 1)
            {
                Error(DiagnosticCodes.AmbiguousDependsOn, $"dependsOn entry '{target}' names {byName.Count} resources ({string.Join(", ", byName.Select(e => e.Id.Type))}); use a resource id or 'type/name'.", path);
                return unresolved;
            }

            var byCopy = index.FindByCopyName(target);
            if (byCopy.Count >= 1)
            {
                return unresolved with { CopyName = byCopy[0].CopyName };
            }

            if (index.TryFindCopyLoop(target, out var loop))
            {
                // The loop's iterations have no ids yet (names depend on copyIndex()); the engine expands the loop
                // and adds an edge to every iteration (DependencyKind.CopyLoop).
                return unresolved with { CopyName = loop.Name };
            }

            Error(DiagnosticCodes.DependsOnTargetNotFound, $"dependsOn entry '{target}' does not name a resource of this template (expected a resource id, 'type/name', a unique resource name, or a copy loop name).", path);
            return unresolved;
        }

        // ---- properties and outputs ----

        private IReadOnlyList<ReferenceOccurrence> ScanProperties(ResolvedResource resource)
        {
            var declaration = resource.Declaration;
            if (declaration.Properties is null)
            {
                return [];
            }

            var context = StaticContext.ContextFor(_context, declaration);
            var staticContext = new StaticContext(_document, _context, _parameters, _variables, _context.Resources, [], new Dictionary<string, ResolvedOutput>(), []);
            var scan = ReferenceScanner.Scan(_owner._evaluator, declaration.Properties, staticContext, context, declaration.Path.Append("properties"));
            _diagnostics.AddRange(scan.Diagnostics);
            return scan.Occurrences;
        }

        private IReadOnlyDictionary<string, ResolvedOutput> EvaluateOutputs()
        {
            var outputs = new Dictionary<string, ResolvedOutput>(StringComparer.OrdinalIgnoreCase);
            var context = _context.ForScope(EvaluationScope.Output);
            foreach (var output in _document.Outputs.Values)
            {
                bool? condition = null;
                if (output.Condition is not null)
                {
                    try
                    {
                        var conditionValue = _owner._evaluator.EvaluateTree(output.Condition, context);
                        if (JsonValues.TryGetBool(conditionValue, out bool b))
                        {
                            condition = b;
                        }
                        else if (!UnknownJson.IsUnknown(conditionValue))
                        {
                            Error(DiagnosticCodes.ConditionNotBoolean, $"Output '{output.Name}': condition must evaluate to a boolean, got {JsonValues.Describe(conditionValue)}.", output.Path.Append("condition"));
                        }
                    }
                    catch (ExpressionException ex)
                    {
                        Report(ex, output.Path.Append("condition"));
                    }
                }

                JsonNode? value = null;
                var valuePath = output.Path.Append("value");
                try
                {
                    value = _owner._evaluator.EvaluateTree(output.Value, context);
                }
                catch (ExpressionException ex)
                {
                    Report(ex, valuePath);
                }

                bool tainted = UnknownJson.ContainsTaint(value);
                if (tainted && !output.IsSecure)
                {
                    Error(DiagnosticCodes.SecureValueInPlainOutput, $"Output '{output.Name}' receives a secure value but is declared as '{output.Type}'; declare it as securestring or secureObject.", valuePath);
                }

                var scan = ReferenceScanner.Scan(_owner._evaluator, output.Value, new StaticContext(_document, _context, _parameters, _variables, _context.Resources, [], new Dictionary<string, ResolvedOutput>(), []), context, valuePath);
                _diagnostics.AddRange(scan.Diagnostics);

                outputs[output.Name] = new ResolvedOutput(output, value, condition, tainted);
            }

            return outputs;
        }

        // ---- helpers ----

        private void Report(ExpressionException exception, JsonPath path)
        {
            Error(exception.Code, exception.MessageWithOffset, path);
        }

        private void Error(string code, string message, JsonPath path)
        {
            _diagnostics.Add(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
        }

        private void Info(string code, string message, JsonPath path)
        {
            _diagnostics.Add(Diagnostic.Info(code, message, path, _document.LocationOf(path), _document.SourcePath));
        }
    }
}
