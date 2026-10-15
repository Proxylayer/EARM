using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine.Planning;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Engine.Providers;

/// <summary>
/// One resource to deploy after expansion: a declaration bound to a concrete copy
/// iteration, with its identity, its evaluation context and, once evaluated, its properties split into the object
/// properties a provider writes and the edge properties the edge pass applies.
/// <para>
/// The instance is mutated by the engine as it learns more (a symbolic name resolved, properties evaluated, the
/// parent's Graph id discovered); each instance is handled by one task at a time.
/// </para>
/// </summary>
public sealed class ResourceInstance
{
    private readonly HashSet<ResourceIdentifier> _dependencies = [];

    internal ResourceInstance(
        ResourceDeclaration declaration,
        ResolvedResource resolved,
        ResourceTypeDefinition definition,
        int declarationIndex,
        ResourceIdentifier id,
        bool isSymbolic,
        JsonNode? nameValue,
        EvaluationContext context,
        CopyIteration? copy)
    {
        Declaration = declaration;
        Resolved = resolved;
        Definition = definition;
        DeclarationIndex = declarationIndex;
        PlannedId = id;
        Id = id;
        IsSymbolic = isSymbolic;
        NameValue = nameValue;
        Context = context;
        Copy = copy;
        ApiVersion = declaration.ApiVersion ?? throw new ArgumentException("A resource instance needs an apiVersion.", nameof(declaration));
        Api = definition.TryGetGraphApi(ApiVersion, out var api) ? GraphApiMapping.ToAz(api) : GraphApi.V1;
        Phase = ApplyPhases.Of(definition.Type);
    }

    public ResourceDeclaration Declaration { get; }

    /// <summary>The static evaluation of the declaration (dependsOn, references, condition...).</summary>
    public ResolvedResource Resolved { get; }

    public ResourceTypeDefinition Definition { get; }

    public string Type => Definition.Type;

    public string ApiVersion { get; }

    /// <summary>The Graph endpoint the apiVersion maps to.</summary>
    public GraphApi Api { get; }

    /// <summary>Position of the declaration in the flattened template (document order).</summary>
    public int DeclarationIndex { get; }

    /// <summary>Apply phase, used as a tie-breaker inside a batch.</summary>
    public int Phase { get; }

    /// <summary>
    /// The identity used while planning: concrete, or symbolic when the name depends on another resource (the name
    /// segment is then the <see cref="Expressions.Values.UnknownValue.Symbol"/>). Dependencies and what-if rows refer to it.
    /// </summary>
    public ResourceIdentifier PlannedId { get; }

    /// <summary>The current identity: equals <see cref="PlannedId"/> until a symbolic name is resolved.</summary>
    public ResourceIdentifier Id { get; internal set; }

    public bool IsSymbolic { get; internal set; }

    /// <summary>The raw evaluated name (an unknown leaf while symbolic).</summary>
    public JsonNode? NameValue { get; internal set; }

    public string Name => Id.Name;

    /// <summary>The value of <c>condition</c>; <c>null</c> while it depends on a deploy-time value.</summary>
    public bool? Condition { get; internal set; } = true;

    public bool IsIgnored => Condition == false;

    public CopyIteration? Copy { get; }

    /// <summary>The parent identity for child types (depth 2), symbolic when the parent's name is.</summary>
    public ResourceIdentifier? ParentId => Id.Parent;

    /// <summary>The parent instance when the parent is declared in the same template; otherwise <c>null</c> (an existing parent).</summary>
    public ResourceInstance? Parent { get; internal set; }

    /// <summary>The parent's Graph object id once known (applied in this run or read during planning).</summary>
    public string? ParentGraphId { get; internal set; }

    /// <summary>The context to evaluate this instance's expressions in (concrete copy frame pushed).</summary>
    public EvaluationContext Context { get; internal set; }

    /// <summary>Evaluated properties minus edge properties; <c>null</c> until evaluated.</summary>
    public JsonObject? Properties { get; internal set; }

    /// <summary>Evaluated relationship/assignment properties, for the edge pass; <c>null</c> until evaluated.</summary>
    public JsonObject? EdgeProperties { get; internal set; }

    public bool IsEvaluated => Properties is not null;

    /// <summary>True when <see cref="Properties"/> still contain deploy-time-unknown leaves.</summary>
    public bool HasUnknownProperties { get; internal set; }

    /// <summary>Same-template <c>reference()</c>/<c>resourceId()</c> occurrences outside edge properties (implicit dependencies).</summary>
    public IReadOnlyList<ReferenceOccurrence> ObjectReferences { get; internal set; } = [];

    /// <summary>Same-template occurrences inside edge properties (no object-pass dependency; consumed by the edge pass).</summary>
    public IReadOnlyList<ReferenceOccurrence> EdgeReferences { get; internal set; } = [];

    /// <summary>Occurrences inside the <c>name</c> expression (what a symbolic name waits for).</summary>
    public IReadOnlyList<ReferenceOccurrence> NameReferences { get; internal set; } = [];

    /// <summary>Existing-resource lookups (<c>reference(id, apiVersion)</c>) found in the properties.</summary>
    public IReadOnlyList<ReferenceOccurrence> ExistingReferences { get; internal set; } = [];

    /// <summary>Planned ids of the instances this one must wait for.</summary>
    public IReadOnlySet<ResourceIdentifier> Dependencies => _dependencies;

    /// <summary>The retry hint for this instance's Graph calls: <see cref="RetryHint.ExpectReplicationLag"/> when a dependency was created in this run.</summary>
    public RetryHint RetryHint { get; internal set; } = RetryHint.Default;

    internal bool AddDependency(ResourceIdentifier id)
    {
        return _dependencies.Add(id);
    }

    /// <summary>A short description for messages: <c>type 'name'</c>.</summary>
    public string Describe()
    {
        return $"{Type} '{Name}'";
    }

    public override string ToString()
    {
        return Id.Id;
    }
}
