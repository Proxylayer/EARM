using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>App role assignments and OAuth2 permission grants: pure rules and end-to-end scenarios.</summary>
public sealed class AssignmentsAndGrantsTests
{
    private const string AssignmentType = AppRoleAssignmentsProvider.ResourceType;
    private const string GrantType = OAuth2PermissionGrantsProvider.ResourceType;
    private const string RoleId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002";
    private const string ScopeId = "7d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0003";
    private const string ResourceAppId = "11111111-2222-3333-4444-555555555555";

    private static ServicePrincipalSummary Resource(bool roleEnabled = true, string memberTypes = "\"User\", \"Application\"")
    {
        return ServicePrincipalLookup.Summarize((JsonObject)JsonNode.Parse("{ \"id\": \"r1\", \"appId\": \"" + ResourceAppId + "\", \"displayName\": \"Orders API\", \"appRoles\": [ { \"id\": \"" + RoleId + "\", \"value\": \"Orders.Read\", \"isEnabled\": " + (roleEnabled ? "true" : "false") + ", \"allowedMemberTypes\": [" + memberTypes + "] } ], \"oauth2PermissionScopes\": [ { \"id\": \"" + ScopeId + "\", \"value\": \"access_as_user\", \"isEnabled\": true }, { \"id\": \"8d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0004\", \"value\": \"legacy\", \"isEnabled\": false } ] }")!);
    }

    // ---- app role assignment rules ----

    [Fact]
    public void Parse_AcceptsTheCanonicalAndSugarForms()
    {
        var canonical = AppRoleAssignmentsRules.Parse((JsonObject)JsonNode.Parse("{ \"principalId\": \"" + Guid.NewGuid() + "\", \"resourceId\": \"" + Guid.NewGuid() + "\", \"appRoleId\": \"" + RoleId + "\" }")!, principalAllowed: true, out var error);
        Assert.Null(error);
        Assert.NotNull(canonical);
        Assert.Equal(RoleId, canonical.AppRoleId);
        Assert.False(canonical.HasUnknown);

        var sugar = AppRoleAssignmentsRules.Parse((JsonObject)JsonNode.Parse("{ \"resourceAppId\": \"" + ResourceAppId + "\", \"appRole\": \"Orders.Read\" }")!, principalAllowed: false, out error);
        Assert.Null(error);
        Assert.Equal(ResourceAppId, sugar!.ResourceAppId);
        Assert.Equal("Orders.Read", sugar.AppRole);
        Assert.Null(sugar.PrincipalId);
    }

