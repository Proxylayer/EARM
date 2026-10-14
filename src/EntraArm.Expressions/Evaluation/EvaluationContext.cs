using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Core.Types;
using EntraArm.Expressions.Functions;

namespace EntraArm.Expressions.Evaluation;

/// <summary>Where an expression sits in the template; decides which functions are legal (plan section 1.5 phases).</summary>
public enum EvaluationScope
{
    /// <summary>A parameter <c>defaultValue</c>: <c>utcNow()</c>/<c>newGuid()</c> allowed; <c>variables()</c> and <c>reference()</c> not.</summary>
    ParameterDefault,

    /// <summary>A variable: <c>reference(id, apiVersion)</c> (existing lookup) allowed; same-template <c>reference()</c> not.</summary>
    Variable,

    /// <summary>
    /// A user-defined function body: no <c>reference()</c>, no <c>variables()</c>, no other user-defined function;
    /// <c>parameters()</c> names the function's own parameters, bound in <see cref="EvaluationContext.Parameters"/>.
    /// </summary>
    UserFunction,

    /// <summary>A resource (<c>name</c>, <c>condition</c>, <c>copy</c>, <c>dependsOn</c>, <c>properties</c>).</summary>
    Resource,

    Output,
}

/// <summary>
/// One active <c>copy</c> loop. <see cref="Index"/> is <c>-1</c> while the loop is known but the iteration is not
/// (static evaluation), in which case <c>copyIndex()</c> yields an unknown value.
/// </summary>
public sealed record CopyFrame(string Name, long Index, long? Count = null)
{
    public bool IsConcrete => Index >= 0;

    public static CopyFrame Unresolved(string name, long? count = null)
    {
        return new CopyFrame(name, -1, count);
    }
}

/// <summary>
/// Everything an expression can observe while being evaluated. Immutable: derive scoped copies with <c>with</c>
/// or the helpers. <see cref="Variables"/> and a <see cref="LazyParameters"/> instance used as
/// <see cref="Parameters"/> evaluate on demand in the context they were bound to (see <see cref="Bind"/>).
/// </summary>
public sealed record EvaluationContext
{
    /// <summary>
    /// Parameter values keyed by name (case-insensitive); secure values are tainted leaves. Inside a user-defined
    /// function (<see cref="EvaluationScope.UserFunction"/>) these are the function's own parameters, bound from the
    /// call's arguments.
    /// </summary>
    public required IReadOnlyDictionary<string, JsonNode?> Parameters { get; init; }

    public LazyVariables Variables { get; init; } = LazyVariables.Empty;

    /// <summary>Active copy loops, innermost on top.</summary>
    public ImmutableStack<CopyFrame> CopyStack { get; init; } = [];

    /// <summary>
    /// The parameters of the enclosing lambdas, bound to the values of the current element (innermost binding wins);
    /// case-insensitive names. Read by <c>lambdaVariables()</c>; empty outside a lambda body.
    /// </summary>
    public ImmutableDictionary<string, JsonNode?> LambdaScope { get; init; } = ImmutableDictionary.Create<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);

    public IReferenceResolver References { get; init; } = StaticReferenceResolver.Instance;

    /// <summary>Resolves <c>appRole()</c>/<c>permissionScope()</c>/<c>roleTemplate()</c> against the tenant; placeholders offline.</summary>
    public IEntraLookup EntraLookup { get; init; } = PlaceholderEntraLookup.Instance;

    public DeploymentInfo Deployment { get; init; } = DeploymentInfo.Offline;

    public FunctionRegistry Functions { get; init; } = FunctionRegistry.Default;

    /// <summary>The resources declared in the template, for bare-name <c>reference()</c> and same-template checks.</summary>
    public TemplateResourceIndex Resources { get; init; } = TemplateResourceIndex.Empty;

    /// <summary>The template's <c>functions</c> section (user-defined functions).</summary>
    public IReadOnlyList<UserFunctionNamespace> UserFunctions { get; init; } = [];

    /// <summary>The qualified name (<c>ns.fn</c>) of the user-defined function whose body is being evaluated; <c>null</c> elsewhere.</summary>
    public string? UserFunction { get; init; }

    public ResourceTypeCatalog Catalog { get; init; } = ResourceTypeCatalog.Default;

    public EvaluationScope Scope { get; init; } = EvaluationScope.Resource;

    /// <summary><c>utcNow()</c> and <c>newGuid()</c> are only legal in parameter defaults.</summary>
    public bool AllowNonDeterministic => Scope == EvaluationScope.ParameterDefault;

    /// <summary>The innermost copy loop, or <c>null</c>.</summary>
    public CopyFrame? CurrentCopy => CopyStack.IsEmpty ? null : CopyStack.Peek();

    public EvaluationContext ForScope(EvaluationScope scope)
    {
        return Scope == scope ? this : this with { Scope = scope };
    }

    public EvaluationContext PushCopy(CopyFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return this with { CopyStack = CopyStack.Push(frame) };
    }

    /// <summary>The context used for parameter defaults and variables: no copy loop, no lambda scope.</summary>
    public EvaluationContext Base => this with { CopyStack = [], LambdaScope = LambdaScope.Clear() };

    /// <summary>
    /// Attaches the lazy variable (and parameter) stores to this context so <c>variables('x')</c> and parameter
    /// defaults evaluate against it. Call once after constructing the root context; copies made with
    /// <c>with</c> share the bound stores.
    /// </summary>
    public EvaluationContext Bind(IExpressionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        Variables.Bind(evaluator, this);
        if (Parameters is LazyParameters lazy)
        {
            lazy.Bind(evaluator, this);
        }

        return this;
    }
}
