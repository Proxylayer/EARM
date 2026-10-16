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

/// <summary>The service principals provider end to end.</summary>
public sealed class ServicePrincipalsProviderTests
{
    private const string AppType = ApplicationsProvider.ResourceType;
    private const string SpType = ServicePrincipalsProvider.ResourceType;
    private const string AppName = "test-api";

    private const string AppResource = """
        { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "test-api",
          "properties": { "displayName": "Test API",
            "appRoles": [ { "id": "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002", "value": "Orders.Admin", "displayName": "Orders admin", "description": "Full", "allowedMemberTypes": ["User"], "isEnabled": true } ] } }
        """;

    /// <summary>Matches what <c>AddApplication(AppName, "Test API", appId)</c> seeds, so the app itself is NoChange.</summary>
    private const string AppMinimalResource = """
        { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "test-api", "properties": { "displayName": "Test API" } }
        """;

    private const string AppWithOwnersResource = """
        { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "test-api",
          "properties": { "displayName": "Test API", "owners": ["00000000-0000-0000-0000-00000000aaaa"] } }
        """;

    private const string SpProperties = """{ "appRoleAssignmentRequired": true, "tags": ["WindowsAzureActiveDirectoryIntegratedApp"], "notes": "managed by EARM" }""";

    private const string SpResource =
        "{ \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[reference('test-api').appId]\", "
        + "\"properties\": " + SpProperties + ", "
        + "\"dependsOn\": [\"[resourceId('Microsoft.Entra/applications', 'test-api')]\"] }";

    private static string LiteralSp(string appId, string properties, string? dependsOn = null)
    {
        return "{ \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + appId + "\", \"properties\": " + properties
            + (dependsOn is null ? string.Empty : ", \"dependsOn\": [\"" + dependsOn + "\"]") + " }";
    }

