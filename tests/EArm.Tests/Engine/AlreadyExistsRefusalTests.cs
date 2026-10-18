using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Graph;
using Xunit;
using static EArm.Tests.Engine.Review1A;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Tests.Engine;

/// <summary>
/// A create answered "already exists" where the provider refuses the object it then finds under the key (a permanent PIM assignment where a time-bound
/// one is declared): the outcome is the provider's refusal. Nothing is reported as "created", and the object is not adopted silently with a warning,
/// whether the provider refuses at once (it found the object itself) or the engine finds it only after waiting out replication.
/// </summary>
public sealed class AlreadyExistsRefusalTests
{
    private const string Refusal = "a permanent assignment holds this tuple; EARM does not manage it";

    private static (EngineHost Host, RefusingProvider Provider, string ExistingId) Seed(bool providerFindsIt)
    {
        var host = new EngineHost();
        var existing = host.Directory.AddGroup("g1", "g1", g => { g["mailEnabled"] = false; g["securityEnabled"] = true; });
        var provider = new RefusingProvider(host.Echo(Fixture.GroupType), providerFindsIt);
        host.Registry.Register(provider, replace: true);
        return (host, provider, IdOf(existing));
    }

    [Fact]
    public async Task TheProviderRefusesTheObjectItFoundAfterAlreadyExists_TheRefusalIsReported_NotACreateThatDidNotComplete()
    {
        var (host, _, _) = Seed(providerFindsIt: true);

        var (_, result) = await ExecuteAsync(host, Groups("g1"));

        var op = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.PimAssignmentConflict, op.ErrorCode);
        Assert.Contains(Refusal, op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("was created", op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("although the create failed", StringComparison.Ordinal));
        Assert.Equal(1, host.Directory.Count("/groups"));
    }

    [Fact]
    public async Task TheEngineFindsAnObjectTheProviderRefuses_AfterAlreadyExists_ItIsNotAdopted()
    {
        var (host, _, existingId) = Seed(providerFindsIt: false);

        var (_, result) = await ExecuteAsync(host, Groups("g1"));

        var op = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.PimAssignmentConflict, op.ErrorCode);
        Assert.Equal(existingId, op.GraphId);
        Assert.Contains("already exists", op.Error, StringComparison.Ordinal);
        Assert.Contains("cannot be adopted", op.Error, StringComparison.Ordinal);
        Assert.Contains(Refusal, op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.ImmutablePropertyChanged);
    }

    /// <summary>
    /// A group provider standing in for a PIM schedule provider: its key read misses the existing object until the create was tried (so the run plans a
    /// create), its create is answered "already exists", and it refuses to manage the object it finds (its plan for that object has an error). With
    /// <c>providerFindsIt</c> it finds the object itself and throws its refusal; otherwise it rethrows "already exists" for the engine to look.
    /// </summary>
    private sealed class RefusingProvider(ProviderBase inner, bool providerFindsIt) : IResourceProvider, IExistingResourceLookup
    {
        private int _createTried;

        public string TypeName => inner.TypeName;

        public IReadOnlyDictionary<string, GraphApi> ApiVersions => inner.ApiVersions;

        public IReadOnlySet<JsonPath> ImmutableProperties => inner.ImmutableProperties;

        public IReadOnlyList<RelationshipSpec> Relationships => inner.Relationships;

        public IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired) => inner.RequiredPermissions(desired);

        public Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken) => inner.PrepareAsync(desired, graph, cancellationToken);

        public ResourceKey ResolveKey(ResourceInstance desired) => inner.ResolveKey(desired);

        public Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
        {
            return Volatile.Read(ref _createTried) == 0 ? Task.FromResult<ExistingResource?>(null) : inner.ReadAsync(key, graph, cancellationToken);
        }

        public ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
        {
            return existing is null
                ? inner.Plan(desired, existing, options)
                : ResourceChange.Unsupported(desired.Id, desired.Type, ChangeType.Modify, $"{desired.Describe()}: {Refusal}") with { UnsupportedCode = EngineDiagnosticCodes.PimAssignmentConflict };
        }

        public async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _createTried, 1);
            var alreadyExists = new GraphException(HttpStatusCode.BadRequest, "RoleAssignmentExists", "The Role assignment already exists.", RetryHint.Default);
            if (!providerFindsIt || await inner.ReadAsync(inner.ResolveKey(desired), graph, cancellationToken) is null)
            {
                throw alreadyExists;
            }

            throw new ProviderException(EngineDiagnosticCodes.PimAssignmentConflict, $"{desired.Describe()}: {Refusal}", desired.Id);
        }

        public Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken) => inner.UpdateAsync(desired, existing, change, graph, cancellationToken);

        public Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken) => inner.DeleteAsync(key, live, graph, cancellationToken);

        public Task<ExistingResource?> ReadExistingAsync(ResourceIdentifier id, string apiVersion, IGraphClient graph, CancellationToken cancellationToken) => inner.ReadExistingAsync(id, apiVersion, graph, cancellationToken);
    }
}
