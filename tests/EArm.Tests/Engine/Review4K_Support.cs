using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Tests.Engine;

/// <summary>
/// A provider whose reads, creates and deletes are plain delegates, for tests that need to say exactly what a provider answers to the read by key and the read by pinned
/// id (the two calls the state-driven delete compares) or to fail a delete in a chosen way. Everything else is unsupported.
/// </summary>
internal sealed class StubProvider : IResourceProvider
{
    public StubProvider(string typeName)
    {
        TypeName = typeName;
    }

    public string TypeName { get; }

    public IReadOnlyDictionary<string, GraphApi> ApiVersions { get; } = new Dictionary<string, GraphApi>();

    public IReadOnlySet<JsonPath> ImmutableProperties { get; } = new HashSet<JsonPath>();

    public IReadOnlyList<RelationshipSpec> Relationships { get; } = [];

    /// <summary>Answers a read: the key and the pinned id (<c>null</c> for the read by key).</summary>
    public Func<ResourceKey, string?, ExistingResource?> OnRead { get; set; } = (_, _) => null;

    /// <summary>Runs inside a delete, before it is recorded; may throw.</summary>
    public Func<StateEntry, Task> OnDelete { get; set; } = _ => Task.CompletedTask;

    /// <summary>The Graph ids of the entries that were deleted.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>The pinned ids the provider was asked to read (<c>null</c> entries are reads by key).</summary>
    public List<string?> Reads { get; } = [];

    public IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        return [];
    }

    public ResourceKey ResolveKey(ResourceInstance desired)
    {
        throw new NotSupportedException();
    }

    public Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken)
    {
        lock (Reads)
        {
            Reads.Add(stateId);
        }

        return Task.FromResult(OnRead(key, stateId));
    }

    public ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        throw new NotSupportedException();
    }

    public Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public async Task DeleteAsync(StateEntry entry, IGraphClient graph, CancellationToken cancellationToken)
    {
        await OnDelete(entry).ConfigureAwait(false);
        lock (Deleted)
        {
            Deleted.Add(entry.GraphId);
        }
    }

    /// <summary>A live object with the given Graph id and properties (the diff view and the raw object are the same).</summary>
    public static ExistingResource Live(string graphId, params (string Name, JsonNode? Value)[] properties)
    {
        var raw = new JsonObject { ["id"] = graphId };
        foreach (var (name, value) in properties)
        {
            raw[name] = value;
        }

        return new ExistingResource(graphId, raw, raw);
    }

    public static ProviderRegistry Registry(params StubProvider[] providers)
    {
        var registry = new ProviderRegistry();
        foreach (var provider in providers)
        {
            registry.Register(provider);
        }

        return registry;
    }
}
