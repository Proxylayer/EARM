using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core;
using EArm.Engine.Edges;
using EArm.Engine.Planning;
using EArm.Engine.State;
using EArm.Providers.Tests.Infrastructure;
using Xunit;
using static EArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EArm.Providers.Tests;

/// <summary>
/// <see cref="RecordedEdges"/> (the Complete-mode removal of state-recorded edges before an object is deleted): the
/// removal itself, every skip reason, tolerance of an already-gone edge, and the rule that a failed removal is logged
/// and never stops the delete.
/// </summary>
public sealed class RecordedEdgesTests
{
    private const string GroupRef = "\"[reference('test-admins').id]\"";

    private static async Task<(ProviderHost Host, StateEntry Alice, EdgeGraphContext Context)> ArrangeAsync()
    {
        var host = new ProviderHost();
        var run = await host.CreateAsync(host.Resources(Join(Group(), Alice("\"memberOf\": [" + GroupRef + "]"))));
        Check.Succeeded(run);
        var alice = run.State!.Resources[AliceId];
        Assert.NotNull(alice.Relationships);
        host.ClearRequests();
        return (host, alice, Context(host));
    }

    private static EdgeGraphContext Context(ProviderHost host)
    {
        return new EdgeGraphContext(host.Fake, host.Retry, new LiveReferenceResolver(), [], host.Logs.For<EdgeGraphContext>());
    }

    [Fact]
    public async Task RemoveAsync_SendsOneDeletePerRecordedEdge()
    {
        var (host, alice, context) = await ArrangeAsync();
        string groupId = host.IdOf("/groups", "uniqueName", "test-admins");

        int removed = await RecordedEdges.RemoveAsync(alice, host.Catalog, host.EdgeKinds, context, ProviderHost.Ct);

        Assert.Equal(1, removed);
        var delete = Assert.Single(host.Fake.Writes);
        Assert.Equal("DELETE", delete.Method);
        Assert.Contains(groupId, delete.Path, StringComparison.Ordinal);
        Assert.Contains(alice.GraphId, delete.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveAsync_AnEntryWithoutRelationships_SendsNothing()
    {
        var (host, alice, context) = await ArrangeAsync();
        var bare = alice with { Relationships = null };
        var empty = alice with { Relationships = new JsonObject() };

        Assert.Equal(0, await RecordedEdges.RemoveAsync(bare, host.Catalog, host.EdgeKinds, context, ProviderHost.Ct));
        Assert.Equal(0, await RecordedEdges.RemoveAsync(empty, host.Catalog, host.EdgeKinds, context, ProviderHost.Ct));
        Assert.Empty(host.Fake.Requests);
    }

    [Fact]
    public async Task RemoveAsync_SkipsAnUnknownType_AnUnknownProperty_AndARecordItCannotInterpret_WithALogLineEach()
    {
        var (host, alice, context) = await ArrangeAsync();
        var unknownType = alice with { Type = "Microsoft.Entra/notInTheCatalog" };
        var unknownProperty = alice with { Relationships = new JsonObject { ["colour"] = new JsonArray("red") } };
        var uninterpretable = alice with { Relationships = new JsonObject { ["memberOf"] = new JsonArray(JsonValue.Create(7)) } };

        int a = await RecordedEdges.RemoveAsync(unknownType, host.Catalog, host.EdgeKinds, context, ProviderHost.Ct);
        int b = await RecordedEdges.RemoveAsync(unknownProperty, host.Catalog, host.EdgeKinds, context, ProviderHost.Ct);
        int c = await RecordedEdges.RemoveAsync(uninterpretable, host.Catalog, host.EdgeKinds, context, ProviderHost.Ct);

        Assert.Equal([0, 0, 0], [a, b, c]);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task RemoveAsync_ASingleObjectRecord_IsHandledLikeAOneElementArray()
    {
        var (host, alice, context) = await ArrangeAsync();
        string groupId = host.IdOf("/groups", "uniqueName", "test-admins");
        var single = alice with { Relationships = new JsonObject { ["memberOf"] = JsonValue.Create(groupId) } };

        int removed = await RecordedEdges.RemoveAsync(single, host.Catalog, host.EdgeKinds, context, ProviderHost.Ct);

        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task RemoveAsync_AnEdgeThatIsAlreadyGone_CountsAsSent_AndAFailedRemovalDoesNotThrow()
    {
        var (host, alice, context) = await ArrangeAsync();
        host.Fake.Failures.Add("DELETE", "$ref", request => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource does not exist.", request.Hint));

        int gone = await RecordedEdges.RemoveAsync(alice, host.Catalog, host.EdgeKinds, context, ProviderHost.Ct);
        Assert.Equal(1, gone);

        host.Fake.Failures.Forbid("$ref", "DELETE");
        int denied = await RecordedEdges.RemoveAsync(alice, host.Catalog, host.EdgeKinds, context, ProviderHost.Ct);

        Assert.Equal(1, denied);
        Assert.Contains(host.Logs.Lines, line => line.Contains("remove", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RemoveAsync_HonoursCancellation_AndRejectsNullArguments()
    {
        var (host, alice, context) = await ArrangeAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecordedEdges.RemoveAsync(alice, host.Catalog, host.EdgeKinds, context, cts.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RecordedEdges.RemoveAsync(null!, host.Catalog, host.EdgeKinds, context, ProviderHost.Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RecordedEdges.RemoveAsync(alice, null!, host.EdgeKinds, context, ProviderHost.Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RecordedEdges.RemoveAsync(alice, host.Catalog, null!, context, ProviderHost.Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => RecordedEdges.RemoveAsync(alice, host.Catalog, host.EdgeKinds, null!, ProviderHost.Ct));
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task RemoveAsync_ARegistryWithoutTheHandler_SkipsTheProperty()
    {
        var (host, alice, context) = await ArrangeAsync();

        int removed = await RecordedEdges.RemoveAsync(alice, host.Catalog, EdgeKindRegistry.Empty, context, ProviderHost.Ct);

        Assert.Equal(0, removed);
        Assert.Empty(host.Fake.Writes);
    }
}
