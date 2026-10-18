using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Pure rules of the applications provider: derived ids, normalisation, value collisions, deferred identifier URIs, the two-step disable.</summary>
public sealed class ApplicationsRulesTests
{
    private const string Name = "test-api";

    [Fact]
    public void DeriveMemberId_IsStable_AndDistinctPerNameAndValue()
    {
        var first = ApplicationsRules.DeriveMemberId(Name, "Orders.Read");
        var again = ApplicationsRules.DeriveMemberId(Name, "Orders.Read");
        var otherValue = ApplicationsRules.DeriveMemberId(Name, "Orders.Admin");
        var otherName = ApplicationsRules.DeriveMemberId("other-api", "Orders.Read");

        Assert.Equal(first, again);
        Assert.NotEqual(first, otherValue);
        Assert.NotEqual(first, otherName);
        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal('5', first.ToString("D")[14]);
    }

    [Fact]
    public void NormalizeDesired_FillsDerivedIds_IsEnabled_TokenVersion_AndImplicitGrant()
    {
        var properties = (JsonObject)JsonNode.Parse("""
            { "appRoles": [ { "value": "Orders.Read", "displayName": "r" }, { "id": "aaaaaaaa-0000-0000-0000-000000000001", "value": "Orders.Admin", "isEnabled": false } ],
              "api": { "requestedAccessTokenVersion": null, "oauth2PermissionScopes": [ { "value": "access_as_user" } ] },
              "web": { "implicitGrantSettings": { } } }
            """)!;

        ApplicationsRules.NormalizeDesired(properties, Name);

        string expectedRole = ApplicationsRules.DeriveMemberId(Name, "Orders.Read").ToString("D");
        string expectedScope = ApplicationsRules.DeriveMemberId(Name, "access_as_user").ToString("D");
        Assert.Equal(expectedRole, properties["appRoles"]![0]!["id"]!.GetValue<string>());
        Assert.True(properties["appRoles"]![0]!["isEnabled"]!.GetValue<bool>());
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", properties["appRoles"]![1]!["id"]!.GetValue<string>());
        Assert.False(properties["appRoles"]![1]!["isEnabled"]!.GetValue<bool>());
        Assert.Equal(expectedScope, properties["api"]!["oauth2PermissionScopes"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(1, properties["api"]!["requestedAccessTokenVersion"]!.GetValue<int>());
        Assert.False(properties["web"]!["implicitGrantSettings"]!["enableAccessTokenIssuance"]!.GetValue<bool>());
        Assert.False(properties["web"]!["implicitGrantSettings"]!["enableIdTokenIssuance"]!.GetValue<bool>());

        // Idempotent: a second pass changes nothing.
        string once = properties.ToJsonString();
        ApplicationsRules.NormalizeDesired(properties, Name);
        Assert.Equal(once, properties.ToJsonString());
    }

    [Fact]
    public void NormalizeDesired_WithoutUniqueName_DerivesNothing()
    {
        var properties = (JsonObject)JsonNode.Parse("""{ "appRoles": [ { "value": "x" } ] }""")!;

        ApplicationsRules.NormalizeDesired(properties, null);

        Assert.Null(properties["appRoles"]![0]!["id"]);
        Assert.True(properties["appRoles"]![0]!["isEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void DuplicateValues_SpansRolesAndScopes_InFirstSeenOrder()
    {
        var properties = (JsonObject)JsonNode.Parse("""
            { "appRoles": [ { "value": "dup" }, { "value": "x" }, { "value": "" } ],
              "api": { "oauth2PermissionScopes": [ { "value": "dup" }, { "value": "y" }, { "value": "y" }, { "value": "" } ] } }
            """)!;

        Assert.Equal(["dup", "y"], ApplicationsRules.DuplicateValues(properties));
        Assert.Empty(ApplicationsRules.DuplicateValues((JsonObject)JsonNode.Parse("""{ "appRoles": [ { "value": "a" } ] }""")!));
        Assert.Empty(ApplicationsRules.DuplicateValues(new JsonObject()));
    }

    [Fact]
    public void IdentifierUris_PlaceholderIsDetected_Substituted_AndDeferred()
    {
        var properties = (JsonObject)JsonNode.Parse("""{ "identifierUris": ["api://{appId}", "api://fixed"] }""")!;
        Assert.True(ApplicationsRules.HasDeferredIdentifierUris(properties));

        var body = (JsonObject)properties.DeepClone();
        Assert.True(ApplicationsRules.RemoveDeferredIdentifierUris(body));
        Assert.Equal("""{"identifierUris":["api://fixed"]}""", body.ToJsonString());
        Assert.False(ApplicationsRules.RemoveDeferredIdentifierUris(body));

        var onlyDeferred = (JsonObject)JsonNode.Parse("""{ "displayName": "x", "identifierUris": ["api://{appId}"] }""")!;
        Assert.True(ApplicationsRules.RemoveDeferredIdentifierUris(onlyDeferred));
        Assert.Equal("""{"displayName":"x"}""", onlyDeferred.ToJsonString());

        Assert.True(ApplicationsRules.SubstituteAppId(properties, "11111111-2222-3333-4444-555555555555"));
        Assert.Equal("""{"identifierUris":["api://11111111-2222-3333-4444-555555555555","api://fixed"]}""", properties.ToJsonString());
        Assert.False(ApplicationsRules.HasDeferredIdentifierUris(properties));
        Assert.False(ApplicationsRules.SubstituteAppId(properties, "11111111-2222-3333-4444-555555555555"));
        Assert.False(ApplicationsRules.SubstituteAppId(new JsonObject(), "11111111-2222-3333-4444-555555555555"));
    }

    [Fact]
    public void NormalizeLive_StripsOrigin_FillsDefaults_AndCopiesDeclaredPassthroughs()
    {
        var stripped = (JsonObject)JsonNode.Parse("""
            { "displayName": "x",
              "appRoles": [ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "value": "r", "origin": "Application" } ],
              "api": { "requestedAccessTokenVersion": null, "oauth2PermissionScopes": [ { "id": "aaaaaaaa-0000-0000-0000-000000000002", "value": "s", "origin": "Application" } ] },
              "web": { "redirectUris": ["https://x"] } }
            """)!;
        var raw = (JsonObject)stripped.DeepClone();
        raw["appId"] = "11111111-2222-3333-4444-555555555555";
        raw["keyCredentials"] = new JsonArray(new JsonObject { ["keyId"] = "k" });
        var existing = new ExistingResource("obj", stripped, raw);

        var declaringBoth = (JsonObject)JsonNode.Parse("""{ "appId": "x", "keyCredentials": [] }""")!;
        var live = ApplicationsRules.NormalizeLive(existing, declaringBoth);

        Assert.Null(live["appRoles"]![0]!["origin"]);
        Assert.Null(live["api"]!["oauth2PermissionScopes"]![0]!["origin"]);
        Assert.Equal(1, live["api"]!["requestedAccessTokenVersion"]!.GetValue<int>());
        Assert.Equal("""{"enableAccessTokenIssuance":false,"enableIdTokenIssuance":false}""", live["web"]!["implicitGrantSettings"]!.ToJsonString());
        Assert.Equal("11111111-2222-3333-4444-555555555555", live["appId"]!.GetValue<string>());
        Assert.Equal("""[{"keyId":"k"}]""", live["keyCredentials"]!.ToJsonString());

        // Not declared: not copied back.
        var plain = ApplicationsRules.NormalizeLive(existing, new JsonObject());
        Assert.Null(plain["appId"]);
        Assert.Null(plain["keyCredentials"]);

        // The existing object is untouched.
        Assert.Equal("Application", stripped["appRoles"]![0]!["origin"]!.GetValue<string>());
    }

    [Fact]
    public void DisableStep_DisablesRemovedAndBreakingMembers_Only()
    {
        var live = (JsonArray)JsonNode.Parse("""
            [ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "value": "R1", "allowedMemberTypes": ["User"], "isEnabled": true },
              { "id": "aaaaaaaa-0000-0000-0000-000000000002", "value": "R2", "allowedMemberTypes": ["User"], "isEnabled": true },
              { "id": "aaaaaaaa-0000-0000-0000-000000000003", "value": "R3", "allowedMemberTypes": ["User"], "isEnabled": false } ]
            """)!;

        // R2 removed, R3 already disabled: only R2 flips.
        var removal = (JsonArray)JsonNode.Parse("""[ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "value": "R1", "allowedMemberTypes": ["User"], "isEnabled": true } ]""")!;
        var step = ApplicationsRules.DisableStep(removal, live, ApplicationsRules.AppRoleBreakingProperties);
        Assert.NotNull(step);
        Assert.Equal(3, step.Count);
        Assert.True(step[0]!["isEnabled"]!.GetValue<bool>());
        Assert.False(step[1]!["isEnabled"]!.GetValue<bool>());
        Assert.False(step[2]!["isEnabled"]!.GetValue<bool>());
        Assert.True(live[1]!["isEnabled"]!.GetValue<bool>());

        // A value change on R1 disables R1 first.
        var valueChange = (JsonArray)JsonNode.Parse("""[ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "value": "R1.v2", "allowedMemberTypes": ["User"], "isEnabled": true }, { "id": "aaaaaaaa-0000-0000-0000-000000000002", "value": "R2", "allowedMemberTypes": ["User"], "isEnabled": true } ]""")!;
        step = ApplicationsRules.DisableStep(valueChange, live, ApplicationsRules.AppRoleBreakingProperties);
        Assert.NotNull(step);
        Assert.False(step[0]!["isEnabled"]!.GetValue<bool>());
        Assert.True(step[1]!["isEnabled"]!.GetValue<bool>());

        // A non-breaking change (description) needs no first step.
        var description = (JsonArray)JsonNode.Parse("""[ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "value": "R1", "allowedMemberTypes": ["User"], "description": "new" }, { "id": "aaaaaaaa-0000-0000-0000-000000000002", "value": "R2", "allowedMemberTypes": ["User"] } ]""")!;
        Assert.Null(ApplicationsRules.DisableStep(description, live, ApplicationsRules.AppRoleBreakingProperties));

        Assert.Null(ApplicationsRules.DisableStep(null, live, ApplicationsRules.AppRoleBreakingProperties));
        Assert.Null(ApplicationsRules.DisableStep(removal, null, ApplicationsRules.AppRoleBreakingProperties));
        Assert.Null(ApplicationsRules.DisableStep(removal, new JsonArray(), ApplicationsRules.AppRoleBreakingProperties));
    }

    [Fact]
    public void DisableStep_ForScopes_UsesTypeAsBreakingProperty()
    {
        var live = (JsonArray)JsonNode.Parse("""[ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "value": "s", "type": "User", "isEnabled": true } ]""")!;
        var typeChange = (JsonArray)JsonNode.Parse("""[ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "value": "s", "type": "Admin" } ]""")!;

        var step = ApplicationsRules.DisableStep(typeChange, live, ApplicationsRules.ScopeBreakingProperties);

        Assert.NotNull(step);
        Assert.False(step.Single()!["isEnabled"]!.GetValue<bool>());
        Assert.Equal(["value", "type"], ApplicationsRules.ScopeBreakingProperties);
        Assert.Equal(["value", "allowedMemberTypes"], ApplicationsRules.AppRoleBreakingProperties);
    }

    [Fact]
    public void AppRolesOf_AndScopesOf_ReturnArraysOnlyWhenDeclaredAsArrays()
    {
        var properties = (JsonObject)JsonNode.Parse("""{ "appRoles": [ { } ], "api": { "oauth2PermissionScopes": [ { }, { } ] } }""")!;
        Assert.Single(ApplicationsRules.AppRolesOf(properties)!);
        Assert.Equal(2, ApplicationsRules.ScopesOf(properties)!.Count);
        Assert.Null(ApplicationsRules.AppRolesOf(new JsonObject { ["appRoles"] = "x" }));
        Assert.Null(ApplicationsRules.ScopesOf(null));
    }
}
