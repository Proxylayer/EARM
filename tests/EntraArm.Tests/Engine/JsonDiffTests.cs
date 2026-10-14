using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Core.Json;
using EntraArm.Core.Secrets;
using EntraArm.Core.Types;
using EntraArm.Engine.Providers;
using EntraArm.Expressions.Values;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary>The shared diff of plan section 1.3 (<see cref="JsonDiff"/>): comparison rules, PATCH bodies, immutability.</summary>
public sealed class JsonDiffTests
{
    private const string GuidLower = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002";
    private const string GuidUpper = "6D2F1C0E-2B7A-4C1E-9A1B-4F3F1F6A0002";

    private static JsonObject J(string json)
    {
        return JsonNode.Parse(json)!.AsObject();
    }

    private static string Text(JsonNode? node)
    {
        return node is null ? "null" : node.ToJsonString();
    }

    private static JsonDiffOptions WithMemberKey(string property, string key)
    {
        return new JsonDiffOptions { MemberKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [property] = key } };
    }

    private static JsonDiffOptions WithNullable(string dotted, bool nullable)
    {
        return new JsonDiffOptions { Nullable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [dotted] = nullable } };
    }

    // ---- scalars ----

    [Fact]
    public void ScalarChange_IsOneModifyDeltaUnderProperties()
    {
        var deltas = JsonDiff.Diff(J("{\"displayName\":\"new\"}"), J("{\"displayName\":\"old\",\"other\":1}"));

        var delta = Assert.Single(deltas);
        Assert.Equal("properties.displayName", delta.Path.ToString());
        Assert.Equal(PropertyChangeType.Modify, delta.Kind);
        Assert.Equal("\"old\"", Text(delta.Before));
        Assert.Equal("\"new\"", Text(delta.After));
        Assert.True(delta.HasEffect);
        Assert.False(delta.IsSecret);
        Assert.False(delta.IsUnknown);
    }

    [Theory]
    [InlineData("{\"appId\":\"" + GuidUpper + "\"}", "{\"appId\":\"" + GuidLower + "\"}")]
    [InlineData("{\"tags\":[]}", "{}")]
    [InlineData("{\"web\":null}", "{}")]
    [InlineData("{\"tags\":[],\"web\":null}", "{\"tags\":[],\"web\":null}")]
    [InlineData("{\"tags\":[\"a\",\"b\"]}", "{\"tags\":[\"b\",\"a\"]}")]
    [InlineData("{\"ids\":[\"" + GuidUpper + "\"]}", "{\"ids\":[\"" + GuidLower + "\"]}")]
    [InlineData("{\"n\":1,\"b\":true}", "{\"n\":1,\"b\":true,\"serverOnly\":\"x\"}")]
    public void EquivalentValues_ProduceNoDelta(string desired, string live)
    {
        Assert.Empty(JsonDiff.Diff(J(desired), J(live)));
    }

    [Theory]
    [InlineData("{\"description\":null}", "{\"description\":\"x\"}", "\"x\"")]
    [InlineData("{\"tags\":[]}", "{\"tags\":[\"t\"]}", "[\"t\"]")]
    [InlineData("{\"web\":null}", "{\"web\":{\"homePageUrl\":\"h\"}}", "{\"homePageUrl\":\"h\"}")]
    public void DesiredNullOrEmpty_AgainstLiveValue_IsDelete(string desired, string live, string before)
    {
        var delta = Assert.Single(JsonDiff.Diff(J(desired), J(live)));

        Assert.Equal(PropertyChangeType.Delete, delta.Kind);
        Assert.Equal(before, Text(delta.Before));
        Assert.True(delta.After is null || delta.After.GetValueKind() == System.Text.Json.JsonValueKind.Null || (delta.After is JsonArray a && a.Count == 0));
    }

    [Fact]
    public void LiveAbsent_IsCreateDelta()
    {
        var delta = Assert.Single(JsonDiff.Diff(J("{\"identifierUris\":[\"api://x\"]}"), J("{}")));

        Assert.Equal(PropertyChangeType.Create, delta.Kind);
        Assert.Null(delta.Before);
        Assert.Equal("[\"api://x\"]", Text(delta.After));
    }

    [Fact]
    public void OnlyDesiredTopLevelProperties_TakePart()
    {
        // Graph PATCH is a merge: live-only top-level properties are never reported.
        Assert.Empty(JsonDiff.Diff(J("{}"), J("{\"displayName\":\"x\",\"web\":{\"a\":1}}")));
    }

    // ---- arrays ----

    [Fact]
    public void ScalarArrays_ReportCreatedAndDeletedMembersUnderAnArrayDelta()
    {
        var deltas = JsonDiff.Diff(J("{\"tags\":[\"a\",\"c\"]}"), J("{\"tags\":[\"a\",\"b\"]}"));

        var array = Assert.Single(deltas);
        Assert.Equal(PropertyChangeType.Array, array.Kind);
        Assert.Equal("properties.tags", array.Path.ToString());
        Assert.NotNull(array.Children);
        Assert.Equal(2, array.Children.Count);

        var created = Assert.Single(array.Children, c => c.Kind == PropertyChangeType.Create);
        Assert.Equal("properties.tags[1]", created.Path.ToString());
        Assert.Equal("\"c\"", Text(created.After));

        var deleted = Assert.Single(array.Children, c => c.Kind == PropertyChangeType.Delete);
        Assert.Equal("properties.tags[1]", deleted.Path.ToString());
        Assert.Equal("\"b\"", Text(deleted.Before));
    }

    [Fact]
    public void ScalarArrays_CountDuplicates()
    {
        var array = Assert.Single(JsonDiff.Diff(J("{\"tags\":[\"a\",\"a\"]}"), J("{\"tags\":[\"a\"]}")));

        var child = Assert.Single(array.Children!);
        Assert.Equal(PropertyChangeType.Create, child.Kind);
    }

    [Fact]
    public void ObjectArrays_WithMemberKey_MatchByKeyAndDiffMembers()
    {
        var desired = J("{\"appRoles\":[{\"id\":\"" + GuidUpper + "\",\"value\":\"Orders.Admin\",\"isEnabled\":true},{\"id\":\"22222222-2222-2222-2222-222222222222\",\"value\":\"New\"}]}");
        var live = J("{\"appRoles\":[{\"id\":\"" + GuidLower + "\",\"value\":\"Orders.Reader\",\"isEnabled\":true},{\"id\":\"33333333-3333-3333-3333-333333333333\",\"value\":\"Gone\"}]}");

        var array = Assert.Single(JsonDiff.Diff(desired, live, WithMemberKey("appRoles", "id")));

        Assert.Equal(PropertyChangeType.Array, array.Kind);
        Assert.Equal(3, array.Children!.Count);

        var modified = Assert.Single(array.Children, c => c.Kind == PropertyChangeType.Modify);
        Assert.Equal(GuidUpper, modified.Path.Last.Name);
        var member = Assert.Single(modified.Children!);
        Assert.Equal("value", member.Path.Last.Name);
        Assert.Equal("\"Orders.Reader\"", Text(member.Before));
        Assert.Equal("\"Orders.Admin\"", Text(member.After));

        var created = Assert.Single(array.Children, c => c.Kind == PropertyChangeType.Create);
        Assert.Equal("22222222-2222-2222-2222-222222222222", created.Path.Last.Name);

        var deleted = Assert.Single(array.Children, c => c.Kind == PropertyChangeType.Delete);
        Assert.Equal("33333333-3333-3333-3333-333333333333", deleted.Path.Last.Name);
    }

    [Fact]
    public void ObjectArrays_WithMemberKey_AndNoChange_ProduceNoDelta()
    {
        var desired = J("{\"appRoles\":[{\"id\":\"" + GuidUpper + "\",\"value\":\"a\"},{\"id\":\"22222222-2222-2222-2222-222222222222\",\"value\":\"b\"}]}");
        var live = J("{\"appRoles\":[{\"id\":\"22222222-2222-2222-2222-222222222222\",\"value\":\"b\"},{\"id\":\"" + GuidLower + "\",\"value\":\"a\"}]}");

        Assert.Empty(JsonDiff.Diff(desired, live, WithMemberKey("appRoles", "id")));
    }

    [Fact]
    public void ObjectArrays_WithoutMemberKey_CompareCanonicallyAndOrderInsensitively()
    {
        var same = JsonDiff.Diff(
            J("{\"resourceAccess\":[{\"id\":\"a\",\"type\":\"Scope\"},{\"type\":\"Role\",\"id\":\"b\"}]}"),
            J("{\"resourceAccess\":[{\"id\":\"b\",\"type\":\"Role\"},{\"id\":\"a\",\"type\":\"Scope\"}]}"));
        Assert.Empty(same);

        var changed = Assert.Single(JsonDiff.Diff(
            J("{\"resourceAccess\":[{\"id\":\"a\",\"type\":\"Role\"}]}"),
            J("{\"resourceAccess\":[{\"id\":\"a\",\"type\":\"Scope\"}]}")));
        Assert.Equal(PropertyChangeType.Array, changed.Kind);
        Assert.Equal(2, changed.Children!.Count);
        Assert.Contains(changed.Children, c => c.Kind == PropertyChangeType.Create && c.Path.ToString() == "properties.resourceAccess[0]");
        Assert.Contains(changed.Children, c => c.Kind == PropertyChangeType.Delete && c.Path.ToString() == "properties.resourceAccess[0]");
    }

    [Fact]
    public void ObjectArrays_MemberKeyFallsBackToCanonical_WhenAMemberIsNotAnObject()
    {
        var array = Assert.Single(JsonDiff.Diff(J("{\"appRoles\":[\"x\"]}"), J("{\"appRoles\":[\"y\"]}"), WithMemberKey("appRoles", "id")));

        Assert.Equal(2, array.Children!.Count);
        Assert.All(array.Children, c => Assert.True(c.Path.Last.IsIndex));
    }

    // ---- nested objects ----

    [Fact]
    public void NestedObject_LiveOnlySubProperty_IsDeleteChildOfAModify()
    {
        var deltas = JsonDiff.Diff(J("{\"web\":{\"homePageUrl\":\"h\"}}"), J("{\"web\":{\"homePageUrl\":\"h\",\"logoutUrl\":\"l\"}}"));

        var web = Assert.Single(deltas);
        Assert.Equal(PropertyChangeType.Modify, web.Kind);
        Assert.Equal("properties.web", web.Path.ToString());
        Assert.True(web.HasEffect);
        var child = Assert.Single(web.Children!);
        Assert.Equal(PropertyChangeType.Delete, child.Kind);
        Assert.Equal("properties.web.logoutUrl", child.Path.ToString());
        Assert.Equal("\"l\"", Text(child.Before));
    }

    [Fact]
    public void NestedObject_NonNullableLiveOnlySubProperty_IsNoEffect()
    {
        var options = WithNullable("web.implicitGrantSettings", false);
        var deltas = JsonDiff.Diff(J("{\"web\":{\"homePageUrl\":\"h\"}}"), J("{\"web\":{\"homePageUrl\":\"h\",\"implicitGrantSettings\":{\"enableIdTokenIssuance\":false}}}"), options);

        var web = Assert.Single(deltas);
        Assert.Equal(PropertyChangeType.Modify, web.Kind);
        var child = Assert.Single(web.Children!);
        Assert.Equal(PropertyChangeType.NoEffect, child.Kind);
        Assert.False(child.HasEffect);
        Assert.False(web.HasEffect, "a Modify whose only child is NoEffect requires no write");
    }

    [Fact]
    public void NestedObject_ChangedAndLiveOnlySubProperties_AreBothReported()
    {
        var deltas = JsonDiff.Diff(J("{\"web\":{\"homePageUrl\":\"new\",\"redirectUris\":[\"a\"]}}"), J("{\"web\":{\"homePageUrl\":\"old\",\"logoutUrl\":\"l\",\"redirectUris\":[\"a\"]}}"));

        var web = Assert.Single(deltas);
        Assert.Equal(2, web.Children!.Count);
        Assert.Contains(web.Children, c => c.Kind == PropertyChangeType.Modify && c.Path.ToString() == "properties.web.homePageUrl");
        Assert.Contains(web.Children, c => c.Kind == PropertyChangeType.Delete && c.Path.ToString() == "properties.web.logoutUrl");
    }

    [Fact]
    public void NestedObject_Equal_ProducesNoDelta()
    {
        Assert.Empty(JsonDiff.Diff(J("{\"web\":{\"homePageUrl\":\"h\",\"redirectUris\":[\"b\",\"a\"]}}"), J("{\"web\":{\"redirectUris\":[\"a\",\"b\"],\"homePageUrl\":\"h\"}}")));
    }

    // ---- secrets and unknowns ----

    [Fact]
    public void SecretLeaf_IsNeverCompared_AndRendersAsMask()
    {
        var desired = new JsonObject { ["passwordProfile"] = new JsonObject { ["password"] = SecretJson.CreateString("hunter2"), ["forceChangePasswordNextSignIn"] = true } };

        var created = Assert.Single(JsonDiff.Diff(desired, J("{}")));
        Assert.Equal(PropertyChangeType.Create, created.Kind);
        Assert.True(created.IsSecret);
        Assert.False(created.IsUnknown);
        Assert.Equal("\"***\"", Text(created.After!["password"]));
        Assert.DoesNotContain("hunter2", Text(created.After), StringComparison.Ordinal);

        var liveSecret = new JsonObject { ["passwordProfile"] = new JsonObject { ["password"] = SecretJson.CreateString("hunter2"), ["forceChangePasswordNextSignIn"] = true } };
        var modified = Assert.Single(JsonDiff.Diff(desired, liveSecret));
        Assert.Equal(PropertyChangeType.Modify, modified.Kind);
        Assert.True(modified.IsSecret);
        Assert.Equal("\"***\"", Text(modified.Before!["password"]));
        Assert.Equal("\"***\"", Text(modified.After!["password"]));
    }

    [Fact]
    public void UnknownLeaf_IsFlaggedAndRendersAsDeployTime()
    {
        var desired = new JsonObject { ["principalId"] = UnknownJson.Create("ref(/providers/Microsoft.Entra/groups/g).id", UnknownOrigins.Reference) };

        var created = Assert.Single(JsonDiff.Diff(desired, J("{}")));
        Assert.Equal(PropertyChangeType.Create, created.Kind);
        Assert.True(created.IsUnknown);
        Assert.False(created.IsSecret);
        Assert.Equal("\"(deploy-time)\"", Text(created.After));

        var modified = Assert.Single(JsonDiff.Diff(desired, J("{\"principalId\":\"live\"}")));
        Assert.Equal(PropertyChangeType.Modify, modified.Kind);
        Assert.True(modified.IsUnknown);
        Assert.Equal("\"live\"", Text(modified.Before));
    }

    [Fact]
    public void DescribeCreate_ListsEveryNonAbsentTopLevelProperty()
    {
        var desired = new JsonObject
        {
            ["displayName"] = "x",
            ["tags"] = new JsonArray(),
            ["nothing"] = null,
            ["secret"] = SecretJson.CreateString("s"),
            ["later"] = UnknownJson.Create("ref(a).id", UnknownOrigins.Reference),
        };

        var deltas = JsonDiff.DescribeCreate(desired);

        Assert.Equal(["properties.displayName", "properties.secret", "properties.later"], deltas.Select(d => d.Path.ToString()).ToList());
        Assert.All(deltas, d => Assert.Equal(PropertyChangeType.Create, d.Kind));
        Assert.True(deltas[1].IsSecret);
        Assert.True(deltas[2].IsUnknown);
    }

    // ---- patch body ----

    [Fact]
    public void BuildPatchBody_SendsChangedPropertiesWhole_WithExplicitNullsAndClearedValues()
    {
        var desired = J("{\"displayName\":\"b\",\"web\":{\"homePageUrl\":\"h\"},\"tags\":null,\"identifierUris\":[],\"description\":null,\"unchanged\":1}");
        var live = J("{\"displayName\":\"a\",\"web\":{\"homePageUrl\":\"g\",\"logoutUrl\":\"l\",\"implicitGrantSettings\":{\"x\":1}},\"tags\":[\"t\"],\"identifierUris\":[\"u\"],\"description\":\"d\",\"unchanged\":1}");
        var options = WithNullable("web.implicitGrantSettings", false);
        var deltas = JsonDiff.Diff(desired, live, options);

        var body = JsonDiff.BuildPatchBody(desired, live, deltas, options);

        Assert.Equal("\"b\"", Text(body["displayName"]));
        Assert.Equal("{\"homePageUrl\":\"h\",\"logoutUrl\":null}", Text(body["web"]));
        Assert.True(body.ContainsKey("tags"));
        Assert.Equal("[]", Text(body["tags"]));
        Assert.Equal("[]", Text(body["identifierUris"]));
        Assert.True(body.ContainsKey("description"));
        Assert.Null(body["description"]);
        Assert.False(body.ContainsKey("unchanged"));
    }

    [Fact]
    public void BuildPatchBody_SkipsNoEffectDeltas_AndKeepsSecretLeaves()
    {
        var options = WithNullable("web.implicitGrantSettings", false);
        var desired = new JsonObject { ["web"] = J("{\"homePageUrl\":\"h\"}"), ["passwordProfile"] = new JsonObject { ["password"] = SecretJson.CreateString("s") } };
        var live = J("{\"web\":{\"homePageUrl\":\"h\",\"implicitGrantSettings\":{\"x\":1}}}");
        var deltas = JsonDiff.Diff(desired, live, options);

        var body = JsonDiff.BuildPatchBody(desired, live, deltas, options);

        Assert.False(body.ContainsKey("web"), "a NoEffect-only Modify is not written");
        Assert.True(SecretJson.IsSecret(body["passwordProfile"]!["password"]), "secret leaves are preserved for the provider to reveal");
    }

    [Fact]
    public void BuildPatchBody_ReplacesArraysWhole()
    {
        var desired = J("{\"tags\":[\"a\",\"c\"]}");
        var live = J("{\"tags\":[\"a\",\"b\"]}");

        var body = JsonDiff.BuildPatchBody(desired, live, JsonDiff.Diff(desired, live));

        Assert.Equal("[\"a\",\"c\"]", Text(body["tags"]));
    }

    // ---- immutability ----

    public static TheoryData<string, string[], bool> ImmutableCases()
    {
        return new TheoryData<string, string[], bool>
        {
            { "properties.uniqueName", new[] { "uniqueName" }, true },
            { "properties.web.redirectUris", new[] { "web" }, true },
            { "properties.web", new[] { "web.redirectUris" }, true },
            { "properties.displayName", new[] { "uniqueName", "appId" }, false },
            { "properties.displayName", Array.Empty<string>(), false },
            { "properties['@odata.type']", new[] { "['@odata.type']" }, true },
        };
    }

    [Theory]
    [MemberData(nameof(ImmutableCases))]
    public void IsImmutable_MatchesAtBelowAndAboveTheImmutablePath(string deltaPath, string[] immutable, bool expected)
    {
        var delta = new PropertyDelta(JsonPath.Parse(deltaPath), PropertyChangeType.Modify, null, null);
        var set = immutable.Select(JsonPath.Parse).ToHashSet();

        Assert.Equal(expected, JsonDiff.IsImmutable(delta, set, JsonPath.Of("properties")));
    }

    [Fact]
    public void IsImmutable_IgnoresDeltasOutsideTheBasePath()
    {
        var delta = new PropertyDelta(JsonPath.Parse("other.uniqueName"), PropertyChangeType.Modify, null, null);

        Assert.False(JsonDiff.IsImmutable(delta, new HashSet<JsonPath> { JsonPath.Parse("uniqueName") }, JsonPath.Of("properties")));
    }

    // ---- overlay-driven options ----

    [Fact]
    public void CreateOnlyPaths_AreExcludedOnUpdateOnly()
    {
        var users = ResourceTypeCatalog.Default.Find(Fixture.UserType)!;
        var desired = new JsonObject { ["displayName"] = "u", ["passwordProfile"] = new JsonObject { ["password"] = SecretJson.CreateString("s") } };
        var live = J("{\"displayName\":\"u\"}");

        Assert.Empty(JsonDiff.Diff(desired, live, JsonDiffOptions.ForDefinition(users, existing: true)));

        var onCreate = JsonDiff.Diff(desired, live, JsonDiffOptions.ForDefinition(users, existing: false));
        var delta = Assert.Single(onCreate);
        Assert.Equal("properties.passwordProfile", delta.Path.ToString());
        Assert.True(delta.IsSecret);
    }

    [Fact]
    public void ForDefinition_CarriesTheOverlayMemberKeysAndNullability()
    {
        var applications = ResourceTypeCatalog.Default.Find(Fixture.AppType)!;

        var options = JsonDiffOptions.ForDefinition(applications, existing: true);

        Assert.Equal("id", options.MemberKey["appRoles"]);
        Assert.Equal("id", options.MemberKey["api.oauth2PermissionScopes"]);
        Assert.False(options.Nullable["web.implicitGrantSettings"]);
        Assert.Empty(options.Exclude);
    }

    [Theory]
    [InlineData("\"" + GuidUpper + "\"", "\"" + GuidLower + "\"", true)]
    [InlineData("\"abc\"", "\"ABC\"", false)]
    [InlineData("true", "\"true\"", false)]
    public void ScalarEquals_IsGuidInsensitiveAndOtherwiseExact(string left, string right, bool expected)
    {
        Assert.Equal(expected, JsonDiff.ScalarEquals(JsonNode.Parse(left), JsonNode.Parse(right)));
    }

    [Fact]
    public void Canonical_LowerCasesGuidsAndDropsAbsentMembers()
    {
        Assert.Equal(JsonDiff.Canonical(J("{\"id\":\"" + GuidUpper + "\",\"x\":null,\"y\":[]}")), JsonDiff.Canonical(J("{\"id\":\"" + GuidLower + "\"}")));
    }
}