    [Theory]
    [InlineData("{ \"bogus\": 1, \"resourceId\": \"x\", \"appRoleId\": \"y\" }", "is not a property")]
    [InlineData("{ \"appRoleId\": \"6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002\" }", "needs the resource")]
    [InlineData("{ \"resourceAppId\": \"11111111-2222-3333-4444-555555555555\" }", "needs the resource")]
    [InlineData("{ \"resourceAppId\": \"not-a-guid\", \"appRole\": \"r\" }", "is not a GUID")]
    [InlineData("{ \"resourceAppId\": \"11111111-2222-3333-4444-555555555555\", \"appRole\": 5 }", "non-empty string")]
    public void Parse_RejectsBadShapes(string json, string expectedText)
    {
        var parsed = AppRoleAssignmentsRules.Parse((JsonObject)JsonNode.Parse(json)!, principalAllowed: true, out var error);

        Assert.Null(parsed);
        Assert.Contains(expectedText, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ForbidsAPrincipalOnPrincipalSideItems()
    {
        var parsed = AppRoleAssignmentsRules.Parse((JsonObject)JsonNode.Parse("{ \"principalId\": \"" + Guid.NewGuid() + "\", \"resourceAppId\": \"" + ResourceAppId + "\", \"appRole\": \"r\" }")!, principalAllowed: false, out var error);
        Assert.Null(parsed);
        Assert.Contains("implied by the declaring resource", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRole_ByValue_ById_AndDefaultAccess_AndTheErrors()
    {
        var resource = Resource();

        Assert.Null(AppRoleAssignmentsRules.ResolveRole(resource, null, "Orders.Read", out var id, out var role));
        Assert.Equal(RoleId, id);
        Assert.NotNull(role);

        Assert.Null(AppRoleAssignmentsRules.ResolveRole(resource, RoleId.ToUpperInvariant(), null, out id, out role));
        Assert.Equal(RoleId, id);

        Assert.Null(AppRoleAssignmentsRules.ResolveRole(resource, AppRoleAssignmentsRules.DefaultAccessRoleId, null, out id, out role));
        Assert.Equal(AppRoleAssignmentsRules.DefaultAccessRoleId, id);
        Assert.Null(role);

        Assert.Contains("no app role with value 'Nope'", AppRoleAssignmentsRules.ResolveRole(resource, null, "Nope", out _, out _), StringComparison.Ordinal);
        Assert.Contains("defined values: Orders.Read", AppRoleAssignmentsRules.ResolveRole(resource, null, "Nope", out _, out _), StringComparison.Ordinal);
        Assert.Contains("has no app role with id", AppRoleAssignmentsRules.ResolveRole(resource, Guid.NewGuid().ToString("D"), null, out _, out _), StringComparison.Ordinal);
        Assert.Contains("differs from the declared appRoleId", AppRoleAssignmentsRules.ResolveRole(resource, Guid.NewGuid().ToString("D"), "Orders.Read", out _, out _), StringComparison.Ordinal);
        Assert.Contains("needs appRoleId or appRole", AppRoleAssignmentsRules.ResolveRole(resource, null, null, out _, out _), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("users", "User")]
    [InlineData("Groups", "User")]
    [InlineData("servicePrincipals", "Application")]
    [InlineData("#microsoft.graph.user", "User")]
    [InlineData("#microsoft.graph.servicePrincipal", "Application")]
    [InlineData("applications", null)]
    [InlineData("", null)]
    public void MemberTypeOf_MapsCollectionsAndODataTypes(string input, string? expected)
    {
        Assert.Equal(expected, AppRoleAssignmentsRules.MemberTypeOf(input));
    }

    [Fact]
    public void MemberTypeOfResourceType_AndCheckAssignable()
    {
        Assert.Equal("User", AppRoleAssignmentsRules.MemberTypeOfResourceType("Microsoft.Entra/users"));
        Assert.Equal("Application", AppRoleAssignmentsRules.MemberTypeOfResourceType("Microsoft.Entra/servicePrincipals"));

        var userOnly = (JsonObject)JsonNode.Parse("""{ "id": "r", "value": "Orders.Read", "allowedMemberTypes": ["User"] }""")!;
        Assert.True(AppRoleAssignmentsRules.RestrictsMemberType(userOnly));
        Assert.Null(AppRoleAssignmentsRules.CheckAssignable(userOnly, "User"));
        Assert.Null(AppRoleAssignmentsRules.CheckAssignable(userOnly, null));
        Assert.Contains("cannot be assigned to a service principal", AppRoleAssignmentsRules.CheckAssignable(userOnly, "Application"), StringComparison.Ordinal);
        Assert.False(AppRoleAssignmentsRules.RestrictsMemberType((JsonObject)JsonNode.Parse("""{ "allowedMemberTypes": ["User", "Application"] }""")!));
        Assert.Contains("is disabled", AppRoleAssignmentsRules.CheckAssignable((JsonObject)JsonNode.Parse("""{ "value": "x", "isEnabled": false }""")!, "User"), StringComparison.Ordinal);
    }

    [Fact]
    public void TargetKeysAndIdentity_AreCaseInsensitiveGuids()
    {
        string key = AppRoleAssignmentsRules.TargetKey(ResourceAppId.ToUpperInvariant(), RoleId.ToUpperInvariant());
        Assert.Equal(ResourceAppId + "/" + RoleId, key);
        Assert.True(AppRoleAssignmentsRules.TryParseTargetKey(key, out var resourceId, out var appRoleId));
        Assert.Equal(ResourceAppId, resourceId);
        Assert.Equal(RoleId, appRoleId);
        Assert.False(AppRoleAssignmentsRules.TryParseTargetKey("plain", out _, out _));
        Assert.False(AppRoleAssignmentsRules.TryParseTargetKey("a/b", out _, out _));
        Assert.False(AppRoleAssignmentsRules.TryParseTargetKey(null, out _, out _));
        Assert.Equal(
            AppRoleAssignmentsRules.Identity(ResourceAppId, ResourceAppId, RoleId),
            AppRoleAssignmentsRules.Identity(ResourceAppId.ToUpperInvariant(), ResourceAppId.ToUpperInvariant(), RoleId.ToUpperInvariant()));
        Assert.True(AppRoleAssignmentsRules.Matches((JsonObject)JsonNode.Parse("{ \"principalId\": \"" + ResourceAppId.ToUpperInvariant() + "\", \"appRoleId\": \"" + RoleId + "\" }")!, ResourceAppId, RoleId));
        Assert.False(AppRoleAssignmentsRules.Matches((JsonObject)JsonNode.Parse("{ \"principalId\": \"" + ResourceAppId + "\", \"appRoleId\": \"x\" }")!, ResourceAppId, RoleId));
        Assert.True(AppRoleAssignmentsRules.IsDefaultAccess("00000000-0000-0000-0000-000000000000"));
    }

    // ---- permission grant rules ----

    [Theory]
    [InlineData("AllPrincipals", "AllPrincipals")]
    [InlineData(" allprincipals ", "AllPrincipals")]
    [InlineData("principal", "Principal")]
    [InlineData("Both", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeConsentType_AcceptsTwoValues(string? input, string? expected)
    {
        Assert.Equal(expected, OAuth2PermissionGrantsRules.NormalizeConsentType(input));
    }

    [Fact]
    public void ScopeStrings_AreSplitDeDuplicatedAndComparedAsSets()
    {
        Assert.Equal(["a", "b", "c"], OAuth2PermissionGrantsRules.Tokens("  a   b\tc a "));
        Assert.Empty(OAuth2PermissionGrantsRules.Tokens(" "));
        Assert.Empty(OAuth2PermissionGrantsRules.Tokens(null));
        Assert.Equal("a b c", OAuth2PermissionGrantsRules.Normalize("a  b c a"));
        Assert.True(OAuth2PermissionGrantsRules.SameSet("a b", "b  a a"));
        Assert.False(OAuth2PermissionGrantsRules.SameSet("a b", "a B"));
        Assert.False(OAuth2PermissionGrantsRules.SameSet("a", "a b"));
        Assert.Equal("x y", OAuth2PermissionGrantsRules.Join(["x", "y"]));
    }

    [Fact]
    public void UnknownAndDisabledScopes_AreFoundOnTheResourcePrincipal()
    {
        var resource = Resource();

        Assert.Equal(["nope"], OAuth2PermissionGrantsRules.UnknownScopes(["access_as_user", "nope"], resource));
        Assert.Empty(OAuth2PermissionGrantsRules.UnknownScopes(["access_as_user", "legacy"], resource));
        Assert.Equal(["legacy"], OAuth2PermissionGrantsRules.DisabledScopes(["access_as_user", "legacy"], resource));
        Assert.Equal("access_as_user, legacy", OAuth2PermissionGrantsRules.ScopeValues(resource));
        Assert.Contains("in total", OAuth2PermissionGrantsRules.ScopeValues(resource, max: 1), StringComparison.Ordinal);
    }

    // ---- end to end ----

    private static string Template(string resources)
    {
        return resources;
    }

    [Fact]
    public async Task AppRoleAssignment_IsCreatedUnderTheResourcePrincipal_AndRerunIsNoChange()
    {
        var host = new ProviderHost();
        string resourceId = host.Directory.AddServicePrincipal(ResourceAppId, "Orders API", sp => sp["appRoles"] = new JsonArray(new JsonObject { ["id"] = RoleId, ["value"] = "Orders.Read", ["isEnabled"] = true, ["allowedMemberTypes"] = new JsonArray("User", "Application") }))["id"]!.GetValue<string>();
        string principalId = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string template = Templates.Resource(AssignmentType, Guid.NewGuid().ToString("D"), "{ \"principalId\": \"" + principalId + "\", \"resourceId\": \"" + resourceId + "\", \"appRoleId\": \"" + RoleId + "\" }");
        var document = host.Resources(Template(template));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Create, Assert.Single(whatIf.Plan.Changes).Type);
        Assert.False(whatIf.Plan.Changes.Single().HasError, whatIf.Plan.Changes.Single().UnsupportedReason);
        Check.NoWrites(whatIf);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Single(host.Directory.Objects("/servicePrincipals/" + resourceId + "/appRoleAssignedTo"));
        var write = Assert.Single(run.Writes);
        Assert.Equal("POST", write.Method);
        Assert.Contains("appRoleAssign", write.Path, StringComparison.Ordinal);
        Assert.Equal(principalId, Check.Body(write)["principalId"]!.GetValue<string>());
        Assert.Equal(RoleId, Check.Body(write)["appRoleId"]!.GetValue<string>());

        var rerun = await host.CreateAsync(document);
        Check.Succeeded(rerun);
        Assert.Equal(ChangeType.NoChange, Assert.Single(rerun.Executed.Operations).ChangeType);
        Check.NoWrites(rerun);

        var complete = await host.CreateAsync(host.Resources(string.Empty), EArm.Engine.Planning.DeploymentMode.Complete);
        Check.Succeeded(complete);
        Assert.Contains(complete.Writes, w => w.Method == "DELETE");
        Assert.Empty(host.Directory.Objects("/servicePrincipals/" + resourceId + "/appRoleAssignedTo"));
    }

    [Fact]
    public async Task AppRoleAssignment_OfAnUnknownRole_IsUnsupported()
    {
        var host = new ProviderHost();
        string resourceId = host.Directory.AddServicePrincipal(ResourceAppId, "Orders API")["id"]!.GetValue<string>();
        string principalId = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string template = Templates.Resource(AssignmentType, Guid.NewGuid().ToString("D"), "{ \"principalId\": \"" + principalId + "\", \"resourceId\": \"" + resourceId + "\", \"appRoleId\": \"" + RoleId + "\" }");

        var run = await host.CreateAsync(host.Resources(template));

        Assert.False(run.Succeeded);
        Check.NoWrites(run);
        Assert.Contains("no app role", run.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermissionGrant_IsCreated_ScopesAreComparedAsASet_AndUnknownScopesAreRejected()
    {
        var host = new ProviderHost();
        string resourceId = host.Directory.AddServicePrincipal(ResourceAppId, "Orders API", sp => sp["oauth2PermissionScopes"] = new JsonArray(
            new JsonObject { ["id"] = ScopeId, ["value"] = "access_as_user", ["isEnabled"] = true },
            new JsonObject { ["id"] = "8d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0004", ["value"] = "orders.write", ["isEnabled"] = true }))["id"]!.GetValue<string>();
        string clientId = host.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "Client")["id"]!.GetValue<string>();
        string Grant(string scope)
        {
            return Templates.Resource(GrantType, "aaaaaaaa-0000-0000-0000-0000000000a1", "{ \"clientId\": \"" + clientId + "\", \"resourceId\": \"" + resourceId + "\", \"consentType\": \"AllPrincipals\", \"scope\": \"" + scope + "\" }");
        }

        var run = await host.CreateAsync(host.Resources(Grant("access_as_user orders.write")));

        Check.Succeeded(run);
        var write = Assert.Single(run.Writes);
        Assert.Equal("POST", write.Method);
        Assert.Equal("/oauth2PermissionGrants", write.Path);
        Assert.Equal("AllPrincipals", Check.Body(write)["consentType"]!.GetValue<string>());
        Assert.Single(host.Directory.Objects("/oauth2PermissionGrants"));

        var reordered = await host.CreateAsync(host.Resources(Grant("orders.write  access_as_user")));
        Check.Succeeded(reordered);
        Assert.Equal(ChangeType.NoChange, Assert.Single(reordered.Executed.Operations).ChangeType);
        Check.NoWrites(reordered);

        var narrowed = await host.CreateAsync(host.Resources(Grant("access_as_user")));
        Check.Succeeded(narrowed);
        var patch = Assert.Single(narrowed.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.StartsWith("/oauth2PermissionGrants/", patch.Path, StringComparison.Ordinal);

        var unknown = await host.WhatIfAsync(host.Resources(Grant("access_as_user nope")));
        var row = Assert.Single(unknown.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("nope", row.UnsupportedReason, StringComparison.Ordinal);
    }
}
