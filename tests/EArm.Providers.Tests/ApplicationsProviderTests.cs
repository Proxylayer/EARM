using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The applications provider end to end: request sequences, bodies, hints, adoption, soft delete, drift, deletes and permissions.</summary>
public sealed class ApplicationsProviderTests
{
    private const string Type = ApplicationsProvider.ResourceType;
    private const string Name = "test-api";
    private const string ResourceId = "/providers/Microsoft.Entra/applications/" + Name;

    private const string FullProperties = """
        {
          "displayName": "Test API",
          "identifierUris": ["api://{appId}"],
          "appRoles": [
            { "id": "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002", "value": "Orders.Admin", "displayName": "Orders admin", "description": "Full", "allowedMemberTypes": ["User"], "isEnabled": true },
            { "value": "Orders.Read", "displayName": "Orders reader", "description": "Read", "allowedMemberTypes": ["User", "Application"] }
          ],
          "api": { "oauth2PermissionScopes": [ { "value": "access_as_user", "type": "User", "adminConsentDisplayName": "Access", "adminConsentDescription": "Access as user" } ] },
          "web": { "redirectUris": ["https://localhost/signin"] },
          "owners": ["00000000-0000-0000-0000-00000000aaaa"]
        }
        """;

    private const string OwnerPlaceholder = "00000000-0000-0000-0000-00000000aaaa";

    private static string App(string properties)
    {
        return Templates.Resource(Type, Name, properties);
    }

    /// <summary>The full declaration with a seeded user as owner (the fake never lists a non-existent owner id, so it would be re-added on every run).</summary>
    private static string FullAppWithOwner(ProviderHost host)
    {
        string ownerId = host.Directory.AddUser("owner@contoso.com", "Owner")["id"]!.GetValue<string>();
        return App(FullProperties.Replace(OwnerPlaceholder, ownerId, StringComparison.Ordinal));
    }