    [Fact]
    public void Discovery_RegistersServicePrincipalsProvider_WithExistingLookup()
    {
        var registry = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);
        Assert.IsType<ServicePrincipalsProvider>(registry.Get(SpType));
        Assert.IsAssignableFrom<IExistingResourceLookup>(registry.Get(SpType));
    }

    [Fact]
    public async Task Create_PostsTheExactBody_WithReplicationHint_AndRerunIsNoChange()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(AppResource + "," + SpResource));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Create, run.OpOfType(SpType).ChangeType);
        string appId = host.Directory.Find("/applications", "uniqueName", AppName)!["appId"]!.GetValue<string>();
        var post = Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == "/servicePrincipals");
        Assert.Equal(RetryHint.ExpectReplicationLag, post.Hint);
        Assert.Equal("{\"appId\":\"" + appId + "\",\"accountEnabled\":true,\"appRoleAssignmentRequired\":true,\"tags\":[\"WindowsAzureActiveDirectoryIntegratedApp\"],\"notes\":\"managed by EARM\"}", post.Body!.ToJsonString());
        var read = Assert.Single(run.Requests, r => r.Method == "GET" && r.Path == "/servicePrincipals(appId='" + appId + "')");
        Assert.Contains("appRoles", read.UrlParameters!["$select"], StringComparison.Ordinal);
        Assert.Contains("samlSingleSignOnSettings", read.UrlParameters["$select"], StringComparison.Ordinal);
        Assert.Equal(RetryHint.Default, read.Hint);
        var sp = host.Directory.Find("/servicePrincipals", "appId", appId)!;
        Assert.Equal(sp["id"]!.GetValue<string>(), run.OpOfType(SpType).GraphId);
        Assert.Single(sp["appRoles"]!.AsArray());
        Assert.Equal(1, host.Directory.Count("/servicePrincipals"));
        Assert.Equal(sp["id"]!.GetValue<string>(), run.GraphIdOf("/providers/Microsoft.Entra/servicePrincipals/" + appId));

        var rerun = await host.CreateAsync(host.Resources(AppResource + "," + SpResource));
        Check.Succeeded(rerun);
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType(SpType).ChangeType);
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType(AppType).ChangeType);
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task ReplicationBadRequest_Twice_IsRetried_ThreePosts()
    {
        var host = new ProviderHost();
        host.Fake.Failures.ReplicationBadRequest("/servicePrincipals", times: 2, method: "POST");

        var run = await host.CreateAsync(host.Resources(AppResource + "," + SpResource));

        Check.Succeeded(run);
        Assert.Equal(3, run.Writes.Count(w => w.Method == "POST" && w.Path == "/servicePrincipals"));
        Assert.Equal(2, host.Delays.Count);
        Assert.Equal(0, host.Fake.Failures.PendingRules);
        Assert.Equal(1, host.Directory.Count("/servicePrincipals"));
    }

    [Fact]
    public async Task WhatIf_ShowsASymbolicCreateRow_WithDefaults_AndWritesNothing()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(AppResource + "," + SpResource));

        var change = run.ChangeOfType(SpType);
        Assert.Equal(ChangeType.Create, change.Type);
        Assert.False(change.HasError);
        Assert.True(change.IsSymbolicId);
        Check.NoWrites(run);
        string after = change.After!.ToJsonString();
        Assert.Contains("\"accountEnabled\":true", after, StringComparison.Ordinal);
        Assert.Contains("\"appRoleAssignmentRequired\":true", after, StringComparison.Ordinal);
        Assert.DoesNotContain("owners", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TagsDrift_IsOnePatchWithTheFullArray_AndReorderIsNoChange()
    {
        var host = new ProviderHost();
        string appId = Guid.NewGuid().ToString("D");
        host.Directory.AddApplication(AppName, "Test API", appId);
        var seeded = host.Directory.AddServicePrincipal(appId, "Test API", sp =>
        {
            sp["tags"] = new JsonArray("a", "b");
            sp["appRoleAssignmentRequired"] = true;
            sp["notes"] = null;
        });

        var run = await host.CreateAsync(host.Resources(LiteralSp(appId, """{ "appRoleAssignmentRequired": true, "tags": ["a", "c"] }""")));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Modify, run.OpOfType(SpType).ChangeType);
        var write = Assert.Single(run.Writes);
        Assert.Equal("PATCH", write.Method);
        Assert.Equal("/servicePrincipals/" + seeded["id"]!.GetValue<string>(), write.Path);
        Assert.Equal("""{"tags":["a","c"]}""", write.Body!.ToJsonString());

        var rerun = await host.CreateAsync(host.Resources(LiteralSp(appId, """{ "appRoleAssignmentRequired": true, "tags": ["c", "a"] }""")));
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType(SpType).ChangeType);
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task UndeclaredDefaults_OnAnExistingPrincipal_AreLeftAlone_DeclaredOnesAreEnforced_AndNestedObjectsArePatchedWhole()
    {
        string appId = Guid.NewGuid().ToString("D");
        var disabled = new ProviderHost();
        disabled.Directory.AddApplication(AppName, "Test API", appId);
        disabled.Directory.AddServicePrincipal(appId, "Test API", sp =>
        {
            sp["accountEnabled"] = false;
            sp["appRoleAssignmentRequired"] = true;
        });

        // Neither default is declared: the disabled, assignment-required principal is left as it is.
        var untouched = await disabled.CreateAsync(disabled.Resources(LiteralSp(appId, """{ "tags": [] }""")));
        Check.Succeeded(untouched);
        Assert.Equal(ChangeType.NoChange, untouched.OpOfType(SpType).ChangeType);
        Check.NoWrites(untouched);
        var live = disabled.Directory.Find("/servicePrincipals", "appId", appId)!;
        Assert.False(live["accountEnabled"]!.GetValue<bool>());
        Assert.True(live["appRoleAssignmentRequired"]!.GetValue<bool>());

        // Declared, the same values are enforced.
        var fix = await disabled.CreateAsync(disabled.Resources(LiteralSp(appId, """{ "accountEnabled": true, "appRoleAssignmentRequired": false }""")));
        Assert.Equal(ChangeType.Modify, fix.OpOfType(SpType).ChangeType);
        Assert.Equal("""{"accountEnabled":true,"appRoleAssignmentRequired":false}""", Assert.Single(fix.Writes).Body!.ToJsonString());

        var saml = new ProviderHost();
        saml.Directory.AddApplication(AppName, "Test API", appId);
        saml.Directory.AddServicePrincipal(appId, "Test API", sp =>
        {
            sp["appRoleAssignmentRequired"] = false;
            sp["samlSingleSignOnSettings"] = new JsonObject { ["relayState"] = "old" };
        });

        var samlRun = await saml.CreateAsync(saml.Resources(LiteralSp(appId, """{ "samlSingleSignOnSettings": { "relayState": "new" } }""")));
        Assert.Equal(ChangeType.Modify, samlRun.OpOfType(SpType).ChangeType);
        Assert.Equal("""{"samlSingleSignOnSettings":{"relayState":"new"}}""", Assert.Single(samlRun.Writes).Body!.ToJsonString());
    }

    [Fact]
    public async Task AlreadyExistsOnPost_AdoptsThePrincipal_AndPatchesOnlyWhenDeclaredPropertiesDiffer()
    {
        string appId = Guid.NewGuid().ToString("D");
        var host = new ProviderHost();
        host.Directory.AddApplication(AppName, "Test API", appId);
        var seeded = host.Directory.AddServicePrincipal(appId, "Test API", sp =>
        {
            sp["appRoleAssignmentRequired"] = true;
            sp["tags"] = new JsonArray("WindowsAzureActiveDirectoryIntegratedApp");
            sp["notes"] = "managed by EARM";
        });
        host.Fake.Failures.ReplicationLag("servicePrincipals(appId=", times: 1, method: "GET");

        var run = await host.CreateAsync(host.Resources(AppMinimalResource + "," + SpResource));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Create, run.OpOfType(SpType).ChangeType);
        Assert.Equal(ChangeType.NoChange, run.OpOfType(AppType).ChangeType);
        Assert.Equal(seeded["id"]!.GetValue<string>(), run.OpOfType(SpType).GraphId);
        Assert.Equal(1, host.Directory.Count("/servicePrincipals"));
        var write = Assert.Single(run.Writes);
        Assert.Equal("POST", write.Method);
        var reads = run.Requests.Where(r => r.Method == "GET" && r.Path.Contains("servicePrincipals(appId=", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, reads.Count);
        Assert.Equal(RetryHint.ExpectReplicationLag, reads[1].Hint);

        var drift = new ProviderHost();
        drift.Directory.AddApplication(AppName, "Test API", appId);
        var old = drift.Directory.AddServicePrincipal(appId, "Test API", sp =>
        {
            sp["appRoleAssignmentRequired"] = true;
            sp["tags"] = new JsonArray("WindowsAzureActiveDirectoryIntegratedApp");
            sp["notes"] = "old";
        });
        drift.Fake.Failures.ReplicationLag("servicePrincipals(appId=", times: 1, method: "GET");

        var adopt = await drift.CreateAsync(drift.Resources(AppMinimalResource + "," + SpResource));

        Check.Succeeded(adopt);
        var writes = adopt.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Equal("PATCH", writes[1].Method);
        Assert.Equal("/servicePrincipals/" + old["id"]!.GetValue<string>(), writes[1].Path);
        Assert.Equal(RetryHint.ExpectReplicationLag, writes[1].Hint);
        Assert.Equal("""{"notes":"managed by EARM"}""", writes[1].Body!.ToJsonString());
        Assert.Equal(1, drift.Directory.Count("/servicePrincipals"));
        Assert.Equal("managed by EARM", drift.Directory.Find("/servicePrincipals", "appId", appId)!["notes"]!.GetValue<string>());
    }

    [Fact]
    public async Task AlreadyExistsOnPost_TheAdoptedPrincipal_DoesNotReceiveTheCreateDefaults()
    {
        string appId = Guid.NewGuid().ToString("D");
        var host = new ProviderHost();
        host.Directory.AddApplication(AppName, "Test API", appId);
        var seeded = host.Directory.AddServicePrincipal(appId, "Test API", sp =>
        {
            sp["accountEnabled"] = false;
            sp["appRoleAssignmentRequired"] = true;
            sp["tags"] = new JsonArray("WindowsAzureActiveDirectoryIntegratedApp");
            sp["notes"] = "managed by EARM";
        });
        host.Fake.Failures.ReplicationLag("servicePrincipals(appId=", times: 1, method: "GET");

        var run = await host.CreateAsync(host.Resources(AppMinimalResource + "," + SpResource));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Create, run.OpOfType(SpType).ChangeType);
        Assert.Equal(seeded["id"]!.GetValue<string>(), run.OpOfType(SpType).GraphId);

        // The POST carried the create defaults; the adoption that followed the "already exists" answer did not PATCH them.
        var write = Assert.Single(run.Writes);
        Assert.Equal("POST", write.Method);
        Assert.Contains("\"accountEnabled\":true", write.Body!.ToJsonString(), StringComparison.Ordinal);
        Assert.False(host.Directory.Find("/servicePrincipals", "appId", appId)!["accountEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ExistingLookup_ResolvesASeededFirstPartyPrincipal_WithoutWritingIt()
    {
        var host = new ProviderHost();
        var graphSp = host.Directory.AddServicePrincipal(ProviderHost.GraphAppId, "Microsoft Graph");
        const string outputs = """ "graphSpId": { "type": "string", "value": "[reference(resourceId('Microsoft.Entra/servicePrincipals', '00000003-0000-0000-c000-000000000000'), '2026-10-01-preview').id]" } """;

        var run = await host.CreateAsync(host.Resources(AppResource, outputs));

        Check.Succeeded(run);
        Assert.Empty(run.Plan.UnresolvedReferences);
        Assert.Equal(graphSp["id"]!.GetValue<string>(), run.Output("graphSpId")!.GetValue<string>());
        var read = Assert.Single(run.Requests, r => r.Method == "GET" && r.Path == "/servicePrincipals(appId='" + ProviderHost.GraphAppId + "')");
        Assert.True(read.UrlParameters!.ContainsKey("$select"));
        Assert.DoesNotContain(run.Writes, w => w.Path == "/servicePrincipals");

        var provider = host.Provider<ServicePrincipalsProvider>(SpType);
        var existing = await provider.ReadExistingAsync(ResourceIdentifier.Create(SpType, ProviderHost.GraphAppId), ProviderHost.ApiVersion, host.Fake, ProviderHost.Ct);
        Assert.NotNull(existing);
        Assert.Equal(graphSp["id"]!.GetValue<string>(), existing.GraphId);
        Assert.False(existing.Properties.ContainsKey("appRoles"), "appRoles are stripped from the diff view");
        Assert.True(existing.Raw.ContainsKey("appRoles"), "appRoles stay in Raw for reference()");
        Assert.Null(await provider.ReadExistingAsync(ResourceIdentifier.Create(SpType, Guid.NewGuid().ToString("D")), ProviderHost.ApiVersion, host.Fake, ProviderHost.Ct));
    }

    [Fact]
    public async Task Delete_IssuesDeleteById_AndToleratesNotFound()
    {
        var host = new ProviderHost();
        string appId = Guid.NewGuid().ToString("D");
        var seeded = host.Directory.AddServicePrincipal(appId, "Test API");
        var provider = host.Provider<ServicePrincipalsProvider>(SpType);
        var entry = new DeleteTarget
        {
            Id = ResourceIdentifier.Create(SpType, appId),
            Type = SpType,
            ApiVersion = ProviderHost.ApiVersion,
            Key = new Dictionary<string, string> { ["appId"] = appId },
            GraphId = seeded["id"]!.GetValue<string>(),
        };

        await provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct);
        var write = Assert.Single(host.Fake.Writes);
        Assert.Equal("DELETE", write.Method);
        Assert.Equal("/servicePrincipals/" + entry.GraphId, write.Path);
        Assert.Equal(0, host.Directory.Count("/servicePrincipals"));

        await provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct);
        Assert.Equal(2, host.Fake.Writes.Count);
    }

    [Fact]
    public async Task RequiredPermissions_DependOnWhetherTheApplicationIsInTheTemplate()
    {
        var host = new ProviderHost();
        Assert.Equal([ServicePrincipalsProvider.ReadWriteAllPermission], host.Provider(SpType).RequiredPermissions(null));

        var templateApp = await host.WhatIfAsync(host.Resources(AppWithOwnersResource + "," + SpResource));
        Assert.Equal([ServicePrincipalsProvider.OwnedByPermission], templateApp.Plan.RequiredPermissions.Order());

        var literal = await host.WhatIfAsync(host.Resources(LiteralSp(ProviderHost.GraphAppId, "{ }")));
        Assert.Equal([ServicePrincipalsProvider.ReadWriteAllPermission], literal.Plan.RequiredPermissions.Order());

        var viaDependsOn = await host.WhatIfAsync(host.Resources(AppWithOwnersResource + "," + LiteralSp(ProviderHost.GraphAppId, "{ }", "[resourceId('Microsoft.Entra/applications', 'test-api')]")));
        Assert.Equal([ServicePrincipalsProvider.OwnedByPermission], viaDependsOn.Plan.RequiredPermissions.Order());
    }

    [Fact]
    public async Task NonGuidName_FailsWithoutWriting_AndDeclaredAppIdMustEqualTheName()
    {
        const string byDisplayName = """
            { "type": "Microsoft.Entra/servicePrincipals", "apiVersion": "2026-10-01-preview", "name": "[reference('test-api').displayName]",
              "properties": { }, "dependsOn": ["[resourceId('Microsoft.Entra/applications', 'test-api')]"] }
            """;
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(AppResource + "," + byDisplayName));
        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        Assert.Contains("not an application (client) id", run.OpOfType(SpType).Error, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Writes, w => w.Path == "/servicePrincipals");

        string appId = Guid.NewGuid().ToString("D");
        var seededHost = new ProviderHost();
        seededHost.Directory.AddApplication(AppName, "Test API", appId);
        const string appDependency = "[resourceId('Microsoft.Entra/applications', 'test-api')]";

        var conflict = await seededHost.CreateAsync(seededHost.Resources(AppMinimalResource + "," + LiteralSp(Guid.NewGuid().ToString("D"), "{ \"appId\": \"[reference('test-api').appId]\" }", appDependency)));
        Assert.Equal(DeploymentStatus.Failed, conflict.Executed.Status);
        Assert.Contains("conflicts with the resource name", conflict.OpOfType(SpType).Error, StringComparison.Ordinal);
        Check.NoWrites(conflict);

        var equal = await seededHost.CreateAsync(seededHost.Resources(AppMinimalResource + "," + LiteralSp(appId, "{ \"appId\": \"[toUpper(reference('test-api').appId)]\" }", appDependency)));
        Check.Succeeded(equal);
        Assert.Equal(ChangeType.Create, equal.OpOfType(SpType).ChangeType);
        Assert.Equal(appId, Check.Body(Assert.Single(equal.Writes, w => w.Method == "POST"))["appId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ServicePrincipalType_IsImmutable()
    {
        var host = new ProviderHost();
        string appId = Guid.NewGuid().ToString("D");
        host.Directory.AddApplication(AppName, "Test API", appId);
        host.Directory.AddServicePrincipal(appId, "Test API", sp => sp["appRoleAssignmentRequired"] = false);

        var same = await host.WhatIfAsync(host.Resources(LiteralSp(appId, """{ "servicePrincipalType": "Application" }""")));
        Assert.Equal(ChangeType.NoChange, same.ChangeOfType(SpType).Type);

        var changed = await host.WhatIfAsync(host.Resources(LiteralSp(appId, """{ "servicePrincipalType": "ManagedIdentity" }""")));
        var row = changed.ChangeOfType(SpType);
        Assert.True(row.HasError, row.UnsupportedReason);
        Assert.Contains("servicePrincipalType", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(LiteralSp(appId, """{ "servicePrincipalType": "ManagedIdentity" }""")));
        Assert.Equal(EngineDiagnosticCodes.ImmutablePropertyChanged, run.OpOfType(SpType).ErrorCode);
        Check.NoWrites(run);
    }
}
