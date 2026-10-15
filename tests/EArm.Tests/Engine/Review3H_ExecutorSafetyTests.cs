using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Json;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers;
using EArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static EArm.Tests.Engine.Review1A;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Tests.Engine;

/// <summary>A provider that forwards to another and runs a hook around its create, update and plan (a post-create step that fails, an unexpected exception).</summary>
internal sealed class Review3HHookedProvider : IResourceProvider, IExistingResourceLookup
{
    private readonly ProviderBase _inner;

    public Review3HHookedProvider(ProviderBase inner)
    {
        _inner = inner;
    }

    /// <summary>Runs after the inner create wrote the object; it may throw (the object then exists although the create failed).</summary>
    public Func<ResourceInstance, AppliedResource, Task>? AfterCreate { get; set; }

    /// <summary>Runs instead of the inner create when it is set and returns a non-null value.</summary>
    public Func<ResourceInstance, Task>? BeforeCreate { get; set; }

    public string TypeName => _inner.TypeName;

    public IReadOnlyDictionary<string, GraphApi> ApiVersions => _inner.ApiVersions;

    public IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired) => _inner.RequiredPermissions(desired);

    public IReadOnlySet<JsonPath> ImmutableProperties => _inner.ImmutableProperties;

    public IReadOnlyList<RelationshipSpec> Relationships => _inner.Relationships;

    public Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken) => _inner.PrepareAsync(desired, graph, cancellationToken);

    public ResourceKey ResolveKey(ResourceInstance desired) => _inner.ResolveKey(desired);

    public Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken) => _inner.ReadAsync(key, graph, cancellationToken);

    public ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options) => _inner.Plan(desired, existing, options);

    public async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        if (BeforeCreate is not null)
        {
            await BeforeCreate(desired);
        }

        var applied = await _inner.CreateAsync(desired, graph, cancellationToken);
        if (AfterCreate is not null)
        {
            await AfterCreate(desired, applied);
        }

        return applied;
    }

    public Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken) => _inner.UpdateAsync(desired, existing, change, graph, cancellationToken);

    public Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken) => _inner.DeleteAsync(key, live, graph, cancellationToken);

    public Task<ExistingResource?> ReadExistingAsync(ResourceIdentifier id, string apiVersion, IGraphClient graph, CancellationToken cancellationToken) => _inner.ReadExistingAsync(id, apiVersion, graph, cancellationToken);
}

