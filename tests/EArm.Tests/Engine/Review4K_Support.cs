using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Tests.Engine;

/// <summary>
/// A provider whose reads, creates and deletes are plain delegates, for tests that need to say exactly what a provider answers to the read by key
/// or to fail a delete in a chosen way. Everything else is unsupported.
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

    /// <summary>Answers a read by key.</summary>
    public Func<ResourceKey, ExistingResource?> OnRead { get; set; } = _ => null;

    /// <summary>Runs inside a delete (with the live object being deleted), before it is recorded; may throw.</summary>
    public Func<ExistingResource, Task> OnDelete { get; set; } = _ => Task.CompletedTask;

    /// <summary>The Graph ids of the objects that were deleted.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>The keys the provider was asked to read.</summary>
    public List<ResourceKey> Reads { get; } = [];

    public IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        return [];
    }

    public ResourceKey ResolveKey(ResourceInstance desired)
    {
        throw new NotSupportedException();
    }

    public Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        lock (Reads)
        {
            Reads.Add(key);
        }

        return Task.FromResult(OnRead(key));
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

    public async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        await OnDelete(live).ConfigureAwait(false);
        lock (Deleted)
        {
            Deleted.Add(live.GraphId);
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
