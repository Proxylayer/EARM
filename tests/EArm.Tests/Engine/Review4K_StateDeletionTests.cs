using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.State;
using EArm.Graph;
using EArm.TestSupport;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// The safety rules of a state-driven delete, one decision per test: the comparisons that must ignore letter case (Graph ids and tenant ids are GUIDs, spelled either way),
/// the object that is not the pinned one, errors that must never carry a registered secret, and the bookkeeping of <see cref="DeploymentDeleter"/>.
/// </summary>
public sealed class Review4K_StateDeletionTests
{
    private const string BearerFragment = "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl";

    private static readonly FakeGraphClient Graph = new();

    private static StateEntry GroupEntry(string name, string graphId, StateProvenance provenance = StateProvenance.Created)
    {
        return Entry(GroupId(name), Fixture.GroupType, graphId, "uniqueName", name, provenance);
    }

    private static StubProvider GroupProvider(string pinnedAndKeyId)
    {
        return new StubProvider(Fixture.GroupType) { OnRead = _ => StubProvider.Live(pinnedAndKeyId, ("uniqueName", "g")) };
    }

    private static Task<DeleteOutcome> DeleteAsync(StateEntry entry, StubProvider provider, DeleteGuard? guard = null, Func<StateDeletion.DeleteCheck, Task>? beforeDelete = null)
    {
        return StateDeletion.DeleteAsync(entry, StubProvider.Registry(provider), Graph, null, guard ?? DeleteGuard.Default, Ct, beforeDelete);
    }

    // ---- the signed-in principal ----

    [Theory]
    [InlineData("abcdef00-0000-4000-8000-000000000001", "ABCDEF00-0000-4000-8000-000000000001")]
    [InlineData("ABCDEF00-0000-4000-8000-000000000002", "abcdef00-0000-4000-8000-000000000002")]
    public async Task TheSignedInPrincipal_IsProtected_WhateverTheCaseOfItsId(string entryId, string signedIn)
    {
        var provider = GroupProvider(entryId);

        var outcome = await DeleteAsync(GroupEntry("g", entryId), provider, DeleteGuard.Default with { SignedInPrincipalId = signedIn });

        Assert.Equal(DeleteStatus.Failed, outcome.Status);
        Assert.Equal(EngineDiagnosticCodes.ProtectedObject, outcome.ErrorCode);
        Assert.Empty(provider.Deleted);
        Assert.Empty(provider.Reads);
    }

    // ---- the parent recorded in a child entry ----

    private static StateFile ChildState(string recordedParentId, string parentPinnedId, out StateEntry child)
    {
        var state = NewState();
        state.Upsert(Entry(AppId("app1"), Fixture.AppType, parentPinnedId, "uniqueName", "app1"));
        child = Entry(AppId("app1") + "/federatedIdentityCredentials/fic1", Fixture.FicType, "fic-graph-id", "name", "fic1", parentGraphId: recordedParentId) with { DependsOn = [AppId("app1")] };
        state.Upsert(child);
        return state;
    }

    private static StubProvider FicProvider()
    {
        return new StubProvider(Fixture.FicType) { OnRead = _ => StubProvider.Live("fic-graph-id", ("name", "fic1")) };
    }

    [Fact]
    public async Task AParentIdThatDiffersOnlyInCase_IsTheSameParent_AndTheChildIsDeleted()
    {
        var state = ChildState("ABCDEF00-0000-4000-8000-0000000000AA", "abcdef00-0000-4000-8000-0000000000aa", out var child);
        var provider = FicProvider();

        var outcome = await DeleteAsync(child, provider, DeleteGuard.Default with { State = state });

        Assert.Equal(DeleteStatus.Deleted, outcome.Status);
        Assert.Equal(["fic-graph-id"], provider.Deleted);
    }

