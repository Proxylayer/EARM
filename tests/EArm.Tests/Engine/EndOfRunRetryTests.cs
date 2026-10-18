using System;
using System.Collections.Generic;
using System.Linq;
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
/// A resource a provider defers until later work of the same run has happened (<see cref="ResourceChange.RetryAtEndOfRun"/>: a group PIM policy whose
/// group a later schedule request of the run onboards) is planned and applied once more after the last batch, so the run converges in one pass.
/// </summary>
public sealed class EndOfRunRetryTests
{
    private const string Waiting = "the group is not onboarded yet";

    private static EArm.Core.TemplateDocument PolicyThenOnboarder()
    {
        return Templates.Resources(
            "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"policy\", \"properties\": { \"displayName\": \"policy\", \"mailEnabled\": false, \"securityEnabled\": true } }, "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"onboarder\", \"dependsOn\": [ \"[resourceId('Microsoft.Entra/groups', 'policy')]\" ], "
            + "\"properties\": { \"displayName\": \"onboarder\", \"mailEnabled\": false, \"securityEnabled\": true } }");
    }

    private static (EngineHost Host, DeferringProvider Provider) Host(bool onboards, int lag = 0)
    {
        var host = new EngineHost();
        var provider = new DeferringProvider(host.Echo(Fixture.GroupType), onboards, lag);
        host.Registry.Register(provider, replace: true);
        return (host, provider);
    }

    [Fact]
    public async Task ADeferredResource_IsAppliedAfterTheLastBatch_InTheSameRun()
    {
        var (host, provider) = Host(onboards: true);

        var (_, result) = await ExecuteAsync(host, PolicyThenOnboarder());

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        var policy = result.Operations.Single(o => o.Id.Id == GroupId("policy"));
        Assert.Equal(OperationStatus.Succeeded, policy.Status);
        Assert.Equal(ChangeType.Create, policy.ChangeType);
        Assert.Equal(2, host.Directory.Count("/groups"));
        Assert.Equal(2, provider.PolicyPlans);
        Assert.Empty(host.ReplicationDelays.Delays);
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains(Waiting, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeferredResource_WhoseCauseIsStillReplicating_IsReadAgainUntilItCanBeApplied()
    {
        var (host, provider) = Host(onboards: true, lag: 2);

        var (_, result) = await ExecuteAsync(host, PolicyThenOnboarder());

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(ChangeType.Create, result.Operations.Single(o => o.Id.Id == GroupId("policy")).ChangeType);
        Assert.Equal(4, provider.PolicyPlans);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], host.ReplicationDelays.Delays);
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains(Waiting, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeferredResource_StillDeferredAtTheEnd_StaysIgnored_AndItsReasonIsReportedOnce()
    {
        var (host, provider) = Host(onboards: false);

        var (_, result) = await ExecuteAsync(host, PolicyThenOnboarder());

        var policy = result.Operations.Single(o => o.Id.Id == GroupId("policy"));
        Assert.Equal(OperationStatus.Ignored, policy.Status);
        Assert.True(provider.PolicyPlans > 2);
        Assert.True(host.ReplicationDelays.Delays.Aggregate(TimeSpan.Zero, (a, b) => a + b) <= ReplicationWait.Default.Window);
        Assert.Single(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.PimPolicyNotAvailable && d.Message.Contains(Waiting, StringComparison.Ordinal));
        Assert.Equal(1, host.Directory.Count("/groups"));
    }

    [Fact]
    public async Task ADeferredResource_IsNotRetried_WhenTheRunStoppedAtABatchBoundary()
    {
        var (host, provider) = Host(onboards: true);
        host.Fake.Failures.Add("PATCH", "onboarder", request => new GraphException(System.Net.HttpStatusCode.BadRequest, "Request_BadRequest", "Invalid value.", request.Hint), times: 1);

        var (_, result) = await ExecuteAsync(host, PolicyThenOnboarder());

        Assert.False(result.Succeeded);
        Assert.Equal(OperationStatus.Ignored, result.Operations.Single(o => o.Id.Id == GroupId("policy")).Status);
        Assert.Equal(1, provider.PolicyPlans);
    }

    /// <summary>
    /// Plans the group <c>policy</c> as a deferred <see cref="ChangeType.Ignore"/> row (<see cref="ResourceChange.RetryAtEndOfRun"/>) until the group
    /// <c>onboarder</c> was created (when <c>onboards</c>) and <c>lag</c> more plans have passed, as a PIM policy waits for the schedule request that onboards its group.
    /// </summary>
    private sealed class DeferringProvider(ProviderBase inner, bool onboards, int lag) : IResourceProvider, IExistingResourceLookup
    {
        private int _onboarded;
        private int _policyPlans;
        private int _plansSinceOnboarded;

        public int PolicyPlans => Volatile.Read(ref _policyPlans);

        public string TypeName => inner.TypeName;

        public IReadOnlyDictionary<string, GraphApi> ApiVersions => inner.ApiVersions;

        public IReadOnlySet<JsonPath> ImmutableProperties => inner.ImmutableProperties;

        public IReadOnlyList<RelationshipSpec> Relationships => inner.Relationships;

        public IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired) => inner.RequiredPermissions(desired);

        public Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken) => inner.PrepareAsync(desired, graph, cancellationToken);

        public ResourceKey ResolveKey(ResourceInstance desired) => inner.ResolveKey(desired);

        public Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken) => inner.ReadAsync(key, graph, cancellationToken);

        public ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
        {
            if (desired.Id.NameLeaf != "policy")
            {
                return inner.Plan(desired, existing, options);
            }

            Interlocked.Increment(ref _policyPlans);
            if (Volatile.Read(ref _onboarded) == 1 && Interlocked.Increment(ref _plansSinceOnboarded) > lag)
            {
                return inner.Plan(desired, existing, options);
            }

            return ResourceChange.Ignore(desired.Id, desired.Type, EngineDiagnosticCodes.PimPolicyNotAvailable + ": " + Waiting) with { RetryAtEndOfRun = true };
        }

        public async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
        {
            var applied = await inner.CreateAsync(desired, graph, cancellationToken);
            if (onboards && desired.Id.NameLeaf == "onboarder")
            {
                Interlocked.Exchange(ref _onboarded, 1);
            }

            return applied;
        }

        public Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken) => inner.UpdateAsync(desired, existing, change, graph, cancellationToken);

        public Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken) => inner.DeleteAsync(key, live, graph, cancellationToken);

        public Task<ExistingResource?> ReadExistingAsync(ResourceIdentifier id, string apiVersion, IGraphClient graph, CancellationToken cancellationToken) => inner.ReadExistingAsync(id, apiVersion, graph, cancellationToken);
    }
}