/// <summary>
/// Review round 2, findings S2-03 (a recreate bypassed the delete guards), S2-05 (a failed principal under <c>--continue-on-error</c> lost its live edges), S2-06 (an edge add
/// whose answer was lost), S2-07 (blind outer retry of a create), S2-08 (a failure after the primary write lost the object and its one-time secret), S2-09 (no
/// catch-all), S2-10 (Ctrl-C in the end phase), S2-17 (an uncertain create).
/// </summary>
public sealed class Review3H_ExecutorSafetyTests
{
    private static string GroupDoc(string name, string extraProperties = "")
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"" + name + "\", \"properties\": { \"displayName\": \"" + name + "\", \"mailEnabled\": false, \"securityEnabled\": true" + extraProperties + " } }";
    }

    private static string UserDoc(string upn, string dependsOn = "")
    {
        return "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"" + upn + "\", " + (dependsOn.Length == 0 ? string.Empty : "\"dependsOn\": [ \"[resourceId('Microsoft.Entra/users', '" + dependsOn + "')]\" ], ")
            + "\"properties\": { \"displayName\": \"" + upn + "\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" } } }";
    }

    private static EArm.Core.TemplateDocument Users(params string[] upns)
    {
        return Templates.Resources(string.Join(", ", upns.Select(u => UserDoc(u))));
    }

    private static async Task AddMemberAsync(EngineHost host, string groupId, string objectId)
    {
        var body = new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/" + objectId };
        await host.Fake.PostAsync(new GraphRequest("/groups/" + groupId + "/members/$ref", GraphApi.V1), body, Ct);
    }

    private static IReadOnlyList<string> MembersOf(EngineHost host, string groupId)
    {
        return host.Directory.References("/groups/" + groupId + "/members");
    }

    // ---- S2-03: recreate ----

    private static string PasswordFixtureDoc()
    {
        return "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"app\", \"properties\": { \"displayName\": \"App\" } }, "
            + "{ \"type\": \"Microsoft.Entra/applications/passwordCredentials\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"app/ci\", \"properties\": { \"rotationTag\": \"v2\" } }";
    }

    private static JsonObject SeedApplicationWithCredential(EngineHost host)
    {
        return host.Directory.AddApplication("app", "App", configure: a => a["passwordCredentials"] = new JsonArray(new JsonObject
        {
            ["keyId"] = "K1",
            ["displayName"] = "ci",
            ["endDateTime"] = "2027-10-01T00:00:00Z",
            ["rotationTag"] = "v1",
            ["hint"] = "old",
            ["customKeyIdentifier"] = null,
        }));
    }

    [Fact]
    public async Task ARecreate_ThatWasNotConfirmed_IsRefused_NothingIsDeletedOrCreated()
    {
        var host = new EngineHost();
        SeedApplicationWithCredential(host);

        var (_, result) = await ExecuteAsync(host, Templates.Resources(PasswordFixtureDoc()), execute: new ExecuteOptions { ApprovedRecreates = new HashSet<string>() });

        Assert.False(result.Succeeded);
        var credential = Assert.Single(result.Operations, o => o.Type.EndsWith("passwordCredentials", StringComparison.Ordinal));
        Assert.Equal(OperationStatus.Failed, credential.Status);
        Assert.Equal(EngineDiagnosticCodes.RecreateNotConfirmed, credential.ErrorCode);
        Assert.Contains("-y", credential.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Path.EndsWith("Password", StringComparison.Ordinal));
        Assert.Single(host.Directory.Objects("/applications").Single()["passwordCredentials"]!.AsArray());
    }

    [Fact]
    public async Task ARecreate_IsPlannedAsADeleteAndCreate_InTheWhatIf()
    {
        var host = new EngineHost();
        SeedApplicationWithCredential(host);

        var plan = await host.WhatIfAsync(Templates.Resources(PasswordFixtureDoc()));

        var row = Assert.Single(plan.Changes, c => c.ResourceType.EndsWith("passwordCredentials", StringComparison.Ordinal));
        Assert.False(row.HasError);
        Assert.Equal(ChangeMode.Recreate, row.Mode);
        Assert.Contains(row.Reasons, r => r.Contains("DELETED and created again", StringComparison.Ordinal));
        Assert.Contains(plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.DestructiveDelete && d.Message.Contains("(-y)", StringComparison.Ordinal));
    }

    // ---- S2-05: --continue-on-error and the edge pass ----

    [Fact]
    public async Task AFailedPrincipal_UnderContinueOnError_DoesNotLoseItsLiveMembership()
    {
        var host = new EngineHost();
        var group = host.Directory.AddGroup("g", "g");
        var failing = host.Directory.AddUser("u1@contoso.com", "U1 old");
        var fine = host.Directory.AddUser("u2@contoso.com", "U2");
        await AddMemberAsync(host, IdOf(group), IdOf(failing));
        await AddMemberAsync(host, IdOf(group), IdOf(fine));
        host.Fake.Failures.Forbid("/users/" + IdOf(failing), "PATCH");
        string userBody = "\"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" }";
        var document = Templates.Resources(
            GroupDoc("g", ", \"members\": [ \"" + IdOf(fine) + "\" ]") + ", "
            + "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u1@contoso.com\", \"properties\": { \"displayName\": \"U1 new\", " + userBody + ", \"memberOf\": [ \"" + IdOf(group) + "\" ] } }, "
            + "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u2@contoso.com\", \"properties\": { \"displayName\": \"U2\", " + userBody + " } }");

        var (_, result) = await ExecuteAsync(host, document, execute: new ExecuteOptions { ContinueOnError = true });

        Assert.Equal(OperationStatus.Failed, result.Operations.Single(o => o.Id.Id.EndsWith("u1@contoso.com", StringComparison.Ordinal)).Status);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE" && w.Path.Contains("/members/", StringComparison.Ordinal));
        Assert.Contains(IdOf(failing), MembersOf(host, IdOf(group)), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(IdOf(fine), MembersOf(host, IdOf(group)), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("was not pruned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheWhatIf_DoesNotPlanToPruneASet_ThatAnUnplannedPrincipalCouldContributeTo()
    {
        var host = new EngineHost();
        var group = host.Directory.AddGroup("g", "g");
        var unreadable = host.Directory.AddUser("u1@contoso.com", "U1");
        var fine = host.Directory.AddUser("u2@contoso.com", "U2");
        await AddMemberAsync(host, IdOf(group), IdOf(unreadable));
        await AddMemberAsync(host, IdOf(group), IdOf(fine));
        host.Fake.Failures.Forbid("/users/u1@contoso.com", "GET");
        string userBody = "\"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" }";
        var document = Templates.Resources(
            GroupDoc("g", ", \"members\": [ \"" + IdOf(fine) + "\" ]") + ", "
            + "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u1@contoso.com\", \"properties\": { \"displayName\": \"U1\", " + userBody + ", \"memberOf\": [ \"" + IdOf(group) + "\" ] } }, "
            + "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u2@contoso.com\", \"properties\": { \"displayName\": \"U2\", " + userBody + " } }");

        var plan = await host.WhatIfAsync(document);

        Assert.True(plan.Changes.Single(c => c.Id.Id.EndsWith("u1@contoso.com", StringComparison.Ordinal)).HasError);
        var groupRow = plan.Changes.Single(c => c.Id.Id == GroupId("g"));
        var members = groupRow.Deltas.SingleOrDefault(d => d.Path.ToString() == "properties.members");
        Assert.True(members is null || members.Children is null || members.Children.All(c => c.Kind != PropertyChangeType.Delete), "no member may be planned for removal");
        Assert.Contains(plan.Diagnostics, d => d.Message.Contains("was not pruned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAFailedPrincipal_TheAuthoritativeSetStillPrunesWhatIsNotDeclared()
    {
        var host = new EngineHost();
        var group = host.Directory.AddGroup("g", "g");
        var stranger = host.Directory.AddUser("stranger@contoso.com", "Stranger");
        await AddMemberAsync(host, IdOf(group), IdOf(stranger));
        var document = Templates.Resources(GroupDoc("g", ", \"members\": []"));

        var (_, result) = await ExecuteAsync(host, document, execute: new ExecuteOptions { ContinueOnError = true });

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.DoesNotContain(IdOf(stranger), MembersOf(host, IdOf(group)), StringComparer.OrdinalIgnoreCase);
    }

    // ---- principal-side edges are ensure-only ----

    [Fact]
    public async Task APrincipalSideEdge_NoLongerDeclared_IsNeverRemoved()
    {
        var host = new EngineHost();
        var g1 = host.Directory.AddGroup("g1", "g1");
        var g2 = host.Directory.AddGroup("g2", "g2");
        var user = host.Directory.AddUser("u@contoso.com", "U");
        await AddMemberAsync(host, IdOf(g1), IdOf(user));
        await AddMemberAsync(host, IdOf(g2), IdOf(user));
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" }, \"memberOf\": [ \"" + IdOf(g1) + "\" ] } }");

        var plan = await host.WhatIfAsync(document);
        var (_, result) = await ExecuteAsync(host, document);

        Assert.DoesNotContain(plan.Changes.SelectMany(c => c.Deltas).SelectMany(d => d.Children ?? []), c => c.Kind == PropertyChangeType.Delete);
        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Contains(IdOf(user), MembersOf(host, IdOf(g2)), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAddWhoseAnswerWasLost_ButThatCommitted_CountsAsDone()
    {
        EngineHost? created = null;
        var host = new EngineHost(graph => new CommitThenFailGraphClient(graph, r => r.Path.EndsWith("/members/$ref", StringComparison.Ordinal), _ => new GraphException(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "the answer got lost", RetryHint.Default)));
        created = host;
        var group = created.Directory.AddGroup("g", "g");
        var user = created.Directory.AddUser("u@contoso.com", "U");
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" }, \"memberOf\": [ \"" + IdOf(group) + "\" ] } }");

        var (_, result) = await ExecuteAsync(host, document);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)) + string.Join("\n", result.Diagnostics));
        Assert.Contains(IdOf(user), MembersOf(host, IdOf(group)), StringComparer.OrdinalIgnoreCase);
    }

    // ---- S2-07: no blind outer retry of a create ----

    [Fact]
    public async Task AnAmbiguousFailureOfACreate_IsNeverRepeatedByTheOuterRetry_ItGoesThroughTheKeyReRead()
    {
        var host = new EngineHost();
        host.Fake.Failures.ServerError("/users", times: 1, method: "POST");

        var (_, result) = await ExecuteAsync(host, Users("u1@contoso.com"));

        Assert.False(result.Succeeded);
        Assert.Single(host.Fake.Writes, w => w.Method == "POST" && w.Path == "/users");
        Assert.Equal(0, host.Directory.Count("/users"));
        Assert.Contains(host.Fake.Requests, r => r.Method == "GET" && r.Path.StartsWith("/users", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AThrottledCreate_IsStillRetried_ItWasRejectedBeforeItWasProcessed()
    {
        var host = new EngineHost();
        host.Fake.Failures.Throttle("/users", times: 1, method: "POST");

        var (_, result) = await ExecuteAsync(host, Users("u1@contoso.com"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(2, host.Fake.Writes.Count(w => w.Method == "POST" && w.Path == "/users"));
        Assert.Equal(1, host.Directory.Count("/users"));
    }

    // ---- S2-08: a failure after the primary write ----

    private static (EngineHost Host, Review3HHookedProvider Hook) HostWithHook(string type)
    {
        var host = new EngineHost();
        var hook = new Review3HHookedProvider(host.Echo(type));
        host.Registry.Register(hook, replace: true);
        return (host, hook);
    }

    [Fact]
    public async Task AFailureAfterTheObjectWasCreated_FailsTheResource_ARerunFindsItByKeyAndConverges()
    {
        var (host, hook) = HostWithHook(Fixture.UserType);
        hook.AfterCreate = (_, _) => throw new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "licenses could not be assigned", RetryHint.Default);

        var (_, result) = await ExecuteAsync(host, Users("g1@contoso.com"));

        Assert.False(result.Succeeded);
        var op = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, op.ErrorCode);
        Assert.Contains("Run the deployment again", op.Error, StringComparison.Ordinal);
        Assert.Equal(1, host.Directory.Count("/users"));
        Assert.Equal(IdOf(host.Directory.Objects("/users").Single()), op.GraphId);
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.ResourceApplyFailed && d.Message.Contains("exists in the tenant", StringComparison.Ordinal));

        hook.AfterCreate = null;
        var (_, rerun) = await ExecuteAsync(host, Users("g1@contoso.com"));
        Assert.True(rerun.Succeeded, string.Join("\n", rerun.Operations.Select(o => o.Error)));
        Assert.Equal(1, host.Directory.Count("/users"));
    }

    [Fact]
    public async Task AProviderThatHandsOverThePartialResult_HasItsOneTimeSecretSaved()
    {
        var (host, hook) = HostWithHook(Fixture.UserType);
        using var directory = new TestDirectory(nameof(AProviderThatHandsOverThePartialResult_HasItsOneTimeSecretSaved));
        string secrets = directory.File("secure.json");
        hook.AfterCreate = (instance, applied) =>
        {
            var live = (JsonObject)applied.Live.DeepClone();
            live["passwordProfile"] = new JsonObject { ["password"] = EArm.Core.Secrets.SecretJson.CreateString("Generated-P@ss-1") };
            var partial = applied with { Live = live, Extra = new JsonObject { ["generatedPassword"] = true } };
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, "assigning the licenses failed", instance.Id) { Partial = partial };
        };
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" } } }");

        var (_, result) = await ExecuteAsync(host, document, execute: new ExecuteOptions { SecureOutputsFile = secrets });

        Assert.False(result.Succeeded);
        Assert.Equal(IdOf(host.Directory.Objects("/users").Single()), Assert.Single(result.Operations).GraphId);
        string file = await File.ReadAllTextAsync(secrets, Ct);
        Assert.Contains("generatedPassword", file, StringComparison.Ordinal);
        Assert.Contains("Generated-P@ss-1", file, StringComparison.Ordinal);
        Assert.DoesNotContain("Generated-P@ss-1", string.Join("\n", result.Operations.Select(o => o.Error)) + string.Join("\n", result.Diagnostics.Select(d => d.Message)), StringComparison.Ordinal);
    }

    // ---- S2-09: a catch-all around one resource ----

    [Fact]
    public async Task AnUnexpectedException_FailsOnlyThatResource_TheSiblingsAndTheEndPhaseStillRun()
    {
        var (host, hook) = HostWithHook(Fixture.GroupType);
        hook.BeforeCreate = instance => instance.Id.NameLeaf == "boom" ? throw new NullReferenceException("a provider bug") : Task.CompletedTask;
        var secrets = new TestDirectory(nameof(AnUnexpectedException_FailsOnlyThatResource_TheSiblingsAndTheEndPhaseStillRun));
        using var _ = secrets;
        var document = Templates.Resources(GroupDoc("ok1") + ", " + GroupDoc("boom") + ", " + GroupDoc("ok2"), []);

        var (plan, result) = await ExecuteAsync(host, document, execute: new ExecuteOptions { ContinueOnError = true });

        Assert.False(result.Succeeded);
        Assert.Equal(DeploymentStatus.Failed, result.Status);
        Assert.Equal(OperationStatus.Succeeded, result.Operations.Single(o => o.Id.Id.EndsWith("/ok1", StringComparison.Ordinal)).Status);
        Assert.Equal(OperationStatus.Succeeded, result.Operations.Single(o => o.Id.Id.EndsWith("/ok2", StringComparison.Ordinal)).Status);
        var boom = result.Operations.Single(o => o.Id.Id.EndsWith("/boom", StringComparison.Ordinal));
        Assert.Equal(OperationStatus.Failed, boom.Status);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, boom.ErrorCode);
        Assert.Contains("NullReferenceException", boom.Error, StringComparison.Ordinal);
        Assert.NotNull(plan);
    }

    // ---- S2-10: Ctrl-C in the end phase ----

    [Fact]
    public async Task TheEdgePass_StopsBetweenOperationsWhenCancelled_AndRecordsTheOnesNotStartedAsFailed()
    {
        using var source = new CancellationTokenSource();
        var graph = new CancelAfterFirstWrite(source);
        var membership = ProviderDiscovery.CreateEdgeKinds(ResourceTypeCatalog.Default);
        Assert.True(membership.TryGet(EArm.Engine.Edges.EdgeKind.GroupMembership, out var handler));
        var operations = Enumerable.Range(1, 5).Select(i => new EArm.Engine.Edges.EdgeOperation(new EArm.Engine.Edges.Edge(EArm.Engine.Edges.EdgeKind.GroupMembership, "0a0a0a0a-0000-4000-8000-000000000001", new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]).ToString("D")), EArm.Engine.Edges.EdgeAction.Add, handler, GraphApi.V1, RetryHint.Default, null)).ToList();
        var plan = new EArm.Engine.Edges.EdgePlan([], operations, []);
        var context = new EArm.Engine.Edges.EdgeGraphContext(graph, null, new LiveReferenceResolver(), [], NullLogger.Instance);

        var result = await EArm.Engine.Edges.EdgeExecutor.ApplyAsync(plan, context, source.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(1, graph.Writes);
        Assert.True(result.Results[operations[0]].Succeeded);
        Assert.All(operations.Skip(1), o =>
        {
            Assert.False(result.Results[o].Succeeded);
            Assert.Contains("cancelled", result.Results[o].Error, StringComparison.Ordinal);
        });
    }

    private sealed class CancelAfterFirstWrite : IGraphClient
    {
        private readonly CancellationTokenSource _source;
        private int _writes;

        public CancelAfterFirstWrite(CancellationTokenSource source)
        {
            _source = source;
        }

        public int Writes => Volatile.Read(ref _writes);

        private async Task<GraphResponse> WriteAsync()
        {
            Interlocked.Increment(ref _writes);
            await _source.CancelAsync();
            return new GraphResponse(HttpStatusCode.NoContent, null);
        }

        public Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken) => Task.FromResult<JsonNode?>(null);

        public Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken) => WriteAsync();

        public Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken) => WriteAsync();

        public Task<GraphResponse> PutAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken) => WriteAsync();

        public Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken) => WriteAsync();

        public async IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // ---- S2-17: an uncertain create, properties applied after "already exists" ----

    [Fact]
    public async Task AnUncertainCreate_WhoseObjectIsFoundUnderTheKey_IsNotCreatedAgain()
    {
        InterceptingGraphClient? interceptor = null;
        var host = new EngineHost(graph => interceptor = new InterceptingGraphClient(graph));
        host.Fake.Failures.ServerError("/users", times: 1, method: "POST");
        interceptor!.BeforePost = (request, _) =>
        {
            if (request.Path == "/users")
            {
                host.Directory.AddUser("old@contoso.com", "old user", g => g["createdDateTime"] = DateTimeOffset.UtcNow.AddHours(-3).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            }

            return Task.CompletedTask;
        };

        var (_, result) = await ExecuteAsync(host, Users("old@contoso.com"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
        Assert.Single(host.Directory.Objects("/users"));
    }

    [Fact]
    public async Task AnUncertainCreate_WhoseObjectWasCreatedDuringTheRun_IsStillCreated()
    {
        var host = new EngineHost(graph => new CommitThenFailGraphClient(graph, r => r.Path == "/users", _ => new GraphException(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "lost", RetryHint.Default)));

        var (_, result) = await ExecuteAsync(host, Users("fresh@contoso.com"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(ChangeType.Create, Assert.Single(result.Operations).ChangeType);
        Assert.Equal(IdOf(host.Directory.Objects("/users").Single()), Assert.Single(result.Operations).GraphId);
    }

    [Fact]
    public async Task AlreadyExists_TakesTheObjectOver_AndAppliesTheDeclaredPropertiesInTheSameRun()
    {
        EngineHost? created = null;
        var host = new EngineHost(graph => new RacingApplicationClient(graph, () => created!.Directory.AddApplication("racy", "Racy (somebody else's name)")));
        created = host;

        var (_, result) = await ExecuteAsync(host, Apps("racy"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal("racy", host.Directory.Find("/applications", "uniqueName", "racy")!["displayName"]!.GetValue<string>());
    }
}
