using System;
using System.Collections.Generic;
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
        private readonly ResourceKeyEvaluator _keys;
        private EvaluationContext _context;

        public Run(StaticEvaluator owner, TemplateDocument document)
        {
            _owner = owner;
            _document = document;
            _parameters = new LazyParameters(document.ParameterValues);
            _variables = new LazyVariables(document.Variables, document.VariableIterations);
            _keys = new ResourceKeyEvaluator(owner._evaluator, document, _diagnostics);
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
                    DependsOn = new DependsOnResolver(_owner._evaluator, _owner._catalog, _document, _context, _diagnostics).Resolve(resource),
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
                    _variables.GetShared(name);
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
                long expanded = 0;
                foreach (var declaration in _document.Resources)
                {
                    AddCopyLoop(next, declaration);
                    var resolved = EvaluateResource(declaration, strict: false);
                    if (resolved.Id is { } id)
                    {
                        next.Add(new TemplateResourceIndex.Entry(id, resolved.NameKind == ResolvedNameKind.Symbolic, declaration, CopyName(declaration)));
                    }
                    else if (resolved.NameKind == ResolvedNameKind.CopyDependent)
                    {
                        expanded += AddCopyInstances(next, declaration, expanded);
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

        /// <summary>
        /// Indexes every instance of a copy loop whose names depend on <c>copyIndex()</c>: the count is evaluated from parameters and
        /// variables and the name once per index with a concrete frame, which is what lets <c>reference()</c> name one instance from
        /// anywhere in the template (ARM: a resource in a loop is referenced by its evaluated name). A count that is not known yet
        /// leaves the loop pending (<see cref="TemplateResourceIndex.PendingCopyLoops"/>); failures are reported by the strict pass
        /// and by expansion. Returns the number of instances indexed; nothing is indexed past <see cref="EvaluationLimits.MaxInstances"/>.
        /// </summary>
        private long AddCopyInstances(TemplateResourceIndex index, ResourceDeclaration declaration, long alreadyExpanded)
        {
            var definition = _owner._catalog.Find(declaration.Type);
            if (CopyName(declaration) is not { } copyName || declaration.Copy?.Count is null || definition is null || declaration.Name is null)
            {
                return 0;
            }

            var context = _context.ForScope(EvaluationScope.Resource);
            long? count;
            UnknownValue? unknown;
            try
            {
                count = CopyExpansion.EvaluateCount(_owner._evaluator, declaration.Copy.Count, context, $"copy '{copyName}'", out unknown);
            }
            catch (ExpressionException)
            {
                return 0;
            }

            if (count is null)
            {
                index.AddPendingCopyLoop(new TemplateResourceIndex.PendingCopyLoop(copyName, declaration, unknown!.Origins, unknown.Symbol));
                return 0;
            }

            if (alreadyExpanded + count.Value > EvaluationLimits.MaxInstances)
            {
                return 0;
            }

            var namePath = declaration.Path.Append("name");
            for (long i = 0; i < count.Value; i++)
            {
                var iteration = context.PushCopy(new CopyFrame(copyName, i, count.Value));
                var start = new ResolvedResource { Declaration = declaration, NameKind = ResolvedNameKind.Invalid, CopyName = copyName };
                var resolved = _keys.EvaluateName(declaration, definition, iteration, namePath, strict: false, start);
                if (resolved.Id is { } id)
                {
                    index.Add(new TemplateResourceIndex.Entry(id, resolved.NameKind == ResolvedNameKind.Symbolic, declaration, copyName, i));
                }
            }

            return count.Value;
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
                result = _keys.EvaluateName(declaration, definition, context, namePath, strict, result);
            }

            if (strict)
            {
                result = _keys.EvaluateCondition(declaration, context, result);
                result = _keys.EvaluateCopy(declaration, context, result);
            }

            return result;
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
                    if (output.Copy is { } outputCopy)
                    {
                        // The count first, so its failure is located at copy.count and the input's at copy.input.
                        valuePath = outputCopy.Path.Append("count");
                        CopyExpansion.EvaluateCount(_owner._evaluator, outputCopy.Count, context, $"output '{output.Name}' copy", out _);
                        valuePath = outputCopy.Path.Append("input");
                    }

                    value = CopyExpansion.EvaluateOutput(_owner._evaluator, output, context);
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

                var scanContext = new StaticContext(_document, _context, _parameters, _variables, _context.Resources, [], new Dictionary<string, ResolvedOutput>(), []);
                var scan = output.Copy is { } copy
                    ? ReferenceScanner.ScanOutputCopy(_owner._evaluator, copy, output.Name, scanContext, context)
                    : ReferenceScanner.Scan(_owner._evaluator, output.Value, scanContext, context, valuePath);
                foreach (var diagnostic in scan.Diagnostics)
                {
                    // What the evaluation above already reported for this output (an invalid loop count, say) is not repeated.
                    if (!_diagnostics.Exists(d => d.Code == diagnostic.Code && d.Path is { } reported && reported.StartsWith(output.Path) && diagnostic.Path is { } found && found.StartsWith(reported)))
                    {
                        _diagnostics.Add(diagnostic);
                    }
                }

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
