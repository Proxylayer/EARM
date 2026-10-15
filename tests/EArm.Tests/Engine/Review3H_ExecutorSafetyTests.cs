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
using EArm.Engine.State;
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

    public Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken) => _inner.ReadAsync(key, stateId, graph, cancellationToken);

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

    public Task DeleteAsync(StateEntry entry, IGraphClient graph, CancellationToken cancellationToken) => _inner.DeleteAsync(entry, graph, cancellationToken);

    public Task<ExistingResource?> ReadExistingAsync(ResourceIdentifier id, string apiVersion, IGraphClient graph, CancellationToken cancellationToken) => _inner.ReadExistingAsync(id, apiVersion, graph, cancellationToken);
}

/// <summary>
/// Review round 2, findings S2-03 (a recreate bypassed the delete guards), S2-05 (a failed principal under <c>--continue-on-error</c> lost its live edges), S2-06 (edge records
/// lost), S2-07 (blind outer retry of a create), S2-08 (a failure after the primary write lost the object and its one-time secret), S2-09 (no catch-all), S2-10 (Ctrl-C
/// in the end phase), S2-17 (provenance after an uncertain create).
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
    public async Task ARecreate_OfAnObjectTheStateDoesNotVouchFor_IsRefused_NothingIsDeletedOrCreated()
    {
        var host = new EngineHost();
        SeedApplicationWithCredential(host);

        var (_, result, _) = await ExecuteAsync(host, NewState(), Templates.Resources(PasswordFixtureDoc()));

        Assert.False(result.Succeeded);
        var credential = Assert.Single(result.Operations, o => o.Type.EndsWith("passwordCredentials", StringComparison.Ordinal));
        Assert.Equal(OperationStatus.Failed, credential.Status);
        Assert.Equal(EngineDiagnosticCodes.AdoptedObjectNotDeleted, credential.ErrorCode);
        Assert.Contains("--delete-adopted", credential.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Path.EndsWith("Password", StringComparison.Ordinal));
        Assert.Single(host.Directory.Objects("/applications").Single()["passwordCredentials"]!.AsArray());
    }

    [Fact]
    public async Task ARecreate_IsPlannedAndCountedAsADelete_InTheWhatIf_AndRefusedForAnAdoptedObject()
    {
        var host = new EngineHost();
        SeedApplicationWithCredential(host);
        var document = Templates.Resources(PasswordFixtureDoc());

        var refused = await host.WhatIfAsync(document, NewState());
        var allowed = await host.WhatIfAsync(document, NewState(), DeploymentMode.Incremental, host.Options() with { DeleteAdopted = true });

        var refusedRow = Assert.Single(refused.Changes, c => c.ResourceType.EndsWith("passwordCredentials", StringComparison.Ordinal));
        Assert.True(refusedRow.HasError);
        Assert.Contains("--delete-adopted", refusedRow.UnsupportedReason, StringComparison.Ordinal);
        var allowedRow = Assert.Single(allowed.Changes, c => c.ResourceType.EndsWith("passwordCredentials", StringComparison.Ordinal));
        Assert.False(allowedRow.HasError);
        Assert.Equal(ChangeMode.Recreate, allowedRow.Mode);
        Assert.Contains(allowedRow.Reasons, r => r.Contains("DELETED and created again", StringComparison.Ordinal));
        Assert.Contains(allowed.Diagnostics, d => d.Code == EngineDiagnosticCodes.DestructiveDelete && d.Message.Contains("--max-deletes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARecreate_OfAnObjectThisDeploymentCreated_Proceeds_AndCountsAgainstTheDeleteLimit()
    {
        var host = new EngineHost();
        var app = SeedApplicationWithCredential(host);
        var credentialEntry = new StateEntry
        {
            Id = StateSerializer.ParseResourceId("/providers/Microsoft.Entra/applications/app/passwordCredentials/ci", Fixture.PasswordType)!,
            Type = Fixture.PasswordType,
            ApiVersion = ApiVersion,
            Key = new Dictionary<string, string> { ["displayName"] = "ci" },
            GraphId = "K1",
            ParentGraphId = IdOf(app),
            Provenance = StateProvenance.Created,
        };
        var allowedState = NewState();
        allowedState.Upsert(Entry(AppId("app"), Fixture.AppType, IdOf(app), "uniqueName", "app"));
        allowedState.Upsert(credentialEntry);

        var (_, replaced, _) = await ExecuteAsync(host, allowedState, Templates.Resources(PasswordFixtureDoc()));

        Assert.True(replaced.Succeeded, string.Join("\n", replaced.Operations.Select(o => o.Error)));
        Assert.Contains(host.Fake.Writes, w => w.Path.EndsWith("/removePassword", StringComparison.Ordinal));

        // The same replacement with a budget of zero deletes: the old object is not touched.
        var host2 = new EngineHost();
        var app2 = SeedApplicationWithCredential(host2);
        var state2 = NewState();
        state2.Upsert(Entry(AppId("app"), Fixture.AppType, IdOf(app2), "uniqueName", "app"));
        state2.Upsert(credentialEntry with { ParentGraphId = IdOf(app2) });

        var (_, limited, _) = await ExecuteAsync(host2, state2, Templates.Resources(PasswordFixtureDoc()), execute: new ExecuteOptions { MaxDeletes = 0 });

        var credential = Assert.Single(limited.Operations, o => o.Type.EndsWith("passwordCredentials", StringComparison.Ordinal));
        Assert.Equal(EngineDiagnosticCodes.DeleteLimitExceeded, credential.ErrorCode);
        Assert.DoesNotContain(host2.Fake.Writes, w => w.Path.EndsWith("Password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheMaxDeletesLimit_IsAHardLimitOfTheRun_TheModePassStopsWhenItIsUsedUp()
    {
        var host = new EngineHost();
        var state = NewState();
        foreach (string name in new[] { "a", "b", "c" })
        {
            state.Upsert(Entry(AppId(name), Fixture.AppType, IdOf(host.Directory.AddApplication(name, name)), "uniqueName", name));
        }

        var (_, result, _) = await ExecuteAsync(host, state, Empty(), DeploymentMode.Complete, execute: new ExecuteOptions { MaxDeletes = 1 });

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1, host.Directory.Count("/applications") == 2 ? 1 : 0);
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.DeleteLimitExceeded && d.IsError);
        Assert.Equal(2, state.Resources.Count);
        Assert.Contains(result.Operations, o => o.ErrorCode == EngineDiagnosticCodes.DeleteLimitExceeded);
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

        var (_, result, _) = await ExecuteAsync(host, NewState(), document, execute: new ExecuteOptions { ContinueOnError = true });

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

        var plan = await host.WhatIfAsync(document, NewState());

        Assert.True(plan.Changes.Single(c => c.Id.Id.EndsWith("u1@contoso.com", StringComparison.Ordinal)).HasError);
        var groupRow = plan.Changes.Single(c => c.Id.Id == GroupId("g"));
        var members = groupRow.Deltas.SingleOrDefault(d => d.Path.ToString() == "properties.members");
        Assert.True(members is null || members.Children is null || members.Children.All(c => c.Kind != PropertyChangeType.Delete), "no member may be planned for removal");
        Assert.Contains(plan.Diagnostics, d => d.Message.Contains("was not pruned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARecordedEdge_WhoseDeclarationMovedToTheTargetSide_StaysRecorded()
    {
        var host = new EngineHost();
        var group = host.Directory.AddGroup("g", "g");
        var user = host.Directory.AddUser("u@contoso.com", "U");
        await AddMemberAsync(host, IdOf(group), IdOf(user));
        var state = NewState();
        state.Upsert(Entry("/providers/Microsoft.Entra/users/u@contoso.com", Fixture.UserType, IdOf(user), "userPrincipalName", "u@contoso.com") with
        {
            Relationships = new JsonObject { ["memberOf"] = new JsonArray(IdOf(group)) },
        });
        string userBody = "\"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" }";
        var document = Templates.Resources(
            GroupDoc("g", ", \"members\": [ \"" + IdOf(user) + "\" ]") + ", "
            + "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\", " + userBody + " } }");

        var (_, result, _) = await ExecuteAsync(host, state, document);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        var recorded = Assert.IsType<JsonArray>(state.Find("/providers/Microsoft.Entra/users/u@contoso.com")!.Relationships!["memberOf"]);
        Assert.Equal([IdOf(group)], recorded.Select(n => n!.GetValue<string>()).ToList());
    }

    [Fact]
    public async Task WithoutAFailedPrincipal_TheAuthoritativeSetStillPrunesWhatIsNotDeclared()
    {
        var host = new EngineHost();
        var group = host.Directory.AddGroup("g", "g");
        var stranger = host.Directory.AddUser("stranger@contoso.com", "Stranger");
        await AddMemberAsync(host, IdOf(group), IdOf(stranger));
        var document = Templates.Resources(GroupDoc("g", ", \"members\": []"));

        var (_, result, _) = await ExecuteAsync(host, NewState(), document, execute: new ExecuteOptions { ContinueOnError = true });

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.DoesNotContain(IdOf(stranger), MembersOf(host, IdOf(group)), StringComparer.OrdinalIgnoreCase);
    }

    // ---- S2-06: edge records ----

    [Fact]
    public async Task ARecordedEdge_WhoseRemovalFails_StaysRecorded_SoALaterRunCanRemoveIt()
    {
        var host = new EngineHost();
        var g1 = host.Directory.AddGroup("g1", "g1");
        var g2 = host.Directory.AddGroup("g2", "g2");
        var user = host.Directory.AddUser("u@contoso.com", "U");
        await AddMemberAsync(host, IdOf(g1), IdOf(user));
        await AddMemberAsync(host, IdOf(g2), IdOf(user));
        var state = NewState();
        state.Upsert(Entry("/providers/Microsoft.Entra/users/u@contoso.com", Fixture.UserType, IdOf(user), "userPrincipalName", "u@contoso.com") with
        {
            Relationships = new JsonObject { ["memberOf"] = new JsonArray(IdOf(g1), IdOf(g2)) },
        });
        host.Fake.Failures.Forbid("/groups/" + IdOf(g2) + "/members", "DELETE");
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" }, \"memberOf\": [ \"" + IdOf(g1) + "\" ] } }");

        var (_, result, _) = await ExecuteAsync(host, state, document);

        Assert.False(result.Succeeded);
        var recorded = Assert.IsType<JsonArray>(state.Find("/providers/Microsoft.Entra/users/u@contoso.com")!.Relationships!["memberOf"]);
        var ids = recorded.Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains(IdOf(g1), ids, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(IdOf(g2), ids, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARecordedEdge_ThatWasRemoved_IsNoLongerRecorded()
    {
        var host = new EngineHost();
        var g1 = host.Directory.AddGroup("g1", "g1");
        var g2 = host.Directory.AddGroup("g2", "g2");
        var user = host.Directory.AddUser("u@contoso.com", "U");
        await AddMemberAsync(host, IdOf(g1), IdOf(user));
        await AddMemberAsync(host, IdOf(g2), IdOf(user));
        var state = NewState();
        state.Upsert(Entry("/providers/Microsoft.Entra/users/u@contoso.com", Fixture.UserType, IdOf(user), "userPrincipalName", "u@contoso.com") with
        {
            Relationships = new JsonObject { ["memberOf"] = new JsonArray(IdOf(g1), IdOf(g2)) },
        });
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" }, \"memberOf\": [ \"" + IdOf(g1) + "\" ] } }");

        var (_, result, _) = await ExecuteAsync(host, state, document);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        var recorded = Assert.IsType<JsonArray>(state.Find("/providers/Microsoft.Entra/users/u@contoso.com")!.Relationships!["memberOf"]);
        Assert.Equal([IdOf(g1)], recorded.Select(n => n!.GetValue<string>()).ToList());
    }

    [Fact]
    public async Task AnAddWhoseAnswerWasLost_ButThatCommitted_IsRecordedAsCreatedByTheDeployment()
    {
        EngineHost? created = null;
        var host = new EngineHost(graph => new CommitThenFailGraphClient(graph, r => r.Path.EndsWith("/members/$ref", StringComparison.Ordinal), _ => new GraphException(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "the answer got lost", RetryHint.Default)));
        created = host;
        var group = created.Directory.AddGroup("g", "g");
        var user = created.Directory.AddUser("u@contoso.com", "U");
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" }, \"memberOf\": [ \"" + IdOf(group) + "\" ] } }");
        var state = NewState();

        var (_, result, _) = await ExecuteAsync(host, state, document);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)) + string.Join("\n", result.Diagnostics));
        Assert.Contains(IdOf(user), MembersOf(host, IdOf(group)), StringComparer.OrdinalIgnoreCase);
        var recorded = Assert.IsType<JsonArray>(state.Find("/providers/Microsoft.Entra/users/u@contoso.com")!.Relationships!["memberOf"]);
        Assert.Equal([IdOf(group)], recorded.Select(n => n!.GetValue<string>()).ToList());
    }

    // ---- S2-07: no blind outer retry of a create ----

    [Fact]
    public async Task AnAmbiguousFailureOfACreate_IsNeverRepeatedByTheOuterRetry_ItGoesThroughTheKeyReRead()
    {
        var host = new EngineHost();
        host.Fake.Failures.ServerError("/users", times: 1, method: "POST");

        var (_, result, _) = await ExecuteAsync(host, NewState(), Users("u1@contoso.com"));

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

        var (_, result, _) = await ExecuteAsync(host, NewState(), Users("u1@contoso.com"));

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
    public async Task AFailureAfterTheObjectWasCreated_RecordsTheObject_AndFailsTheResource_ARerunConverges()
    {
        var (host, hook) = HostWithHook(Fixture.UserType);
        hook.AfterCreate = (_, _) => throw new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "licenses could not be assigned", RetryHint.Default);
        var state = NewState();

        var (_, result, _) = await ExecuteAsync(host, state, Users("g1@contoso.com"));

        Assert.False(result.Succeeded);
        var op = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, op.ErrorCode);
        Assert.Contains("recorded in the state", op.Error, StringComparison.Ordinal);
        Assert.Equal(1, host.Directory.Count("/users"));
        var entry = state.Find(UserResourceId("g1@contoso.com"));
        Assert.NotNull(entry);
        Assert.Equal(StateProvenance.Created, entry.Provenance);
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.ResourceApplyFailed && d.Message.Contains("exists in the tenant", StringComparison.Ordinal));

        hook.AfterCreate = null;
        var (_, rerun, _) = await ExecuteAsync(host, state, Users("g1@contoso.com"));
        Assert.True(rerun.Succeeded, string.Join("\n", rerun.Operations.Select(o => o.Error)));
        Assert.Equal(1, host.Directory.Count("/users"));
    }

    [Fact]
    public async Task AProviderThatHandsOverThePartialResult_HasItsObjectAndItsOneTimeSecretRecorded()
    {
        var (host, hook) = HostWithHook(Fixture.UserType);
        using var directory = new TestDirectory(nameof(AProviderThatHandsOverThePartialResult_HasItsObjectAndItsOneTimeSecretRecorded));
        string secrets = directory.File("secure.json");
        hook.AfterCreate = (instance, applied) =>
        {
            var live = (JsonObject)applied.Live.DeepClone();
            live["passwordProfile"] = new JsonObject { ["password"] = EArm.Core.Secrets.SecretJson.CreateString("Generated-P@ss-1") };
            var partial = applied with { Live = live, Extra = new JsonObject { ["generatedPassword"] = true } };
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, "assigning the licenses failed", instance.Id) { Partial = partial };
        };
        var state = NewState();
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" } } }");

        var (_, result, _) = await ExecuteAsync(host, state, document, execute: new ExecuteOptions { SecureOutputsFile = secrets });

        Assert.False(result.Succeeded);
        Assert.NotNull(state.Find(UserResourceId("u@contoso.com")));
        string file = await File.ReadAllTextAsync(secrets, Ct);
        Assert.Contains("generatedPassword", file, StringComparison.Ordinal);
        Assert.Contains("Generated-P@ss-1", file, StringComparison.Ordinal);
        Assert.DoesNotContain("Generated-P@ss-1", string.Join("\n", result.Operations.Select(o => o.Error)) + string.Join("\n", result.Diagnostics.Select(d => d.Message)), StringComparison.Ordinal);
    }

    private static string UserResourceId(string upn)
    {
        return "/providers/Microsoft.Entra/users/" + upn;
    }

    // ---- S2-09: a catch-all around one resource ----

    [Fact]
    public async Task AnUnexpectedException_FailsOnlyThatResource_TheSiblingsAndTheEndPhaseStillRun()
    {
        var (host, hook) = HostWithHook(Fixture.GroupType);
        hook.BeforeCreate = instance => instance.Id.NameLeaf == "boom" ? throw new NullReferenceException("a provider bug") : Task.CompletedTask;
        var state = NewState();
        var secrets = new TestDirectory(nameof(AnUnexpectedException_FailsOnlyThatResource_TheSiblingsAndTheEndPhaseStillRun));
        using var _ = secrets;
        var document = Templates.Resources(GroupDoc("ok1") + ", " + GroupDoc("boom") + ", " + GroupDoc("ok2"), []);

        var (plan, result, store) = await ExecuteAsync(host, state, document, execute: new ExecuteOptions { ContinueOnError = true });

        Assert.False(result.Succeeded);
        Assert.Equal(DeploymentStatus.Failed, result.Status);
        Assert.Equal(OperationStatus.Succeeded, result.Operations.Single(o => o.Id.Id.EndsWith("/ok1", StringComparison.Ordinal)).Status);
        Assert.Equal(OperationStatus.Succeeded, result.Operations.Single(o => o.Id.Id.EndsWith("/ok2", StringComparison.Ordinal)).Status);
        var boom = result.Operations.Single(o => o.Id.Id.EndsWith("/boom", StringComparison.Ordinal));
        Assert.Equal(OperationStatus.Failed, boom.Status);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, boom.ErrorCode);
        Assert.Contains("NullReferenceException", boom.Error, StringComparison.Ordinal);
        Assert.Equal(StateFile.StatusFailed, state.LastStatus);
        Assert.NotEqual(StateFile.StatusRunning, StateSerializer.Deserialize(store.DocumentOf(EngineHost.DeploymentName)!, EngineHost.DeploymentName).LastStatus);
        Assert.NotNull(plan);
    }

    [Fact]
    public async Task AnEngineFailureOutsideAnyResource_StillRunsTheEndPhase_TheStateIsNeverLeftRunning()
    {
        var host = new EngineHost();
        var sink = new ThrowingSkipSink();
        var state = NewState();
        var document = Templates.Resources(UserDoc("first@contoso.com") + ", " + UserDoc("second@contoso.com", "first@contoso.com"));
        host.Fake.Failures.Forbid("/users", "POST");
        var plan = await host.ExpandAsync(document, state);

        var result = await host.Executor().ExecuteAsync(plan, new ExecuteOptions { StateSink = sink, State = state }, Ct);

        Assert.Equal(DeploymentStatus.Failed, result.Status);
        Assert.Contains("Completed:Failed", sink.Calls);
        Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("unexpected error", StringComparison.Ordinal));
    }

    /// <summary>A sink whose "skipped" record fails with a bug-like exception (not an I/O error): the batch loop itself throws.</summary>
    private sealed class ThrowingSkipSink : IStateSink
    {
        public List<string> Calls { get; } = [];

        public Task ResourceAppliedAsync(ResourceInstance instance, ResourceChange change, AppliedResource applied, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResourceFailedAsync(ResourceInstance instance, string error, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResourceSkippedAsync(ResourceInstance instance, string reason, CancellationToken cancellationToken) => throw new InvalidCastException("a sink bug");

        public Task CompletedAsync(DeploymentStatus status, CancellationToken cancellationToken)
        {
            Calls.Add("Completed:" + status);
            return Task.CompletedTask;
        }
    }

    // ---- S2-10: Ctrl-C in the end phase ----

    private sealed class CancelOnDelete : GraphClientDecorator
    {
        private readonly CancellationTokenSource _source;
        private readonly int _afterDeletes;
        private int _deletes;

        public CancelOnDelete(IGraphClient inner, CancellationTokenSource source, int afterDeletes)
            : base(inner)
        {
            _source = source;
            _afterDeletes = afterDeletes;
        }

        public int Deletes => Volatile.Read(ref _deletes);

        public override async Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken)
        {
            await base.DeleteAsync(request, cancellationToken);
            if (Interlocked.Increment(ref _deletes) == _afterDeletes)
            {
                await _source.CancelAsync();
            }
        }
    }

    [Fact]
    public async Task ACtrlCDuringTheCompleteModeDeletes_StopsTheNextDelete_RecordsTheRestAsSkipped_AndEndsCancelled()
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        CancelOnDelete? canceller = null;
        var host = new EngineHost(graph => canceller = new CancelOnDelete(graph, source, 2));
        var state = NewState();
        foreach (string name in new[] { "a", "b", "c", "d", "e" })
        {
            state.Upsert(Entry(AppId(name), Fixture.AppType, IdOf(host.Directory.AddApplication(name, name)), "uniqueName", name));
        }

        var store = new InMemoryStateStore();
        var plan = await host.ExpandAsync(Empty(), state, DeploymentMode.Complete);
        var sink = new FileStateSink(store, state, plan) { SaveRetryBaseDelay = TimeSpan.FromMilliseconds(1) };
        await sink.BeginAsync(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Executor().ExecuteAsync(plan, new ExecuteOptions { StateSink = sink, State = state }, source.Token));

        Assert.Equal(2, canceller!.Deletes);
        Assert.Equal(3, host.Directory.Count("/applications"));
        Assert.Equal(3, state.Resources.Count);
        Assert.Equal(StateFile.StatusCancelled, state.LastStatus);
        Assert.Equal(3, state.Operations.Count(o => o.Status == OperationStatus.Succeeded && o.ChangeType == ChangeType.Delete) + 1);
        Assert.Equal(2, state.Operations.Count(o => o.Status == OperationStatus.Succeeded && o.ChangeType == ChangeType.Delete));
    }

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

    // ---- S2-17: provenance after an uncertain create, properties applied after "already exists" ----

    [Fact]
    public async Task AnUncertainCreate_WhoseObjectWasCreatedLongBeforeTheRun_IsAdopted_NotCreated()
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
        var state = NewState();

        var (_, result, _) = await ExecuteAsync(host, state, Users("old@contoso.com"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(StateProvenance.Adopted, state.Find(UserResourceId("old@contoso.com"))!.Provenance);
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.AdoptedExistingObject);
    }

    [Fact]
    public async Task AnUncertainCreate_WhoseObjectWasCreatedDuringTheRun_IsStillCreated()
    {
        var host = new EngineHost(graph => new CommitThenFailGraphClient(graph, r => r.Path == "/users", _ => new GraphException(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "lost", RetryHint.Default)));
        var state = NewState();

        var (_, result, _) = await ExecuteAsync(host, state, Users("fresh@contoso.com"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(StateProvenance.Created, state.Find(UserResourceId("fresh@contoso.com"))!.Provenance);
    }

    [Fact]
    public async Task AlreadyExists_AdoptsTheObject_AndAppliesTheDeclaredPropertiesInTheSameRun()
    {
        EngineHost? created = null;
        var host = new EngineHost(graph => new RacingApplicationClient(graph, () => created!.Directory.AddApplication("racy", "Racy (somebody else's name)")));
        created = host;
        var state = NewState();

        var (_, result, _) = await ExecuteAsync(host, state, Apps("racy"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(StateProvenance.Adopted, state.Find(AppId("racy"))!.Provenance);
        Assert.Equal("racy", host.Directory.Find("/applications", "uniqueName", "racy")!["displayName"]!.GetValue<string>());
    }
}