    [Fact]
    public void Discovery_RegistersApplicationsProvider()
    {
        var registry = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);
        Assert.True(registry.Contains(Type));
        Assert.IsType<ApplicationsProvider>(registry.Get(Type));
    }

    [Fact]
    public async Task Create_IssuesTheDocumentedSequence_WithExactBodies()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(FullAppWithOwner(host)));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Create, run.OpOfType(Type).ChangeType);
        var requests = run.Requests;
        Assert.Equal("GET", requests[0].Method);
        Assert.Contains("applications(uniqueName='test-api')", requests[0].Path, StringComparison.Ordinal);
        Assert.True(requests[0].UrlParameters!.ContainsKey("$select"), "read selects the managed set");
        Assert.Equal(RetryHint.Default, requests[0].Hint);
        Assert.Equal("GET", requests[1].Method);
        Assert.Equal(ApplicationsProvider.DeletedApplicationsPath, requests[1].Path);
        Assert.Equal("uniqueName eq 'test-api'", requests[1].UrlParameters!["$filter"]);
        Assert.True(requests[1].HasHeader("ConsistencyLevel", "eventual"));

        var writes = run.Writes;
        Assert.Equal(3, writes.Count);
        Assert.Equal("POST", writes[2].Method);
        Assert.EndsWith("/owners/$ref", writes[2].Path, StringComparison.Ordinal);
        var upsert = writes[0];
        Assert.Equal("PATCH", upsert.Method);
        Assert.Contains("applications(uniqueName='test-api')", upsert.Path, StringComparison.Ordinal);
        Assert.True(upsert.HasHeader("Prefer", "create-if-missing"));
        var body = Check.Body(upsert);
        Assert.False(body.ContainsKey("identifierUris"), "deferred identifierUris are not in the upsert body");
        Assert.False(body.ContainsKey("owners"), "edge properties are not in the body");
        Assert.False(body.ContainsKey("appId"));
        Assert.False(body.ContainsKey("uniqueName"), "uniqueName is the URL key of the upsert; Graph documents it as read-only, so it is not in the body (Review 1D / G-07)");
        Assert.False(body.ContainsKey("signInAudience"), "the overlay default never rides on the upsert, which would merge it into an application that already exists");

        var app = host.Directory.Find("/applications", "uniqueName", Name)!;
        Assert.Equal("AzureADMyOrg", app["signInAudience"]!.GetValue<string>());
        string appId = app["appId"]!.GetValue<string>();
        var second = writes[1];
        Assert.Equal("PATCH", second.Method);
        Assert.Equal("/applications/" + app["id"]!.GetValue<string>(), second.Path);
        Assert.Equal(RetryHint.ExpectReplicationLag, second.Hint);
        Assert.Equal("api://" + appId, Check.Body(second)["identifierUris"]![0]!.GetValue<string>());
        Assert.Equal("api://" + appId, app["identifierUris"]![0]!.GetValue<string>());

        string expectedRoleId = ApplicationsRules.DeriveMemberId(Name, "Orders.Read").ToString("D");
        var roles = app["appRoles"]!.AsArray();
        Assert.Equal(expectedRoleId, roles[1]!["id"]!.GetValue<string>());
        Assert.True(roles[1]!["isEnabled"]!.GetValue<bool>());
        Assert.Equal(ApplicationsRules.DeriveMemberId(Name, "access_as_user").ToString("D"), app["api"]!["oauth2PermissionScopes"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rerun_IsNoChange_WithZeroWrites()
    {
        var host = new ProviderHost();
        var document = host.Resources(FullAppWithOwner(host));
        var first = await host.CreateAsync(document);
        Check.Succeeded(first);
        string expectedRoleId = ApplicationsRules.DeriveMemberId(Name, "Orders.Read").ToString("D");

        var rerun = await host.CreateAsync(document);

        Check.Succeeded(rerun);
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType(Type).ChangeType);
        Check.NoWrites(rerun);
        Assert.Equal(DeploymentStatus.Succeeded, rerun.Executed.Status);

        // Fresh tenant: the derived ids are the same.
        var fresh = new ProviderHost();
        await fresh.CreateAsync(fresh.Resources(FullAppWithOwner(fresh)));
        Assert.Equal(expectedRoleId, fresh.Directory.Find("/applications", "uniqueName", Name)!["appRoles"]![1]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task WhatIf_WritesNothing_AndExplainsTheDeferredIdentifierUris()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(App(FullProperties)));

        var change = run.ChangeOfType(Type);
        Assert.Equal(ChangeType.Create, change.Type);
        Assert.False(change.HasError);
        Check.NoWrites(run);
        Assert.Contains(change.Reasons, r => r.Contains("second PATCH", StringComparison.Ordinal));
        Assert.DoesNotContain("owners", change.After!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drift_IsCorrectedWithExactlyTheChangedProperties_ThenNoChange()
    {
        var host = new ProviderHost();
        host.Directory.AddApplication(Name, "Old name", configure: app =>
        {
            app["identifierUris"] = new JsonArray("api://old");
            app["web"] = new JsonObject
            {
                ["redirectUris"] = new JsonArray("https://old"),
                ["homePageUrl"] = null,
                ["implicitGrantSettings"] = new JsonObject { ["enableAccessTokenIssuance"] = false, ["enableIdTokenIssuance"] = false },
            };
            app["api"] = new JsonObject { ["requestedAccessTokenVersion"] = null, ["oauth2PermissionScopes"] = new JsonArray() };
        });
        const string properties = """{ "displayName": "New name", "identifierUris": ["api://new"], "web": { "redirectUris": ["https://new"] }, "api": { "oauth2PermissionScopes": [] } }""";

        var run = await host.CreateAsync(host.Resources(App(properties)));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Modify, run.OpOfType(Type).ChangeType);
        var write = Assert.Single(run.Writes);
        Assert.Equal("PATCH", write.Method);
        Assert.Equal("""{"displayName":"New name","identifierUris":["api://new"],"web":{"redirectUris":["https://new"]}}""", write.Body!.ToJsonString());

        var rerun = await host.CreateAsync(host.Resources(App(properties)));
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType(Type).ChangeType);
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task RoleRemoval_AndValueChange_TakeTwoPatches_DescriptionChangeOne()
    {
        var host = new ProviderHost();
        host.Directory.AddApplication(Name, "Test API", configure: app =>
        {
            app["appRoles"] = new JsonArray(
                new JsonObject { ["id"] = "aaaaaaaa-0000-0000-0000-000000000001", ["value"] = "R1", ["displayName"] = "R1", ["description"] = "R1", ["allowedMemberTypes"] = new JsonArray("User"), ["isEnabled"] = true, ["origin"] = "Application" },
                new JsonObject { ["id"] = "aaaaaaaa-0000-0000-0000-000000000002", ["value"] = "R2", ["displayName"] = "R2", ["description"] = "R2", ["allowedMemberTypes"] = new JsonArray("User"), ["isEnabled"] = true, ["origin"] = "Application" });
        });

        // R2 removed: only R2 is disabled first.
        const string removal = """{ "displayName": "Test API", "appRoles": [ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "value": "R1", "displayName": "R1", "description": "R1", "allowedMemberTypes": ["User"], "isEnabled": true } ] }""";
        var run = await host.CreateAsync(host.Resources(App(removal)));
        Check.Succeeded(run);
        var writes = run.Writes;
        Assert.Equal(2, writes.Count);
        var first = Check.Body(writes[0])["appRoles"]!.AsArray();
        Assert.Equal(2, first.Count);
        Assert.True(first[0]!["isEnabled"]!.GetValue<bool>());
        Assert.False(first[1]!["isEnabled"]!.GetValue<bool>());
        Assert.False(((JsonObject)first[1]!).ContainsKey("origin"), "the server-managed origin is never sent");
        var final = Check.Body(writes[1])["appRoles"]!.AsArray();
        Assert.Equal("R1", Assert.Single(final)!["value"]!.GetValue<string>());
        Assert.Single(host.Directory.Find("/applications", "uniqueName", Name)!["appRoles"]!.AsArray());

        // Value change on R1: disabled first as well.
        const string valueChange = """{ "displayName": "Test API", "appRoles": [ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "value": "R1.v2", "displayName": "R1", "description": "R1", "allowedMemberTypes": ["User"], "isEnabled": true } ] }""";
        var change = await host.CreateAsync(host.Resources(App(valueChange)));
        Check.Succeeded(change);
        writes = change.Writes;
        Assert.Equal(2, writes.Count);
        Assert.False(Check.Body(writes[0])["appRoles"]![0]!["isEnabled"]!.GetValue<bool>());
        Assert.Equal("R1.v2", Check.Body(writes[1])["appRoles"]![0]!["value"]!.GetValue<string>());

        // Description-only change: one PATCH.
        const string descriptionChange = """{ "displayName": "Test API", "appRoles": [ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "value": "R1.v2", "displayName": "R1", "description": "Renamed", "allowedMemberTypes": ["User"], "isEnabled": true } ] }""";
        var description = await host.CreateAsync(host.Resources(App(descriptionChange)));
        Check.Succeeded(description);
        Assert.Single(description.Writes);

        // What-if of a removal explains the two-step.
        var whatIf = await host.WhatIfAsync(host.Resources(App("""{ "displayName": "Test API", "appRoles": [] }""")));
        Assert.Contains(whatIf.ChangeOfType(Type).Reasons, r => r.Contains("disabled by a first PATCH", StringComparison.Ordinal));
        Check.NoWrites(whatIf);
    }

    [Fact]
    public async Task Upsert204_AdoptsTheExistingObject_ByReReadWithReplicationHint()
    {
        var host = new ProviderHost();
        var seeded = host.Directory.AddApplication(Name, "Old name");
        host.Fake.Failures.ReplicationLag("applications(uniqueName='test-api')", times: 1, method: "GET");

        var run = await host.CreateAsync(host.Resources(App("""{ "displayName": "New name" }""")));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Create, run.OpOfType(Type).ChangeType);
        Assert.Equal(seeded["id"]!.GetValue<string>(), run.OpOfType(Type).GraphId);
        var write = Assert.Single(run.Writes);
        Assert.True(write.HasHeader("Prefer", "create-if-missing"));
        Assert.Equal(1, host.Directory.Count("/applications"));
        Assert.Equal("New name", host.Directory.Find("/applications", "uniqueName", Name)!["displayName"]!.GetValue<string>());
        var reads = run.Requests.Where(r => r.Method == "GET" && r.Path.Contains("uniqueName=", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, reads.Count);
        Assert.Equal(RetryHint.ExpectReplicationLag, reads[1].Hint);
        Assert.Equal(seeded["id"]!.GetValue<string>(), run.GraphIdOf(ResourceId));
    }

    [Fact]
    public async Task SoftDeleted_IsAConflict_UnlessRestoreIsRequested()
    {
        var host = new ProviderHost();
        var deleted = host.Directory.AddDeleted("/applications", new JsonObject { ["uniqueName"] = Name, ["displayName"] = "Old name", ["appId"] = Guid.NewGuid().ToString("D") });

        var run = await host.CreateAsync(host.Resources(App("""{ "displayName": "New name" }""")));

        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        Assert.Equal(ProviderException.SoftDeletedConflict, run.OpOfType(Type).ErrorCode);
        Check.NoWrites(run);

        host.OnSoftDeletedConflict = SoftDeletedConflict.Restore;
        var restore = await host.CreateAsync(host.Resources(App("""{ "displayName": "New name" }""")));

        Check.Succeeded(restore);
        var writes = restore.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Equal("POST", writes[0].Method);
        Assert.Equal("/directory/deletedItems/" + deleted["id"]!.GetValue<string>() + "/restore", writes[0].Path);
        Assert.Equal("PATCH", writes[1].Method);
        Assert.True(writes[1].HasHeader("Prefer", "create-if-missing"));
        Assert.Equal(1, host.Directory.Count("/applications"));
        Assert.Equal(0, host.Directory.Count("/directory/deletedItems"));
        Assert.Equal("New name", host.Directory.Find("/applications", "uniqueName", Name)!["displayName"]!.GetValue<string>());
        Assert.Equal(deleted["id"]!.GetValue<string>(), restore.OpOfType(Type).GraphId);
    }

    [Fact]
    public async Task ValueCollision_IsUnsupported_AndFailsBeforeAnyWrite()
    {
        const string properties = """
            { "displayName": "X", "appRoles": [ { "value": "dup", "displayName": "a", "description": "a", "allowedMemberTypes": ["User"] } ],
              "api": { "oauth2PermissionScopes": [ { "value": "dup", "type": "User", "adminConsentDisplayName": "a", "adminConsentDescription": "a" } ] } }
            """;
        var host = new ProviderHost();

        var whatIf = await host.WhatIfAsync(host.Resources(App(properties)));
        var row = whatIf.ChangeOfType(Type);
        Assert.True(row.HasError);
        Assert.Contains("more than once", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(App(properties)));
        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task ALiveUniqueName_ThatDiffersOnlyInCase_IsTheSameApplication_NotAnImmutableChange()
    {
        // Graph alternate keys are case-insensitive: the lookup finds 'Test-API' for the name 'test-api', so the live-vs-declared check must agree (B19).
        var host = new ProviderHost();
        var seeded = host.Directory.AddApplication("Test-API", "Test API");

        var whatIf = await host.WhatIfAsync(host.Resources(App("""{ "displayName": "Test API" }""")));
        var change = whatIf.ChangeOfType(Type);
        Assert.False(change.HasError, change.UnsupportedReason);
        Assert.Equal(seeded["id"]!.GetValue<string>(), change.GraphId);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Check.NoWrites(whatIf);

        var run = await host.CreateAsync(host.Resources(App("""{ "displayName": "Renamed" }""")));
        Check.Succeeded(run);
        Assert.Equal(seeded["id"]!.GetValue<string>(), run.GraphIdOf(ResourceId));
        Assert.Equal(1, host.Directory.Count("/applications"));
    }

    [Fact]
    public async Task UniqueNameExpression_ThatDiffersFromTheName_IsAnError_EqualIsTolerated()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(App("""{ "displayName": "X", "uniqueName": "[concat('other', '-name')]" }""")));
        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        Assert.Contains("conflicts with the resource name", run.OpOfType(Type).Error, StringComparison.Ordinal);
        Check.NoWrites(run);

        var equal = await new ProviderHost().CreateAsync(host.Resources(App("""{ "displayName": "X", "uniqueName": "[concat('test', '-api')]" }""")));
        Check.Succeeded(equal);
    }

    [Fact]
    public async Task DeclaredAppId_AdoptsABrownfieldApplication_AndNeverWritesAppId()
    {
        var host = new ProviderHost();
        string appId = Guid.NewGuid().ToString("D");
        var seeded = host.Directory.Add("/applications", new JsonObject { ["displayName"] = "Brownfield", ["appId"] = appId });
        string properties = "{ \"displayName\": \"Brownfield\", \"appId\": \"" + appId + "\" }";

        var run = await host.CreateAsync(host.Resources(App(properties)));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Modify, run.OpOfType(Type).ChangeType);
        Assert.Equal(seeded["id"]!.GetValue<string>(), run.OpOfType(Type).GraphId);
        var write = Assert.Single(run.Writes);
        Assert.Equal("/applications/" + seeded["id"]!.GetValue<string>(), write.Path);
        var body = Check.Body(write);
        Assert.Equal(Name, body["uniqueName"]!.GetValue<string>());
        Assert.False(body.ContainsKey("appId"));
        Assert.Contains(run.Requests, r => r.Method == "GET" && r.Path.Contains("appId=", StringComparison.Ordinal));

        var rerun = await host.CreateAsync(host.Resources(App(properties)));
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType(Type).ChangeType);
        Check.NoWrites(rerun);
        Assert.DoesNotContain(rerun.Requests, r => r.Path.Contains("appId=", StringComparison.Ordinal));

        var missing = await new ProviderHost().CreateAsync(host.Resources(App("""{ "displayName": "X", "appId": "11111111-2222-3333-4444-555555555555" }""")));
        Assert.Equal(DeploymentStatus.Failed, missing.Executed.Status);
        Assert.Contains("Graph assigns the appId", missing.OpOfType(Type).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclaredAppId_DifferingFromTheLiveOne_IsAnImmutableChange()
    {
        var host = new ProviderHost();
        host.Directory.AddApplication(Name, "Test API", appId: Guid.NewGuid().ToString("D"));

        var whatIf = await host.WhatIfAsync(host.Resources(App("""{ "displayName": "Test API", "appId": "11111111-2222-3333-4444-555555555555" }""")));

        var row = whatIf.ChangeOfType(Type);
        Assert.True(row.HasError, row.UnsupportedReason);
        Assert.Contains("appId", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(App("""{ "displayName": "Test API", "appId": "11111111-2222-3333-4444-555555555555" }""")));
        Assert.Equal(OperationStatus.Failed, run.OpOfType(Type).Status);
        Assert.Equal(EngineDiagnosticCodes.ImmutablePropertyChanged, run.OpOfType(Type).ErrorCode);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task Delete_IssuesDeleteById_AndToleratesNotFound()
    {
        var host = new ProviderHost();
        var seeded = host.Directory.AddApplication(Name, "Test API");
        var provider = host.Provider<ApplicationsProvider>(Type);
        var entry = new DeleteTarget
        {
            Id = ResourceIdentifier.Create(Type, Name),
            Type = Type,
            ApiVersion = ProviderHost.ApiVersion,
            Key = new Dictionary<string, string> { ["uniqueName"] = Name },
            GraphId = seeded["id"]!.GetValue<string>(),
        };

        await provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct);

        var write = Assert.Single(host.Fake.Writes);
        Assert.Equal("DELETE", write.Method);
        Assert.Equal("/applications/" + entry.GraphId, write.Path);
        Assert.Equal(0, host.Directory.Count("/applications"));

        await provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct);
        Assert.Equal(2, host.Fake.Writes.Count);
    }

    [Fact]
    public async Task Rerun_ReadsByUniqueName_AndAReplacedObjectIsFoundByKey_RePinningTheState()
    {
        var host = new ProviderHost();
        var first = await host.CreateAsync(host.Resources(App("""{ "displayName": "Test API" }""")));
        Check.Succeeded(first);
        string oldId = first.GraphIdOf(ResourceId);

        var rerun = await host.CreateAsync(host.Resources(App("""{ "displayName": "Test API" }""")));
        Assert.Contains("applications(uniqueName='test-api')", rerun.Requests[0].Path, StringComparison.Ordinal);
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType(Type).ChangeType);

        // The recorded object vanished; another object carries the uniqueName now.
        await host.Fake.DeleteAsync(new GraphRequest("/applications/" + oldId), ProviderHost.Ct);
        var replacement = host.Directory.AddApplication(Name, "Test API");
        string newId = replacement["id"]!.GetValue<string>();

        var drift = await host.CreateAsync(host.Resources(App("""{ "displayName": "Test API" }""")));

        Check.Succeeded(drift);
        Assert.Contains("applications(uniqueName='test-api')", drift.Requests[0].Path, StringComparison.Ordinal);
        Assert.DoesNotContain(drift.Requests, r => r.Path == "/applications/" + oldId);
        Assert.Equal(ChangeType.NoChange, drift.OpOfType(Type).ChangeType);
        Assert.Equal(newId, drift.GraphIdOf(ResourceId));
    }

    [Fact]
    public async Task RequiredPermissions_AreOwnedByOnlyWhenOwnersAreDeclared()
    {
        var host = new ProviderHost();
        Assert.Equal([ApplicationsProvider.ReadWriteAllPermission], host.Provider(Type).RequiredPermissions(null));

        var withOwners = await host.WhatIfAsync(host.Resources(App(FullProperties)));
        Assert.Equal([ApplicationsProvider.OwnedByPermission], withOwners.Plan.RequiredPermissions.Order());

        var withoutOwners = await host.WhatIfAsync(host.Resources(App("""{ "displayName": "X" }""")));
        Assert.Equal([ApplicationsProvider.ReadWriteAllPermission], withoutOwners.Plan.RequiredPermissions.Order());
    }
}
