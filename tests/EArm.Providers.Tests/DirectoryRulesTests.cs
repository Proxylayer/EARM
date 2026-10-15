using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Secrets;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Pure rules of administrative units.</summary>
public sealed class AdministrativeUnitsRulesTests
{
    private static JsonObject Obj(string json)
    {
        return (JsonObject)JsonNode.Parse(json)!;
    }

    [Theory]
    [InlineData("dynamic", "Dynamic")]
    [InlineData("DYNAMIC", "Dynamic")]
    [InlineData(" assigned ", "Assigned")]
    public void NormalizeDesired_PutsMembershipTypeInCanonicalCasing(string declared, string expected)
    {
        var properties = Obj("{ \"membershipType\": \"" + declared + "\", \"membershipRule\": \"(user.department -eq \\\"x\\\")\" }");

        Assert.Empty(AdministrativeUnitsRules.NormalizeDesired(properties));
        Assert.Equal(expected, properties["membershipType"]!.GetValue<string>());
    }

    [Fact]
    public void NormalizeDesired_DynamicUnit_InjectsProcessingOn_ButKeepsADeclaredState()
    {
        var injected = Obj("""{ "membershipType": "Dynamic", "membershipRule": "r" }""");
        Assert.Empty(AdministrativeUnitsRules.NormalizeDesired(injected));
        Assert.Equal("On", injected["membershipRuleProcessingState"]!.GetValue<string>());

        var paused = Obj("""{ "membershipType": "Dynamic", "membershipRule": "r", "membershipRuleProcessingState": "paused" }""");
        Assert.Empty(AdministrativeUnitsRules.NormalizeDesired(paused));
        Assert.Equal("Paused", paused["membershipRuleProcessingState"]!.GetValue<string>());

        var assigned = Obj("""{ "membershipType": "Assigned", "description": "d" }""");
        Assert.Empty(AdministrativeUnitsRules.NormalizeDesired(assigned));
        Assert.False(assigned.ContainsKey("membershipRuleProcessingState"));
    }

