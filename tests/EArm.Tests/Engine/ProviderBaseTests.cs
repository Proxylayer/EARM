using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.TestSupport;
using Xunit;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Tests.Engine;

/// <summary>
/// The shared provider behaviour of <see cref="ProviderBase"/> (key resolution, plan, create/patch bodies, defaults,
/// immutability, existing-resource lookups, pinned-id reads) driven through <see cref="EchoProvider"/> on real
/// expanded instances.
/// </summary>
public sealed class ProviderBaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject J(string json)
    {
        return JsonNode.Parse(json)!.AsObject();
    }

    private static string Text(JsonNode? node)
    {
        return node is null ? "null" : node.ToJsonString();
    }

    /// <summary>Expands one inline resource and evaluates its properties, returning the instance and its provider.</summary>
    private static async Task<(EngineHost Host, ResourceInstance Instance, EchoProvider Provider)> OneAsync(string resourceJson, string resourceId, params (string Name, string Value)[] parameters)
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(Templates.Resources(resourceJson, parameters));
        var instance = EngineHost.Evaluated(plan, resourceId);
        return (host, instance, host.Echo(instance.Type));
    }

    private const string AppJson = "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"App\", \"identifierUris\": [\"api://app\"] } }";
    private const string AppId = "/providers/Microsoft.Entra/applications/app";

    // ---- plan: create / no change / modify ----

    [Fact]
    public async Task Plan_WithoutExistingObject_IsCreate()
    {
        var (_, instance, provider) = await OneAsync(AppJson, AppId);

        var change = provider.Plan(instance, null, PlanOptions.Default);

        Assert.Equal(ChangeType.Create, change.Type);
        Assert.Equal(ChangeMode.Create, change.Mode);
        Assert.Null(change.UnsupportedReason);
        Assert.Null(change.Before);
        Assert.Equal(["properties.displayName", "properties.identifierUris"], change.Deltas.Select(d => d.Path.ToString()).ToList());
        Assert.All(change.Deltas, d => Assert.Equal(PropertyChangeType.Create, d.Kind));
        Assert.Equal("\"App\"", Text(change.After!["displayName"]));
        Assert.False(change.IsUnknown);
        Assert.False(change.IsSymbolicId);
        Assert.Equal('+', change.Symbol);
    }

    [Fact]
    public async Task Plan_WithIdenticalLiveObject_IsNoChange_AfterStrippingServerManagedPaths()
    {
        var (_, instance, provider) = await OneAsync(AppJson, AppId);
        var live = J("{\"id\":\"A1\",\"uniqueName\":\"app\",\"appId\":\"11111111-1111-1111-1111-111111111111\",\"createdDateTime\":\"2026-01-01T00:00:00Z\",\"displayName\":\"App\",\"identifierUris\":[\"api://app\"],\"publisherDomain\":\"contoso.com\"}");

        var existing = provider.ToExisting(live);
        var change = provider.Plan(instance, existing, PlanOptions.Default);

        Assert.Equal("A1", existing.GraphId);
        Assert.False(existing.Properties.ContainsKey("appId"), "stripOnRead removes appId from the diff view");
        Assert.False(existing.Properties.ContainsKey("publisherDomain"));
        Assert.True(existing.Raw.ContainsKey("appId"), "the raw object keeps everything for reference()");
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.Equal(ChangeMode.None, change.Mode);
        Assert.Equal("A1", change.GraphId);
        Assert.Empty(change.Deltas);
        Assert.Equal('=', change.Symbol);
    }

    [Fact]
    public async Task Plan_WithADifferentLiveValue_IsModifyPatch_AndThePatchBodyCarriesOnlyTheChange()
    {
        var (_, instance, provider) = await OneAsync(AppJson, AppId);
        var existing = provider.ToExisting(J("{\"id\":\"A1\",\"uniqueName\":\"app\",\"displayName\":\"Old\",\"identifierUris\":[\"api://app\"],\"notes\":\"n\"}"));

        var change = provider.Plan(instance, existing, PlanOptions.Default);
        var body = provider.BuildPatchBody(instance, existing, change);

        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.Equal(ChangeMode.Patch, change.Mode);
        Assert.Null(change.UnsupportedReason);
        var delta = Assert.Single(change.Deltas);
        Assert.Equal("properties.displayName", delta.Path.ToString());
        Assert.Equal("{\"displayName\":\"App\"}", Text(body));
        Assert.Equal("\"Old\"", Text(change.Before!["displayName"]));
        Assert.Equal('~', change.Symbol);
    }

    [Fact]
    public async Task Plan_IgnoredInstance_IsIgnoreRow()
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(Templates.Parse("\"parameters\": { \"env\": { \"type\": \"string\" } }, \"resources\": [ { \"condition\": \"[equals(parameters('env'), 'prod')]\", \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"App\" } } ]", ("env", "dev")));
        var instance = EngineHost.Evaluated(plan, AppId);
        var provider = host.Echo(instance.Type);

        var change = provider.Plan(instance, null, PlanOptions.Default);

        Assert.True(instance.IsIgnored);
        Assert.Equal(ChangeType.Ignore, change.Type);
        Assert.Contains("condition evaluated to false", change.Reasons);
        Assert.Equal('*', change.Symbol);
    }

    // ---- immutability ----

    [Fact]
    public async Task Plan_ImmutableChangeOnApplications_IsUnsupported()
    {
        var (_, instance, provider) = await OneAsync("{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"App\", \"appId\": \"22222222-2222-2222-2222-222222222222\" } }", AppId);
        var existing = provider.ToExisting(J("{\"id\":\"A1\",\"uniqueName\":\"app\",\"displayName\":\"App\",\"appId\":\"11111111-1111-1111-1111-111111111111\"}"));

        var change = provider.Plan(instance, existing, PlanOptions.Default);

        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.True(change.HasError);
        Assert.Contains("Immutable property properties.appId", change.UnsupportedReason);
        Assert.Contains("delete and recreate", change.UnsupportedReason);
        Assert.NotEqual(ChangeMode.Recreate, change.Mode);
    }

    [Fact]
    public async Task Plan_ImmutableChangeOnGroups_IsUnsupported()
    {
        var (_, instance, provider) = await OneAsync("{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"mailNickname\": \"g\", \"isAssignableToRole\": true } }", "/providers/Microsoft.Entra/groups/g");
        var existing = provider.ToExisting(J("{\"id\":\"G1\",\"uniqueName\":\"g\",\"displayName\":\"G\",\"mailEnabled\":false,\"securityEnabled\":true,\"mailNickname\":\"g\",\"isAssignableToRole\":false}"));

        var change = provider.Plan(instance, existing, PlanOptions.Default);

        Assert.True(change.HasError);
        Assert.Contains("properties.isAssignableToRole", change.UnsupportedReason);
        Assert.Contains(change.Reasons, r => r.Contains("Immutable property", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_ChangeOnAnImmutableAllType_IsModifyRecreate()
    {
        // passwordCredentials: immutable ["*"] (replace, never edit).
        const string password = "/providers/Microsoft.Entra/applications/app/passwordCredentials/ci-secret";
        var (_, instance, provider) = await OneAsync(
            AppJson + ", { \"type\": \"Microsoft.Entra/applications/passwordCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app/ci-secret\", \"properties\": { \"endDateTime\": \"2027-10-01T00:00:00Z\", \"rotationTag\": \"2026Q4\" }, \"dependsOn\": [\"" + AppId + "\"] }",
            password);
        var existing = provider.ToExisting(J("{\"id\":\"K1\",\"keyId\":\"K1\",\"displayName\":\"ci-secret\",\"endDateTime\":\"2027-10-01T00:00:00Z\",\"rotationTag\":\"2026Q3\",\"hint\":\"fak\"}"));

        var change = provider.Plan(instance, existing, PlanOptions.Default);

        Assert.True(provider.Definition.ImmutableAll);
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.Equal(ChangeMode.Recreate, change.Mode);
        Assert.Null(change.UnsupportedReason);
        Assert.Contains(change.Reasons, r => r.Contains("deleted and created again", StringComparison.Ordinal));
    }

    // ---- requiredOnCreate ----

    [Theory]
    [InlineData("{ \"type\": \"Microsoft.Entra/conditionalAccess/namedLocations\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Office\", \"properties\": \"[createObject('isTrusted', true())]\" }", "/providers/Microsoft.Entra/conditionalAccess/namedLocations/Office", "@odata.type")]
    [InlineData("{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\" } }", "/providers/Microsoft.Entra/groups/g", "mailEnabled, securityEnabled")]
    [InlineData("{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"u@contoso.com\", \"properties\": { \"mailNickname\": \"u\" } }", "/providers/Microsoft.Entra/users/u@contoso.com", "displayName")]
    public async Task Plan_MissingRequiredOnCreate_IsUnsupportedCreate(string resourceJson, string resourceId, string expectedMissing)
    {
        var (_, instance, provider) = await OneAsync(resourceJson, resourceId);

        var change = provider.Plan(instance, null, PlanOptions.Default);

        Assert.Equal(ChangeType.Create, change.Type);
        Assert.True(change.HasError);
        Assert.Contains("cannot be created: required propert", change.UnsupportedReason);
        Assert.Contains(expectedMissing, change.UnsupportedReason);
    }

    [Fact]
    public async Task Plan_RequiredPropertyCoveredByAnOverlayDefault_IsNotMissing()
    {
        // conditionalAccess/policies requires state, but the overlay defaults it.
        var (_, instance, provider) = await OneAsync(
            "{ \"type\": \"Microsoft.Entra/conditionalAccess/policies\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"CA-1\", \"properties\": { \"conditions\": { \"users\": { \"includeUsers\": [\"All\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }, \"grantControls\": { \"builtInControls\": [\"mfa\"] } } }",
            "/providers/Microsoft.Entra/conditionalAccess/policies/CA-1");

        var change = provider.Plan(instance, null, PlanOptions.Default);
        var body = provider.BuildCreateBody(instance);

        Assert.False(change.HasError, change.UnsupportedReason);
        Assert.Equal("\"enabledForReportingButNotEnforced\"", Text(body["state"]));
        Assert.Equal("[\"all\"]", Text(body["conditions"]!["clientAppTypes"]));
        Assert.Equal("\"OR\"", Text(body["grantControls"]!["operator"]));
        Assert.Equal("\"CA-1\"", Text(body["displayName"]));
    }

    [Fact]
    public async Task Plan_DeployTimeUnknownRequiredProperty_CountsAsPresent_AndFlagsTheRow()
    {
        var (_, instance, provider) = await OneAsync(
            "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"other\", \"properties\": { \"displayName\": \"O\", \"mailEnabled\": false, \"securityEnabled\": true } }, " +
            "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"[reference('other').displayName]\", \"mailEnabled\": false, \"securityEnabled\": true } }",
            "/providers/Microsoft.Entra/groups/g");

        var change = provider.Plan(instance, null, PlanOptions.Default);

        Assert.True(instance.HasUnknownProperties);
        Assert.False(change.HasError);
        Assert.True(change.IsUnknown);
        Assert.Contains(change.Reasons, r => r.Contains("only known at deployment time", StringComparison.Ordinal));
        Assert.Equal("\"(deploy-time)\"", Text(change.After!["displayName"]));
    }

    // ---- create body: name injection, defaults, secrets ----

    [Fact]
    public async Task BuildCreateBody_InjectsTheNameAndTheOverlayDefaults()
    {
        var (_, instance, provider) = await OneAsync(AppJson, AppId);

        var body = provider.BuildCreateBody(instance);

        Assert.Equal("\"app\"", Text(body["uniqueName"]));
        Assert.Equal("\"AzureADMyOrg\"", Text(body["signInAudience"]));
        Assert.Equal("\"App\"", Text(body["displayName"]));
        Assert.Null(instance.Properties!["uniqueName"]);
    }

    [Fact]
    public async Task BuildCreateBody_KeepsAnExplicitValueOverTheDefault_AndToleratesAnEqualMappedValue()
    {
        var (_, instance, provider) = await OneAsync("{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"App\", \"signInAudience\": \"AzureADMultipleOrgs\", \"uniqueName\": \"app\" } }", AppId);

        var body = provider.BuildCreateBody(instance);

        Assert.Equal("\"AzureADMultipleOrgs\"", Text(body["signInAudience"]));
        Assert.Equal("\"app\"", Text(body["uniqueName"]));
    }

    [Fact]
    public async Task BuildCreateBody_DottedDefault_AndSecretReveal_ForUsers()
    {
        var template = Templates.Parse("\"parameters\": { \"pw\": { \"type\": \"securestring\" } }, \"resources\": [ { \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\", \"passwordProfile\": { \"password\": \"[parameters('pw')]\" } } } ]", ("pw", "s3cret!"));
        var host = new EngineHost();
        var plan = await host.ExpandAsync(template);
        var instance = EngineHost.Evaluated(plan, "/providers/Microsoft.Entra/users/u@contoso.com");
        var provider = host.Echo(instance.Type);

        Assert.True(SecretJson.IsSecret(instance.Properties!["passwordProfile"]!["password"]), "a securestring parameter is tainted in the evaluated properties");

        var body = provider.BuildCreateBody(instance);

        Assert.Equal("\"u@contoso.com\"", Text(body["userPrincipalName"]));
        Assert.Equal("true", Text(body["accountEnabled"]));
        Assert.Equal("true", Text(body["passwordProfile"]!["forceChangePasswordNextSignIn"]));
        Assert.Equal("\"s3cret!\"", Text(body["passwordProfile"]!["password"]));
        Assert.False(SecretJson.ContainsSecret(body), "the create body is the one place secrets are revealed");
        Assert.True(SecretJson.ContainsSecret(instance.Properties), "the instance keeps its taint");
    }

    [Fact]
    public async Task BuildCreateBody_ConflictingMappedValue_ThrowsNameMappedPropertyConflict()
    {
        // A literal conflict is caught by validation; an expression reaches the provider.
        var (_, instance, provider) = await OneAsync("{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"App\", \"uniqueName\": \"[concat('oth', 'er')]\" } }", AppId);

        var error = Assert.Throws<ProviderException>(() => provider.BuildCreateBody(instance));

        Assert.Equal(DiagnosticCodes.NameMappedPropertyConflict, error.Code);
        Assert.Equal(AppId, error.ResourceId?.Id);
        Assert.Contains("'other'", error.Message);
    }

    // ---- keys ----

    [Fact]
    public async Task ResolveKey_UsesNameMapsTo_AndTheParentKeyForChildren()
    {
        const string fic = "/providers/Microsoft.Entra/applications/app/federatedIdentityCredentials/github";
        var (host, instance, provider) = await OneAsync(
            AppJson + ", { \"type\": \"Microsoft.Entra/applications/federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app/github\", \"properties\": { \"issuer\": \"i\", \"subject\": \"s\" }, \"dependsOn\": [\"" + AppId + "\"] }",
            fic);

        var key = provider.ResolveKey(instance);
        var appKey = host.Echo(Fixture.AppType).ResolveKey(EngineHost.Evaluated(await host.ExpandAsync(Templates.Resources(AppJson)), AppId));
        Assert.NotNull(instance.Parent);

        Assert.Equal("github", key.Single);
        Assert.Equal("name", key.SingleName);
        Assert.True(key.IsResolved);
        Assert.NotNull(key.ParentKey);
        Assert.Equal("app", key.ParentKey.Single);
        Assert.Equal("uniqueName", key.ParentKey.SingleName);
        Assert.Null(key.ParentGraphId);
        Assert.Equal("uniqueName='app' / name='github'", key.Describe());
        Assert.Equal("uniqueName='app'", appKey.Describe());
        Assert.Equal(GraphApi.V1, appKey.Api);
    }

    [Fact]
    public async Task ResolveKey_GuidNamedType_IsCompositeFromProperties_AndUnknownMembersAreUnresolved()
    {
        const string assignment = "/providers/Microsoft.Entra/appRoleAssignments/11111111-1111-1111-1111-111111111111";
        var (_, instance, provider) = await OneAsync(
            "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true } }, " +
            "{ \"type\": \"Microsoft.Entra/appRoleAssignments\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"11111111-1111-1111-1111-111111111111\", \"properties\": { \"principalId\": \"[reference('g').id]\", \"resourceId\": \"33333333-3333-3333-3333-333333333333\", \"appRoleId\": \"44444444-4444-4444-4444-444444444444\" } }",
            assignment);

        var key = provider.ResolveKey(instance);

        Assert.False(key.IsResolved);
        Assert.Equal(["principalId"], key.UnresolvedProperties);
        Assert.True(key.TryGet("resourceId", out var resourceId));
        Assert.Equal("33333333-3333-3333-3333-333333333333", resourceId);
        Assert.Contains("(unresolved: principalId)", key.Describe());
        Assert.Throws<InvalidOperationException>(() => key.Single);
    }

    [Fact]
    public async Task ResolveKey_SymbolicName_IsUnresolved()
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var sp = EngineHost.Evaluated(plan, Fixture.SpSymbolic);

        var key = host.Provider(Fixture.SpType).ResolveKey(sp);

        Assert.True(sp.IsSymbolic);
        Assert.False(key.IsResolved);
        Assert.Equal(["appId"], key.UnresolvedProperties);
    }

    // ---- existing-resource lookups ----

    [Fact]
    public async Task ReadExistingAsync_RefusesGuidNamedTypes()
    {
        var host = new EngineHost();
        var provider = host.Echo(Fixture.AppRoleAssignmentType);

        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.ReadExistingAsync(ResourceIdentifier.Create(Fixture.AppRoleAssignmentType, "11111111-1111-1111-1111-111111111111"), "2026-10-01-preview", host.Graph, Ct));

        Assert.Equal(EngineDiagnosticCodes.ResourceKeyUnresolved, error.Code);
        Assert.Contains("deterministic GUID", error.Message);
        Assert.Empty(host.Fake.Requests);
    }

    [Fact]
    public async Task ReadExistingAsync_RefusesTheRetiredApiVersionWithTheRenameHint()
    {
        var host = new EngineHost();
        var provider = host.Echo(Fixture.AppType);

        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.ReadExistingAsync(ResourceIdentifier.Create(Fixture.AppType, "app"), "2026-10-01", host.Graph, Ct));

        Assert.Equal(DiagnosticCodes.UnknownApiVersion, error.Code);
        Assert.Contains("was renamed '2026-10-01-preview'", error.Message, StringComparison.Ordinal);
        Assert.Empty(host.Fake.Requests);
    }

    [Fact]
    public async Task ReadExistingAsync_RefusesAnUnknownApiVersion()
    {
        var host = new EngineHost();
        var provider = host.Echo(Fixture.AppType);

        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.ReadExistingAsync(ResourceIdentifier.Create(Fixture.AppType, "app"), "2020-01-01", host.Graph, Ct));

        Assert.Equal(DiagnosticCodes.UnknownApiVersion, error.Code);
        Assert.Contains("2026-10-01-preview", error.Message);
        Assert.Empty(host.Fake.Requests);
    }

    [Fact]
    public async Task ReadExistingAsync_ReadsByTheKeyTheIdImplies_IncludingChildren()
    {
        var host = new EngineHost();
        var app = host.Directory.AddApplication("app", "App");
        host.Directory.Add("/applications/" + app["id"] + "/federatedIdentityCredentials", J("{\"id\":\"F1\",\"name\":\"github\",\"issuer\":\"i\",\"subject\":\"s\"}"));

        var foundApp = await host.Echo(Fixture.AppType).ReadExistingAsync(ResourceIdentifier.Create(Fixture.AppType, "app"), "2026-10-01-preview", host.Graph, Ct);
        var foundFic = await host.Echo(Fixture.FicType).ReadExistingAsync(ResourceIdentifier.Create(Fixture.FicType, "app/github"), "2026-10-01-preview.beta", host.Graph, Ct);
        var missing = await host.Echo(Fixture.AppType).ReadExistingAsync(ResourceIdentifier.Create(Fixture.AppType, "nope"), "2026-10-01-preview", host.Graph, Ct);

        Assert.Equal(app["id"]!.GetValue<string>(), foundApp!.GraphId);
        Assert.Equal("F1", foundFic!.GraphId);
        Assert.Null(missing);
        Assert.Equal("/applications(uniqueName='app')", host.Fake.Requests[0].Path);
        Assert.Equal(GraphApi.V1, host.Fake.Requests[0].Api);
        // Review 3I (intentional change): the echo provider resolves a parent known only by its uniqueName to its object id first (Learn documents the child endpoints under
        // /applications/{id}, and the fake directory refuses them below a uniqueName key), so the credential read is the third request.
        Assert.Equal("/applications(uniqueName='app')", host.Fake.Requests[1].Path);
        Assert.Equal("/applications/" + app["id"] + "/federatedIdentityCredentials(name='github')", host.Fake.Requests[2].Path);
        Assert.Equal(GraphApi.Beta, host.Fake.Requests[2].Api);
        Assert.All(host.Fake.Requests, r => Assert.False(r.IsWrite));
    }

    // ---- pinned Graph id ----

    /// <summary>One row per name kind; <c>{id}</c> in the key text is the object's own id (the id-keyed kind pins the key itself).</summary>
    public static TheoryData<string, string, string> KeyReadCases()
    {
        return new TheoryData<string, string, string>
        {
            { Fixture.AppType, "uniqueName=app", "/applications" },
            { Fixture.UserType, "userPrincipalName=u@contoso.com", "/users" },
            { Fixture.SpType, "appId=11111111-1111-1111-1111-111111111111", "/servicePrincipals" },
            { Fixture.RoleDefinitionType, "displayName=Orders Support", "/roleManagement/directory/roleDefinitions" },
            { Fixture.AccrType, "id={id}", "/identity/conditionalAccess/authenticationContextClassReferences" },
            { Fixture.AppRoleAssignmentType, "principalId=p1;resourceId=r1;appRoleId=a1", "/servicePrincipals/r1/appRoleAssignedTo" },
            { Fixture.FicType, "name=github", "/applications/P1/federatedIdentityCredentials" },
        };
    }

    private static Dictionary<string, string> KeyValues(string keyText, string id)
    {
        return keyText.Replace("{id}", id, StringComparison.Ordinal).Split(';').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The parents the child-collection cases address (<c>/servicePrincipals/r1/...</c>, <c>/applications/P1/...</c>).</summary>
    private static void SeedParents(EngineHost host)
    {
        host.Directory.Add("/servicePrincipals", new JsonObject { ["id"] = "r1", ["appId"] = "22222222-2222-2222-2222-222222222222", ["displayName"] = "Resource" });
        host.Directory.Add("/applications", new JsonObject { ["id"] = "P1", ["uniqueName"] = "parent", ["displayName"] = "Parent" });
    }

    [Theory]
    [MemberData(nameof(KeyReadCases))]
    public async Task ReadAsync_FindsTheObjectByKey_ForEveryNameKind(string type, string keyText, string collection)
    {
        var host = new EngineHost();
        SeedParents(host);
        var values = KeyValues(keyText, "LIVE");
        var seeded = new JsonObject { ["id"] = "LIVE" };
        foreach (var pair in values)
        {
            seeded[pair.Key] = pair.Value;
        }

        host.Directory.Add(collection, seeded);
        var key = new ResourceKey { Type = type, Values = values, ParentGraphId = type == Fixture.FicType ? "P1" : null };

        var found = await host.Echo(type).ReadAsync(key, host.Graph, Ct);

        Assert.Equal("LIVE", found!.GraphId);
        Assert.All(host.Fake.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task ReadAsync_DisplayNameLookup_WithSeveralMatches_IsAmbiguous()
    {
        var host = new EngineHost();
        host.Directory.Add("/roleManagement/directory/roleDefinitions", J("{\"displayName\":\"Dup\"}"));
        host.Directory.Add("/roleManagement/directory/roleDefinitions", J("{\"displayName\":\"Dup\"}"));
        var key = new ResourceKey { Type = Fixture.RoleDefinitionType, Values = new Dictionary<string, string> { ["displayName"] = "Dup" } };

        var error = await Assert.ThrowsAsync<ProviderException>(() => host.Echo(Fixture.RoleDefinitionType).ReadAsync(key, host.Graph, Ct));

        Assert.Equal(ProviderException.AmbiguousMatch, error.Code);
        Assert.Contains("2 objects", error.Message);
    }

    [Fact]
    public void RequiredPermissions_AreTheOverlayApplicationPermissions()
    {
        var host = new EngineHost();

        Assert.Equal(["Application.ReadWrite.OwnedBy", "Application.ReadWrite.All"], host.Provider(Fixture.AppType).RequiredPermissions(null));
        Assert.Equal(["User.ReadWrite.All"], host.Provider(Fixture.UserType).RequiredPermissions(null));
        Assert.Equal(["2026-10-01-preview", "2026-10-01-preview.beta"], host.Provider(Fixture.AppType).ApiVersions.Keys.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(GraphApi.Beta, host.Provider(Fixture.AppType).ApiVersions["2026-10-01-preview.beta"]);
    }

    [Fact]
    public void ProviderRegistry_ReportsMissingTypes_InFirstSeenOrder()
    {
        var registry = new ProviderRegistry();
        registry.Register(new EchoProvider(ResourceTypeCatalogDefault.Find(Fixture.AppType)));

        Assert.True(registry.Contains("microsoft.entra/APPLICATIONS"));
        Assert.Equal(["Microsoft.Entra/users", "Microsoft.Entra/groups"], registry.MissingTypes([Fixture.UserType, Fixture.AppType, Fixture.GroupType, Fixture.UserType]));
        var error = Assert.Throws<UnknownProviderException>(() => registry.Get(Fixture.UserType));
        Assert.Equal([Fixture.UserType], error.Types);
        Assert.Equal([Fixture.AppType], error.RegisteredTypes);
        Assert.Contains("No resource provider is registered for type Microsoft.Entra/users", error.Message);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new EchoProvider(ResourceTypeCatalogDefault.Find(Fixture.AppType))));
    }

    private static class ResourceTypeCatalogDefault
    {
        public static EArm.Core.Types.ResourceTypeDefinition Find(string type)
        {
            return EArm.Core.Types.ResourceTypeCatalog.Default.Find(type)!;
        }
    }
}