    [Fact]
    public async Task AParentIdThatContradictsTheParentsEntry_StopsTheDelete_WithAStateEntryMismatch()
    {
        var state = ChildState("abcdef00-0000-4000-8000-0000000000aa", "abcdef00-0000-4000-8000-0000000000bb", out var child);
        var provider = FicProvider();

        var outcome = await DeleteAsync(child, provider, DeleteGuard.Default with { State = state });

        Assert.Equal(DeleteStatus.Failed, outcome.Status);
        Assert.Equal(EngineDiagnosticCodes.StateEntryMismatch, outcome.ErrorCode);
        Assert.Contains("records parent Graph id abcdef00-0000-4000-8000-0000000000aa", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(provider.Deleted);
        Assert.Empty(provider.Reads);
    }

    [Fact]
    public async Task WithoutTheStateInTheGuard_TheParentConsistencyCheckHasNothingToCompareWith()
    {
        ChildState("abcdef00-0000-4000-8000-0000000000aa", "abcdef00-0000-4000-8000-0000000000bb", out var child);
        var provider = FicProvider();

        var outcome = await DeleteAsync(child, provider);

        Assert.Equal(DeleteStatus.Deleted, outcome.Status);
    }

    [Fact]
    public async Task DeleteAll_PassesTheStateToTheGuard_SoAnInconsistentChildIsNotDeleted()
    {
        var state = ChildState("abcdef00-0000-4000-8000-0000000000aa", "abcdef00-0000-4000-8000-0000000000bb", out _);
        var fic = FicProvider();
        var app = new StubProvider(Fixture.AppType) { OnRead = _ => StubProvider.Live("abcdef00-0000-4000-8000-0000000000bb", ("uniqueName", "app1")) };

        var result = await DeploymentDeleter.DeleteAllAsync(state, new InMemoryStateStore(), StubProvider.Registry(fic, app), Graph, null, continueOnError: true, DeleteGuard.Default, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Ct);

        Assert.Empty(fic.Deleted);
        Assert.Equal(EngineDiagnosticCodes.StateEntryMismatch, result.Outcomes.Single(o => o.Entry.Type == Fixture.FicType).ErrorCode);
        Assert.NotNull(state.Find(AppId("app1") + "/federatedIdentityCredentials/fic1"));
    }

    [Fact]
    public async Task TheExecutorsModePass_PassesTheStateToTheGuard_SoAnInconsistentChildIsNotDeleted()
    {
        var fic = FicProvider();
        var app = new StubProvider(Fixture.AppType) { OnRead = _ => StubProvider.Live("abcdef00-0000-4000-8000-0000000000bb", ("uniqueName", "app1")) };
        var host = new EngineHost(registry: StubProvider.Registry(fic, app));
        var state = ChildState("abcdef00-0000-4000-8000-0000000000aa", "abcdef00-0000-4000-8000-0000000000bb", out _);

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete, execute: new ExecuteOptions { ContinueOnError = true });

        Assert.Empty(fic.Deleted);
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.StateEntryMismatch);
    }

    // ---- the live object found by key ----

    [Fact]
    public async Task ALiveIdThatDiffersOnlyInCase_IsTheRecordedObject()
    {
        var provider = new StubProvider(Fixture.GroupType)
        {
            // By key: the object with the id spelled in capitals.
            OnRead = _ => StubProvider.Live("AAAAAAAA-0000-4000-8000-000000000001", ("uniqueName", "g")),
        };

        var outcome = await DeleteAsync(GroupEntry("g", "aaaaaaaa-0000-4000-8000-000000000001"), provider);

        Assert.Equal(DeleteStatus.Deleted, outcome.Status);
        Assert.Single(provider.Reads);
    }

    [Fact]
    public async Task WhenTheKeyBelongsToAnotherObject_NothingIsDeleted_AndNoEdgeIsRemoved()
    {
        var provider = new StubProvider(Fixture.GroupType)
        {
            // The key leads to object B, not the recorded object A: B is never deleted on the entry's behalf.
            OnRead = _ => StubProvider.Live("bbbbbbbb-0000-4000-8000-000000000002", ("uniqueName", "g")),
        };
        int beforeDeleteCalls = 0;

        var outcome = await DeleteAsync(GroupEntry("g", "aaaaaaaa-0000-4000-8000-000000000001"), provider, beforeDelete: _ =>
        {
            beforeDeleteCalls++;
            return Task.CompletedTask;
        });

        Assert.Equal(DeleteStatus.Failed, outcome.Status);
        Assert.Equal(EngineDiagnosticCodes.StateEntryMismatch, outcome.ErrorCode);
        Assert.Contains("names Graph id bbbbbbbb-0000-4000-8000-000000000002", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(provider.Deleted);
        Assert.Equal(0, beforeDeleteCalls);
    }

    [Fact]
    public async Task WhenTheKeyFindsNothing_TheObjectIsAlreadyGone()
    {
        var provider = new StubProvider(Fixture.GroupType) { OnRead = _ => null };

        var outcome = await DeleteAsync(GroupEntry("g", "aaaaaaaa-0000-4000-8000-000000000001"), provider);

        Assert.Equal(DeleteStatus.AlreadyGone, outcome.Status);
        Assert.Empty(provider.Deleted);
        Assert.Single(provider.Reads);
    }

    // ---- first-party service principals ----

    [Theory]
    [InlineData("Microsoft.Entra/servicePrincipals", "22222222-2222-2222-2222-222222222222", "11111111-1111-1111-1111-111111111111", true)]
    [InlineData("MICROSOFT.ENTRA/SERVICEPRINCIPALS", "22222222-2222-2222-2222-222222222222", "11111111-1111-1111-1111-111111111111", true)]
    [InlineData("Microsoft.Entra/servicePrincipals", "AAAAAAAA-2222-2222-2222-222222222222", "aaaaaaaa-2222-2222-2222-222222222222", false)]
    [InlineData("Microsoft.Entra/servicePrincipals", "aaaaaaaa-2222-2222-2222-222222222222", "AAAAAAAA-2222-2222-2222-222222222222", false)]
    [InlineData("Microsoft.Entra/groups", "22222222-2222-2222-2222-222222222222", "11111111-1111-1111-1111-111111111111", false)]
    [InlineData("Microsoft.Entra/servicePrincipals", "", "11111111-1111-1111-1111-111111111111", false)]
    [InlineData("Microsoft.Entra/servicePrincipals", "22222222-2222-2222-2222-222222222222", "", false)]
    [InlineData("Microsoft.Entra/servicePrincipals", "22222222-2222-2222-2222-222222222222", null, false)]
    public void FirstPartyServicePrincipals_AreRecognisedByTheirOwnerTenant_IgnoringCase(string type, string owner, string? tenant, bool expected)
    {
        var raw = new JsonObject { ["appOwnerOrganizationId"] = owner };

        Assert.Equal(expected, StateDeletion.IsFirstPartyServicePrincipal(type, raw, tenant));
        Assert.False(StateDeletion.IsFirstPartyServicePrincipal(type, null, tenant));
    }

    // ---- errors never carry a registered secret ----

    private const string Secret = "Zq9-n0t-in-any-shape-4711";

    [Fact]
    public async Task AProviderFailure_IsScrubbedOfRegisteredSecrets()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);
        var provider = GroupProvider("aaaaaaaa-0000-4000-8000-000000000001");
        provider.OnDelete = _ => throw new ProviderExceptionForTest("the provider echoed " + Secret);

        var outcome = await DeleteAsync(GroupEntry("g", "aaaaaaaa-0000-4000-8000-000000000001"), provider);

        Assert.Equal(DeleteStatus.Failed, outcome.Status);
        Assert.DoesNotContain(Secret, outcome.Error, StringComparison.Ordinal);
        Assert.Contains("***", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGraphFailure_IsScrubbedOfRegisteredSecrets()
    {
        using var scope = SecretScrubber.Begin();
        var provider = GroupProvider("aaaaaaaa-0000-4000-8000-000000000001");
        provider.OnDelete = _ =>
        {
            // The value becomes known after the exception text was built from a body that echoed it.
            var failure = new GraphTransportException(GraphTransportFailure.Network, "the request failed: " + Secret);
            SecretScrubber.Register(Secret);
            throw failure;
        };

        var outcome = await DeleteAsync(GroupEntry("g", "aaaaaaaa-0000-4000-8000-000000000001"), provider);

        Assert.Equal(DeleteStatus.Failed, outcome.Status);
        Assert.DoesNotContain(Secret, outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnexpectedFailureOfTheEngineKind_IsScrubbedOfRegisteredSecrets()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);
        var provider = GroupProvider("aaaaaaaa-0000-4000-8000-000000000001");
        provider.OnDelete = _ => throw new InvalidOperationException("the provider echoed " + Secret);

        var outcome = await DeleteAsync(GroupEntry("g", "aaaaaaaa-0000-4000-8000-000000000001"), provider);

        Assert.Equal(DeleteStatus.Failed, outcome.Status);
        Assert.DoesNotContain(Secret, outcome.Error, StringComparison.Ordinal);
        Assert.StartsWith("InvalidOperationException:", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANotFoundOnDelete_IsAlreadyGone()
    {
        var provider = GroupProvider("aaaaaaaa-0000-4000-8000-000000000001");
        provider.OnDelete = _ => throw new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "gone");

        var outcome = await DeleteAsync(GroupEntry("g", "aaaaaaaa-0000-4000-8000-000000000001"), provider);

        Assert.Equal(DeleteStatus.AlreadyGone, outcome.Status);
    }

    // ---- DeploymentDeleter bookkeeping ----

    [Fact]
    public async Task DeleteAll_StartsTheOperationsOfTheRunFresh_AndMarksItRunningUntilItEnds()
    {
        var state = NewState();
        state.Upsert(GroupEntry("g", "aaaaaaaa-0000-4000-8000-000000000001"));
        state.Operations.Add(new OperationRecord { ResourceId = "/stale", Type = Fixture.GroupType, Status = OperationStatus.Succeeded, ChangeType = EArm.Engine.Providers.ChangeType.Create, StartedOn = Start, FinishedOn = Start });
        var provider = GroupProvider("aaaaaaaa-0000-4000-8000-000000000001");
        string? statusWhileDeleting = null;
        provider.OnDelete = _ =>
        {
            statusWhileDeleting = state.LastStatus;
            return Task.CompletedTask;
        };
        var store = new InMemoryStateStore();

        var result = await DeploymentDeleter.DeleteAllAsync(state, store, StubProvider.Registry(provider), Graph, null, continueOnError: false, DeleteGuard.Default, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Ct);

        Assert.True(result.StateRemoved);
        Assert.Equal(StateFile.StatusRunning, statusWhileDeleting);
        var operation = Assert.Single(state.Operations);
        Assert.Equal("/providers/Microsoft.Entra/groups/g", operation.ResourceId);
        Assert.DoesNotContain(state.Operations, o => o.ResourceId == "/stale");
    }

    [Fact]
    public async Task DeleteAll_RedactsTheErrorItRecordsInTheStateOperations_OfARefusedEntry()
    {
        var state = NewState();
        // An adopted entry is refused; its message carries the Graph id, which here holds a token-shaped fragment.
        state.Upsert(GroupEntry("g", BearerFragment, StateProvenance.Adopted));
        var provider = GroupProvider(BearerFragment);

        var result = await DeploymentDeleter.DeleteAllAsync(state, new InMemoryStateStore(), StubProvider.Registry(provider), Graph, null, continueOnError: true, DeleteGuard.Default, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Ct);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(DeleteStatus.Refused, outcome.Status);
        Assert.Contains(BearerFragment, outcome.Error, StringComparison.Ordinal);
        var operation = Assert.Single(state.Operations);
        Assert.Equal(OperationStatus.Ignored, operation.Status);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", operation.Error, StringComparison.Ordinal);
        Assert.Equal(EArm.Graph.SecretRedactor.RedactText(outcome.Error), operation.Error);
    }

    [Fact]
    public async Task DeleteAll_RedactsTheErrorItRecordsInTheStateOperations_OfAFailedEntry()
    {
        var state = NewState();
        state.Upsert(GroupEntry("g", "aaaaaaaa-0000-4000-8000-000000000001"));
        var provider = GroupProvider("aaaaaaaa-0000-4000-8000-000000000001");
        provider.OnDelete = _ => throw new ProviderExceptionForTest("the provider echoed " + BearerFragment);

        var result = await DeploymentDeleter.DeleteAllAsync(state, new InMemoryStateStore(), StubProvider.Registry(provider), Graph, null, continueOnError: true, DeleteGuard.Default, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Ct);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(DeleteStatus.Failed, outcome.Status);
        Assert.Contains(BearerFragment, outcome.Error, StringComparison.Ordinal);
        var operation = Assert.Single(state.Operations);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", operation.Error, StringComparison.Ordinal);
        Assert.Equal(EArm.Graph.SecretRedactor.RedactText(outcome.Error), operation.Error);
    }

    private sealed class ProviderExceptionForTest : EArm.Engine.Providers.ProviderException
    {
        public ProviderExceptionForTest(string message)
            : base(EngineDiagnosticCodes.ResourceDeleted, message)
        {
        }
    }
}
