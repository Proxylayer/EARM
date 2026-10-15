using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core.Json;
using EArm.Engine.Edges;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Pure rules behind the groups provider and the edge pass (WI-09): <see cref="GroupsShape"/>, <see cref="GroupsLicensing.Diff"/> and <see cref="EdgePlan.RecordingsFor"/>.</summary>
public sealed class GroupsRulesTests
{
    private const string SkuId = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData("Harness_Grp One!!", "harness_grp-one-")]
    [InlineData("Contoso Dev-Admins", "contoso-dev-admins")]
    [InlineData("a   b", "a-b")]
    [InlineData("ÄÖ", "-")]
    [InlineData("plain_name-1", "plain_name-1")]
    public void DeriveMailNickname_LowerCases_ReplacesAndCollapses(string name, string expected)
    {
        Assert.Equal(expected, GroupsShape.DeriveMailNickname(name));
    }

    [Fact]
    public void DeriveMailNickname_IsCappedAt64Characters()
    {
        Assert.Equal(64, GroupsShape.DeriveMailNickname(new string('a', 70)).Length);
        Assert.Equal(64, GroupsShape.MailNicknameMaxLength);
    }

    [Fact]
    public void Analyze_ClassifiesTheKind_WithDefaults()
    {
        var security = GroupsShape.Analyze(new JsonObject());
        Assert.False(security.IsUnified);
        Assert.False(security.IsDynamic);
        Assert.Null(security.MailEnabled);
        Assert.False(security.EffectiveMailEnabled);
        Assert.True(security.EffectiveSecurityEnabled);
        Assert.Equal("security", security.Kind);
        Assert.True(security.IsPermanentlyDeleted);

        var unified = GroupsShape.Analyze((JsonObject)JsonNode.Parse("""{ "groupTypes": ["unified"] }""")!);
        Assert.True(unified.IsUnified);
        Assert.True(unified.EffectiveMailEnabled);
        Assert.False(unified.EffectiveSecurityEnabled);
        Assert.Equal("microsoft365", unified.Kind);
        Assert.False(unified.IsPermanentlyDeleted);

        var dynamic = GroupsShape.Analyze((JsonObject)JsonNode.Parse("""{ "groupTypes": ["DynamicMembership"], "membershipRule": "  ", "visibility": "Private" }""")!);
        Assert.True(dynamic.IsDynamic);
        Assert.False(dynamic.HasMembershipRule);
        Assert.Equal("Private", dynamic.Visibility);

        var mes = GroupsShape.Analyze((JsonObject)JsonNode.Parse("""{ "mailEnabled": true, "securityEnabled": true }""")!);
        Assert.True(mes.IsMailEnabledSecurity);
        Assert.Equal("mailEnabledSecurity", mes.Kind);

        var distribution = GroupsShape.Analyze((JsonObject)JsonNode.Parse("""{ "mailEnabled": true, "securityEnabled": false }""")!);
        Assert.True(distribution.IsDistributionList);
        Assert.Equal("distribution", distribution.Kind);
    }

    [Fact]
    public void CreateError_NamesTheAdoptOnlyKinds_AndTheMissingRule()
    {
        Assert.Contains("mail-enabled security", GroupsShape.CreateError(GroupsShape.Analyze((JsonObject)JsonNode.Parse("""{ "mailEnabled": true, "securityEnabled": true }""")!), "g"), StringComparison.Ordinal);
        Assert.Contains("distribution lists", GroupsShape.CreateError(GroupsShape.Analyze((JsonObject)JsonNode.Parse("""{ "mailEnabled": true, "securityEnabled": false }""")!), "g"), StringComparison.Ordinal);
        Assert.Contains("membershipRule", GroupsShape.CreateError(GroupsShape.Analyze((JsonObject)JsonNode.Parse("""{ "groupTypes": ["DynamicMembership"] }""")!), "g"), StringComparison.Ordinal);
        Assert.Null(GroupsShape.CreateError(GroupsShape.Analyze((JsonObject)JsonNode.Parse("""{ "groupTypes": ["DynamicMembership"], "membershipRule": "r" }""")!), "g"));
        Assert.Null(GroupsShape.CreateError(GroupsShape.Analyze((JsonObject)JsonNode.Parse("""{ "groupTypes": ["Unified"] }""")!), "g"));
        Assert.Null(GroupsShape.CreateError(GroupsShape.Analyze(new JsonObject()), "g"));
    }

    [Fact]
    public void ApplyCreateDefaults_FillsOnlyWhatTheTemplateLeftOut()
    {
        var body = new JsonObject();
        GroupsShape.ApplyCreateDefaults(body, "Grp One", GroupsShape.Analyze(body));
        Assert.Equal("""{"groupTypes":[],"mailEnabled":false,"securityEnabled":true,"mailNickname":"grp-one"}""", body.ToJsonString());

        var declared = (JsonObject)JsonNode.Parse("""{ "mailNickname": "custom", "groupTypes": ["DynamicMembership"], "membershipRule": "r" }""")!;
        GroupsShape.ApplyCreateDefaults(declared, "Grp", GroupsShape.Analyze(declared));
        Assert.Equal("custom", declared["mailNickname"]!.GetValue<string>());
        Assert.Equal("On", declared["membershipRuleProcessingState"]!.GetValue<string>());
        Assert.False(declared["mailEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void PresenceChecks_DetectUnifiedDynamicAndHiddenMembership()
    {
        var unified = new JsonArray("Unified");
        var dynamic = new JsonArray("DynamicMembership");
        Assert.True(GroupsShape.UnifiedPresenceDiffers(unified, new JsonArray()));
        Assert.False(GroupsShape.UnifiedPresenceDiffers(unified, new JsonArray("unified", "DynamicMembership")));
        Assert.True(GroupsShape.DynamicPresenceDiffers(null, dynamic));
        Assert.False(GroupsShape.DynamicPresenceDiffers(dynamic, dynamic));
        Assert.True(GroupsShape.HiddenMembershipInvolved(JsonValue.Create("HiddenMembership"), JsonValue.Create("Private")));
        Assert.True(GroupsShape.HiddenMembershipInvolved(null, JsonValue.Create("hiddenmembership")));
        Assert.False(GroupsShape.HiddenMembershipInvolved(JsonValue.Create("Public"), JsonValue.Create("Private")));
        Assert.True(GroupsShape.GroupTypesContain(unified, "UNIFIED"));
        Assert.False(GroupsShape.GroupTypesContain(JsonValue.Create("Unified"), "Unified"));
    }

    // ---- licensing ----

    private static GroupsLicensing.SkuTable Skus()
    {
        var skus = new GroupsLicensing.SkuTable();
        skus.Add(SkuId, "ENTERPRISEPREMIUM");
        return skus;
    }

    [Fact]
    public void LicenseDiff_AddsResolvedPartNumbers_RemovesUndeclared_AndReaddsChangedPlans()
    {
        var skus = Skus();

        var add = GroupsLicensing.Diff((JsonArray)JsonNode.Parse("""[ { "skuPartNumber": "ENTERPRISEPREMIUM", "disabledPlans": [] } ]""")!, null, skus);
        var added = Assert.Single(add.Adds);
        Assert.Equal(SkuId, added.SkuId);
        Assert.Equal("ENTERPRISEPREMIUM", added.SkuPartNumber);
        Assert.Empty(add.Removes);
        Assert.Empty(add.Errors);
        Assert.True(add.HasChanges);
        var child = Assert.Single(add.Children);
        Assert.Equal(PropertyChangeType.Create, child.Kind);
        Assert.Equal("[0]", child.Path.ToString());

        var remove = GroupsLicensing.Diff(new JsonArray(), (JsonArray)JsonNode.Parse("[ { \"skuId\": \"" + SkuId + "\", \"disabledPlans\": [] } ]")!, skus);
        Assert.Equal([SkuId], remove.Removes);
        Assert.Empty(remove.Adds);
        Assert.Equal(PropertyChangeType.Delete, Assert.Single(remove.Children).Kind);

        var plans = GroupsLicensing.Diff(
            (JsonArray)JsonNode.Parse("[ { \"skuId\": \"" + SkuId + "\", \"disabledPlans\": [\"p1\"] } ]")!,
            (JsonArray)JsonNode.Parse("[ { \"skuId\": \"" + SkuId.ToUpperInvariant() + "\", \"disabledPlans\": [] } ]")!,
            skus);
        Assert.Single(plans.Adds);
        Assert.Empty(plans.Removes);
        Assert.Equal(PropertyChangeType.Modify, Assert.Single(plans.Children).Kind);

        var same = GroupsLicensing.Diff(
            (JsonArray)JsonNode.Parse("""[ { "skuPartNumber": "ENTERPRISEPREMIUM", "disabledPlans": ["b", "a"] } ]""")!,
            (JsonArray)JsonNode.Parse("[ { \"skuId\": \"" + SkuId + "\", \"disabledPlans\": [\"a\", \"b\"] } ]")!,
            skus);
        Assert.False(same.HasChanges);
        Assert.Empty(same.Children);
    }

    [Fact]
    public void LicenseDiff_ReportsUnknownPartNumbers_AndEntriesWithoutAKey()
    {
        var unknown = GroupsLicensing.Diff((JsonArray)JsonNode.Parse("""[ { "skuPartNumber": "NOPE" } ]""")!, null, Skus());
        Assert.Contains(unknown.Errors, e => e.Contains("no subscription", StringComparison.Ordinal) && e.Contains("NOPE", StringComparison.Ordinal));
        Assert.Empty(unknown.Adds);

        var keyless = GroupsLicensing.Diff((JsonArray)JsonNode.Parse("""[ { "disabledPlans": [] }, "text" ]""")!, null, Skus());
        Assert.Equal(2, keyless.Errors.Count);
        Assert.Contains(keyless.Errors, e => e.Contains("needs skuId or skuPartNumber", StringComparison.Ordinal));
        Assert.Contains(keyless.Errors, e => e.Contains("must be an object", StringComparison.Ordinal));

        var noTable = GroupsLicensing.Diff((JsonArray)JsonNode.Parse("""[ { "skuPartNumber": "ANY" } ]""")!, null, null, JsonPath.Of("properties", "assignedLicenses"));
        Assert.Empty(noTable.Errors);
        Assert.Null(Assert.Single(noTable.Adds).SkuId);
    }

    // ---- edge recordings ----

    [Fact]
    public async Task RecordingsFor_RecordsOnlyPrincipalSideEdgesTheDeploymentCreated()
    {
        var host = new ProviderHost();
        var plan = await host.ExpandAsync(host.Resources(Templates.Resource("Microsoft.Entra/users", "alice@contoso.com", """{ "displayName": "Alice", "memberOf": ["g1"], "manager": "m1" }""")));
        var instance = plan.Instances.Single();
        var memberOfSpec = instance.Definition.Relationships.Single(r => r.Property == "memberOf");
        var managerSpec = instance.Definition.Relationships.Single(r => r.Property == "manager");
        Assert.True(host.EdgeKinds.TryGet(EdgeKind.GroupMembership, out var membership));
        Assert.True(host.EdgeKinds.TryGet(EdgeKind.Manager, out var manager));

        var row = new EdgeRow(instance) { SelfId = "u1" };
        var memberOf = new EdgePropertyPlan(memberOfSpec, membership, isDeclared: true);
        var declaration = new EdgeDeclaration(instance, memberOfSpec, EdgeKind.GroupMembership, 0, JsonPath.Of("properties", "memberOf")) { SelfId = "u1", OtherId = "g1" };
        memberOf.Declarations.Add(declaration);
        row.Properties["memberOf"] = memberOf;
        var managerPlan = new EdgePropertyPlan(managerSpec, manager, isDeclared: false);
        managerPlan.PreviousRecords.Add(JsonValue.Create("m0"));
        row.Properties["manager"] = managerPlan;

        var edge = declaration.Edge!;
        Assert.Equal("g1", edge.TargetId);
        Assert.Equal("u1", edge.PrincipalId);
        var operation = new EdgeOperation(edge, EdgeAction.Add, membership, GraphApi.V1, RetryHint.Default, null);

        // Added by the pass: recorded; the undeclared single-valued manager keeps its previous record.
        var added = new EdgeApplyResult();
        added.Record(operation, EdgeOperationResult.Ok(null));
        Assert.Equal("""{"memberOf":["g1"],"manager":"m0"}""", EdgePlan.RecordingsFor(row, added)!.ToJsonString());

        // Already in place (created by someone else) and never recorded before: not recorded.
        row.Properties.Remove("manager");
        var already = new EdgeApplyResult();
        already.Record(operation, EdgeOperationResult.Already);
        Assert.Null(EdgePlan.RecordingsFor(row, already));

        // Previously recorded by this deployment: still recorded.
        memberOf.PreviouslyRecorded.Add(edge);
        Assert.Equal("""{"memberOf":["g1"]}""", EdgePlan.RecordingsFor(row, already)!.ToJsonString());

        // Target-side properties never record.
        var groupPlan = await host.ExpandAsync(host.Resources(Templates.Resource("Microsoft.Entra/groups", "g", """{ "displayName": "G", "members": ["u1"] }""")));
        var group = groupPlan.Instances.Single();
        var membersSpec = group.Definition.Relationships.Single(r => r.Property == "members");
        var groupRow = new EdgeRow(group) { SelfId = "g1" };
        var members = new EdgePropertyPlan(membersSpec, membership, isDeclared: true);
        members.Declarations.Add(new EdgeDeclaration(group, membersSpec, EdgeKind.GroupMembership, 0, JsonPath.Of("properties", "members")) { SelfId = "g1", OtherId = "u1" });
        groupRow.Properties["members"] = members;
        Assert.Null(EdgePlan.RecordingsFor(groupRow, added));
    }
}