    [Fact]
    public void NormalizeDesired_DynamicWithoutARule_IsAProblem()
    {
        var problems = AdministrativeUnitsRules.NormalizeDesired(Obj("""{ "membershipType": "Dynamic" }"""));

        Assert.Contains(problems, p => p.Contains("requires properties.membershipRule", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeDesired_ReportsShapeProblems()
    {
        Assert.Contains(AdministrativeUnitsRules.NormalizeDesired(Obj("""{ "membershipType": "Hybrid" }""")), p => p.Contains("must be 'Assigned' or 'Dynamic'", StringComparison.Ordinal));
        Assert.Contains(AdministrativeUnitsRules.NormalizeDesired(Obj("""{ "membershipType": 5 }""")), p => p.Contains("membershipType must be", StringComparison.Ordinal));
        Assert.Contains(AdministrativeUnitsRules.NormalizeDesired(Obj("""{ "membershipRule": "  " }""")), p => p.Contains("membershipRule must be a non-empty string", StringComparison.Ordinal));
        Assert.Contains(AdministrativeUnitsRules.NormalizeDesired(Obj("""{ "membershipRuleProcessingState": "Off" }""")), p => p.Contains("'On' or 'Paused'", StringComparison.Ordinal));
        Assert.Contains(AdministrativeUnitsRules.NormalizeDesired(Obj("""{ "isMemberManagementRestricted": "yes" }""")), p => p.Contains("isMemberManagementRestricted must be a boolean", StringComparison.Ordinal));
        Assert.Empty(AdministrativeUnitsRules.NormalizeDesired(Obj("""{ "isMemberManagementRestricted": true }""")));
        Assert.Empty(AdministrativeUnitsRules.NormalizeDesired(new JsonObject()));
    }

    [Fact]
    public void NormalizeLive_ANullMembershipTypeIsAssigned()
    {
        Assert.Equal("Assigned", AdministrativeUnitsRules.NormalizeLive(Obj("""{ "membershipType": null }"""))["membershipType"]!.GetValue<string>());
        Assert.Equal("Assigned", AdministrativeUnitsRules.NormalizeLive(Obj("""{ "displayName": "x" }"""))["membershipType"]!.GetValue<string>());
        Assert.Equal("Dynamic", AdministrativeUnitsRules.NormalizeLive(Obj("""{ "membershipType": "dynamic" }"""))["membershipType"]!.GetValue<string>());

        var input = Obj("""{ "membershipType": null }""");
        AdministrativeUnitsRules.NormalizeLive(input);
        Assert.Null(input["membershipType"]);
    }

    [Fact]
    public void IsDynamic_AndIsRestricted_ReadTheProperties()
    {
        Assert.True(AdministrativeUnitsRules.IsDynamic(Obj("""{ "membershipType": "dynamic" }""")));
        Assert.False(AdministrativeUnitsRules.IsDynamic(Obj("""{ "membershipType": "Assigned" }""")));
        Assert.False(AdministrativeUnitsRules.IsDynamic(null));
        Assert.True(AdministrativeUnitsRules.IsRestricted(Obj("""{ "isMemberManagementRestricted": true }""")));
        Assert.False(AdministrativeUnitsRules.IsRestricted(Obj("""{ "isMemberManagementRestricted": false }""")));
        Assert.False(AdministrativeUnitsRules.IsRestricted(new JsonObject()));
    }
}

/// <summary>Pure rules of custom role definitions.</summary>
public sealed class RoleDefinitionsRulesTests
{
    private static JsonObject Obj(string json)
    {
        return (JsonObject)JsonNode.Parse(json)!;
    }

    [Fact]
    public void NormalizeDesired_SortsAndDeduplicatesActionSets_CaseInsensitively()
    {
        var properties = Obj("""
            { "rolePermissions": [ { "allowedResourceActions": [ "microsoft.directory/users/standard/read", " microsoft.directory/Groups/standard/read ", "MICROSOFT.directory/users/standard/read", "microsoft.directory/applications/basic/update" ],
                                     "excludedResourceActions": [ "z/b", "z/a" ] } ] }
            """);

        Assert.Empty(RoleDefinitionsRules.NormalizeDesired(properties));

        Assert.Equal("""["microsoft.directory/applications/basic/update","microsoft.directory/Groups/standard/read","microsoft.directory/users/standard/read"]""", properties["rolePermissions"]![0]!["allowedResourceActions"]!.ToJsonString());
        Assert.Equal("""["z/a","z/b"]""", properties["rolePermissions"]![0]!["excludedResourceActions"]!.ToJsonString());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("templateId")]
    [InlineData("version")]
    [InlineData("isBuiltIn")]
    [InlineData("inheritsPermissionsFrom")]
    public void NormalizeDesired_RejectsServerManagedProperties(string property)
    {
        var properties = Obj("{ \"" + property + "\": \"x\", \"rolePermissions\": [ { \"allowedResourceActions\": [\"a/b/c\"] } ] }");

        var problems = RoleDefinitionsRules.NormalizeDesired(properties);

        Assert.Contains(problems, p => p.Contains("properties." + property + " is assigned by Graph", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeDesired_ResourceScopes_RootIsDropped_AnythingElseIsAProblem()
    {
        var root = Obj("""{ "resourceScopes": ["/"], "rolePermissions": [ { "allowedResourceActions": ["a/b/c"] } ] }""");
        Assert.Empty(RoleDefinitionsRules.NormalizeDesired(root));
        Assert.False(root.ContainsKey("resourceScopes"));

        var other = Obj("""{ "resourceScopes": ["/administrativeUnits/x"], "rolePermissions": [ { "allowedResourceActions": ["a/b/c"] } ] }""");
        Assert.Contains(RoleDefinitionsRules.NormalizeDesired(other), p => p.Contains("resourceScopes may only be", StringComparison.Ordinal));
        Assert.False(other.ContainsKey("resourceScopes"));

        var two = Obj("""{ "resourceScopes": ["/", "/x"] }""");
        Assert.Single(RoleDefinitionsRules.NormalizeDesired(two));
    }

    [Fact]
    public void NormalizeDesired_ReportsEmptyAndMalformedRolePermissions()
    {
        // An empty array counts as absent (JsonObjects.IsAbsent), so NormalizeDesired reports nothing for it; the
        // "at least one entry" branch is unreachable and the empty declaration is caught by the create requirement instead
        // (see RoleDefinitionsProviderTests.EmptyRolePermissions_AreNotCreated).
        Assert.Empty(RoleDefinitionsRules.NormalizeDesired(Obj("""{ "rolePermissions": [] }""")));
        Assert.Contains(RoleDefinitionsRules.NormalizeDesired(Obj("""{ "rolePermissions": "x" }""")), p => p.Contains("must be an array", StringComparison.Ordinal));
        Assert.Contains(RoleDefinitionsRules.NormalizeDesired(Obj("""{ "rolePermissions": [ "x" ] }""")), p => p.Contains("rolePermissions[0] must be an object", StringComparison.Ordinal));
        Assert.Contains(RoleDefinitionsRules.NormalizeDesired(Obj("""{ "rolePermissions": [ { } ] }""")), p => p.Contains("needs allowedResourceActions", StringComparison.Ordinal));
        Assert.Contains(RoleDefinitionsRules.NormalizeDesired(Obj("""{ "rolePermissions": [ { "allowedResourceActions": [] } ] }""")), p => p.Contains("needs allowedResourceActions", StringComparison.Ordinal));
        Assert.Contains(RoleDefinitionsRules.NormalizeDesired(Obj("""{ "rolePermissions": [ { "allowedResourceActions": "a" } ] }""")), p => p.Contains("must be an array of strings", StringComparison.Ordinal));
        Assert.Contains(RoleDefinitionsRules.NormalizeDesired(Obj("""{ "rolePermissions": [ { "allowedResourceActions": ["a", ""] } ] }""")), p => p.Contains("non-empty strings", StringComparison.Ordinal));
        Assert.Contains(RoleDefinitionsRules.NormalizeDesired(Obj("""{ "rolePermissions": [ { "allowedResourceActions": ["a"], "bogus": 1 } ] }""")), p => p.Contains("bogus is not a role permission property", StringComparison.Ordinal));
        Assert.Contains(RoleDefinitionsRules.NormalizeDesired(Obj("""{ "isEnabled": "yes" }""")), p => p.Contains("isEnabled must be a boolean", StringComparison.Ordinal));
        Assert.Empty(RoleDefinitionsRules.NormalizeDesired(new JsonObject()));
    }

    [Fact]
    public void NormalizeLive_UsesTheSameCanonicalSets_AndDropsEmptyExtras()
    {
        var live = Obj("""
            { "id": "x", "rolePermissions": [ { "allowedResourceActions": ["b/b/b", "a/a/a", "B/B/B"], "excludedResourceActions": [], "condition": null } ] }
            """);

        var normalized = RoleDefinitionsRules.NormalizeLive(live);

        Assert.Equal("""[{"allowedResourceActions":["a/a/a","b/b/b"]}]""", normalized["rolePermissions"]!.ToJsonString());
        Assert.Equal(3, live["rolePermissions"]![0]!["allowedResourceActions"]!.AsArray().Count);
    }

    [Fact]
    public void BuiltInHelpers_AndBodyHelpers()
    {
        Assert.True(RoleDefinitionsRules.IsBuiltIn(Obj("""{ "isBuiltIn": true }""")));
        Assert.False(RoleDefinitionsRules.IsBuiltIn(Obj("""{ "isBuiltIn": false }""")));
        Assert.False(RoleDefinitionsRules.IsBuiltIn(null));
        Assert.Contains("roleTemplate('O''Neil')", RoleDefinitionsRules.BuiltInMessage("O'Neil"), StringComparison.Ordinal);

        var body = Obj("""{ "ID": "x", "templateId": "t", "displayName": "d", "Version": "1" }""");
        RoleDefinitionsRules.RemoveServerManaged(body);
        Assert.Equal("""{"displayName":"d"}""", body.ToJsonString());
        RoleDefinitionsRules.ApplyRootScope(body);
        Assert.Equal("""["/"]""", body["resourceScopes"]!.ToJsonString());
    }
}

/// <summary>Pure rules of directory role assignments, shared by the standalone resource and the directoryRoles edge kind.</summary>
public sealed class RoleAssignmentsRulesTests
{
    private const string UnitId = "3b9f9a6e-0d4c-4b1e-8c9c-2f4b7a6d1e10";
    private const string PrincipalId = "11111111-2222-3333-4444-555555555555";

    private static JsonObject Obj(string json)
    {
        return (JsonObject)JsonNode.Parse(json)!;
    }

    [Fact]
    public void Parse_AcceptsCanonicalAndSugarShapes()
    {
        var canonical = RoleAssignmentsRules.Parse(Obj("{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\", \"principalId\": \"" + PrincipalId + "\", \"directoryScopeId\": \"/\" }"), principalAllowed: true, out var error);
        Assert.Null(error);
        Assert.Equal(BuiltInRoles.GlobalReader, canonical!.RoleDefinitionId);
        Assert.Equal(PrincipalId, canonical.PrincipalId);
        Assert.Equal("/", canonical.DirectoryScopeId);
        Assert.False(canonical.HasUnknown);

        var sugar = RoleAssignmentsRules.Parse(Obj("""{ "role": "Global Reader", "administrativeUnit": "Orders AU" }"""), principalAllowed: false, out error);
        Assert.Null(error);
        Assert.Equal("Global Reader", sugar!.Role);
        Assert.Equal("Orders AU", sugar.AdministrativeUnit);
        Assert.Null(sugar.PrincipalId);
    }

    [Theory]
    [InlineData("{ \"bogus\": 1, \"role\": \"x\" }", "is not a property of a directory role assignment")]
    [InlineData("{ \"principalId\": \"11111111-2222-3333-4444-555555555555\" }", "needs the role")]
    [InlineData("{ \"role\": 5 }", "role must be a non-empty string")]
    [InlineData("{ \"role\": \"  \" }", "role must be a non-empty string")]
    [InlineData("{ \"roleDefinitionId\": \"not-a-guid\" }", "roleDefinitionId 'not-a-guid' is not a GUID")]
    [InlineData("{ \"role\": \"x\", \"principalId\": \"nope\" }", "principalId 'nope' is not a GUID")]
    [InlineData("{ \"role\": \"x\", \"directoryScopeId\": \"administrativeUnits/x\" }", "must start with '/'")]
    public void Parse_ReportsShapeErrors(string json, string expected)
    {
        var parsed = RoleAssignmentsRules.Parse(Obj(json), principalAllowed: true, out var error);

        Assert.Null(parsed);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ForbidsAPrincipalOnPrincipalSideItems()
    {
        Assert.Null(RoleAssignmentsRules.Parse(Obj("{ \"role\": \"x\", \"principalId\": \"" + PrincipalId + "\" }"), principalAllowed: false, out var error));
        Assert.Contains("implied by the declaring resource", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRoleOffline_HandlesGuidsAndBuiltInNames_ButNotCustomNames()
    {
        Assert.Equal(BuiltInRoles.GlobalReader, RoleAssignmentsRules.ResolveRoleOffline(BuiltInRoles.GlobalReader.ToUpperInvariant()));
        Assert.Equal(BuiltInRoles.GlobalReader, RoleAssignmentsRules.ResolveRoleOffline("Global Reader"));
        Assert.Equal(BuiltInRoles.UserAdministrator, RoleAssignmentsRules.ResolveRoleOffline("User Administrator"));
        Assert.Null(RoleAssignmentsRules.ResolveRoleOffline("Orders Support"));
        Assert.True(RoleAssignmentsRules.IsGlobalAdministrator(BuiltInRoles.GlobalAdministrator.ToUpperInvariant()));
        Assert.False(RoleAssignmentsRules.IsGlobalAdministrator(BuiltInRoles.GlobalReader));
        Assert.False(RoleAssignmentsRules.IsGlobalAdministrator(null));
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("  ", "/")]
    [InlineData("/", "/")]
    [InlineData(" / ", "/")]
    [InlineData("/administrativeUnits/3B9F9A6E-0D4C-4B1E-8C9C-2F4B7A6D1E10", "/administrativeUnits/3b9f9a6e-0d4c-4b1e-8c9c-2f4b7a6d1e10")]
    [InlineData("/ADMINISTRATIVEUNITS/3b9f9a6e-0d4c-4b1e-8c9c-2f4b7a6d1e10/", "/administrativeUnits/3b9f9a6e-0d4c-4b1e-8c9c-2f4b7a6d1e10")]
    [InlineData("/3B9F9A6E-0D4C-4B1E-8C9C-2F4B7A6D1E10", "/3b9f9a6e-0d4c-4b1e-8c9c-2f4b7a6d1e10")]
    [InlineData("/something/else", "/something/else")]
    public void NormalizeScope_CanonicalisesTheThreeScopeForms(string? scope, string expected)
    {
        Assert.Equal(expected, RoleAssignmentsRules.NormalizeScope(scope));
    }

    [Fact]
    public void AdministrativeUnitScopeHelpers()
    {
        Assert.Equal("/administrativeUnits/" + UnitId, RoleAssignmentsRules.AdministrativeUnitScope(UnitId.ToUpperInvariant()));
        Assert.True(RoleAssignmentsRules.TryGetAdministrativeUnitId("/administrativeUnits/" + UnitId.ToUpperInvariant(), out var id));
        Assert.Equal(UnitId, id);
        Assert.False(RoleAssignmentsRules.TryGetAdministrativeUnitId("/administrativeUnits/not-a-guid", out _));
        Assert.False(RoleAssignmentsRules.TryGetAdministrativeUnitId("/", out _));
        Assert.False(RoleAssignmentsRules.TryGetAdministrativeUnitId(null, out _));
        Assert.True(RoleAssignmentsRules.ScopeEquals("/administrativeUnits/" + UnitId.ToUpperInvariant(), "/administrativeUnits/" + UnitId));
        Assert.True(RoleAssignmentsRules.ScopeEquals(null, "/"));
        Assert.False(RoleAssignmentsRules.ScopeEquals("/", "/administrativeUnits/" + UnitId));
    }

    [Fact]
    public void TargetKey_RoundTrips_AndRejectsPlainIdsAndMalformedKeys()
    {
        string key = RoleAssignmentsRules.TargetKey(BuiltInRoles.GlobalReader.ToUpperInvariant(), "/administrativeUnits/" + UnitId.ToUpperInvariant());
        Assert.Equal(BuiltInRoles.GlobalReader + "@/administrativeUnits/" + UnitId, key);
        Assert.True(RoleAssignmentsRules.TryParseTargetKey(key, out var role, out var scope));
        Assert.Equal(BuiltInRoles.GlobalReader, role);
        Assert.Equal("/administrativeUnits/" + UnitId, scope);
        Assert.Equal(BuiltInRoles.GlobalReader + "@/", RoleAssignmentsRules.TargetKey(BuiltInRoles.GlobalReader, null));

        Assert.False(RoleAssignmentsRules.TryParseTargetKey(BuiltInRoles.GlobalReader, out _, out _));
        Assert.False(RoleAssignmentsRules.TryParseTargetKey("@/", out _, out _));
        Assert.False(RoleAssignmentsRules.TryParseTargetKey("nope@/", out _, out _));
        Assert.False(RoleAssignmentsRules.TryParseTargetKey(BuiltInRoles.GlobalReader + "@scope", out _, out _));
        Assert.False(RoleAssignmentsRules.TryParseTargetKey(BuiltInRoles.GlobalReader + "@", out _, out _));
        Assert.False(RoleAssignmentsRules.TryParseTargetKey(null, out _, out _));
    }

    [Fact]
    public void Identity_IsCaseInsensitive_AndScopeAware()
    {
        string a = RoleAssignmentsRules.Identity(PrincipalId, BuiltInRoles.GlobalReader, "/");
        Assert.Equal(a, RoleAssignmentsRules.Identity(PrincipalId.ToUpperInvariant(), BuiltInRoles.GlobalReader.ToUpperInvariant(), null));
        Assert.NotEqual(a, RoleAssignmentsRules.Identity(PrincipalId, BuiltInRoles.GlobalReader, "/administrativeUnits/" + UnitId));
        Assert.NotEqual(a, RoleAssignmentsRules.Identity(PrincipalId, BuiltInRoles.SecurityReader, "/"));
        Assert.StartsWith("DirectoryRole|", a, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_ComparesRoleAndCanonicalScope()
    {
        var assignment = Obj("{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader.ToUpperInvariant() + "\", \"directoryScopeId\": \"/administrativeUnits/" + UnitId.ToUpperInvariant() + "\" }");

        Assert.True(RoleAssignmentsRules.Matches(assignment, BuiltInRoles.GlobalReader, "/administrativeUnits/" + UnitId));
        Assert.False(RoleAssignmentsRules.Matches(assignment, BuiltInRoles.GlobalReader, "/"));
        Assert.False(RoleAssignmentsRules.Matches(assignment, BuiltInRoles.SecurityReader, "/administrativeUnits/" + UnitId));
        Assert.True(RoleAssignmentsRules.Matches(Obj("{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\" }"), BuiltInRoles.GlobalReader, null));
    }

    [Fact]
    public void BodiesAndExtras()
    {
        Assert.Equal("{\"@odata.type\":\"#microsoft.graph.unifiedRoleAssignment\",\"roleDefinitionId\":\"" + BuiltInRoles.GlobalReader + "\",\"principalId\":\"" + PrincipalId + "\",\"directoryScopeId\":\"/\"}",
            RoleAssignmentsRules.AssignmentBody(BuiltInRoles.GlobalReader.ToUpperInvariant(), PrincipalId.ToUpperInvariant(), null).ToJsonString());

        var extra = RoleAssignmentsRules.LiveExtra(Obj("{ \"id\": \"a1\", \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader.ToUpperInvariant() + "\", \"directoryScopeId\": \"/3B9F9A6E-0D4C-4B1E-8C9C-2F4B7A6D1E10\" }"));
        Assert.Equal("{\"id\":\"a1\",\"roleDefinitionId\":\"" + BuiltInRoles.GlobalReader + "\",\"directoryScopeId\":\"/" + UnitId + "\"}", extra.ToJsonString());

        Assert.Equal("user", RoleAssignmentsRules.PrincipalNoun("Users"));
        Assert.Equal("group", RoleAssignmentsRules.PrincipalNoun("groups"));
        Assert.Equal("service principal", RoleAssignmentsRules.PrincipalNoun("servicePrincipals"));
        Assert.Null(RoleAssignmentsRules.PrincipalNoun("applications"));
        Assert.Null(RoleAssignmentsRules.PrincipalNoun(null));
    }

    [Fact]
    public void CheckPrincipalKind_AcceptsUsersServicePrincipalsAndAssignableGroups()
    {
        Assert.Null(RoleAssignmentsLookup.CheckPrincipalKind("users", null, PrincipalId));
        Assert.Null(RoleAssignmentsLookup.CheckPrincipalKind("servicePrincipals", null, PrincipalId));
        Assert.Null(RoleAssignmentsLookup.CheckPrincipalKind("groups", true, PrincipalId));
        Assert.Contains("not role-assignable", RoleAssignmentsLookup.CheckPrincipalKind("groups", false, PrincipalId), StringComparison.Ordinal);
        Assert.Contains("not role-assignable", RoleAssignmentsLookup.CheckPrincipalKind("groups", null, PrincipalId), StringComparison.Ordinal);
        Assert.Contains("does not exist", RoleAssignmentsLookup.CheckPrincipalKind(null, null, PrincipalId), StringComparison.Ordinal);
        Assert.Contains("nothing else can hold", RoleAssignmentsLookup.CheckPrincipalKind("applications", null, PrincipalId), StringComparison.Ordinal);
        Assert.True(RoleAssignmentsLookup.IsRoleAssignable(Obj("""{ "isAssignableToRole": true }""")));
        Assert.False(RoleAssignmentsLookup.IsRoleAssignable(Obj("""{ "isAssignableToRole": null }""")));
    }
}
