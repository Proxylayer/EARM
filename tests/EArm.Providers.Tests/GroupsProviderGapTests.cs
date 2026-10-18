using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Groups provider branches beyond G1–G9: permission derivation, ignored rows, the HiddenMembership and dynamic-rule refusals, licence shape errors, the best-effort recycle-bin probe and the delete warnings.</summary>
public sealed class GroupsProviderGapTests
{
    private const string Type = "Microsoft.Entra/groups";
    private const string SomeId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001";

    private static string Group(string name, string properties, string? extra = null)
    {
        return Templates.Resource(Type, name, properties, extra: extra);
    }

    // ---- permissions ----

    [Theory]
    [InlineData("{ \"displayName\": \"G\" }", new[] { "Group.ReadWrite.All" })]
    [InlineData("{ \"displayName\": \"G\", \"members\": [] }", new[] { "Group.ReadWrite.All" })]
    [InlineData("{ \"displayName\": \"G\", \"members\": [\"" + SomeId + "\"] }", new[] { "Group.ReadWrite.All", "GroupMember.ReadWrite.All" })]
    [InlineData("{ \"displayName\": \"G\", \"owners\": [\"" + SomeId + "\"] }", new[] { "Group.ReadWrite.All", "GroupMember.ReadWrite.All" })]
    [InlineData("{ \"displayName\": \"G\", \"memberOf\": [\"" + SomeId + "\"] }", new[] { "Group.ReadWrite.All", "GroupMember.ReadWrite.All" })]
    [InlineData("{ \"displayName\": \"G\", \"administrativeUnits\": [\"" + SomeId + "\"] }", new[] { "Group.ReadWrite.All", "AdministrativeUnit.ReadWrite.All" })]
    public async Task RequiredPermissions_FollowTheEdgePropertiesTheTemplateDeclares(string properties, string[] expected)
    {
        var host = new ProviderHost();
        var provider = host.Provider(Type);
        var instance = await InstanceFactory.WithPropertiesAsync(host, Group("g1", "{ \"displayName\": \"G\" }"), properties);
        instance.EdgeProperties = (JsonObject)JsonNode.Parse(properties)!;

        var permissions = provider.RequiredPermissions(instance);

        foreach (string permission in expected)
        {
            Assert.Contains(permission, permissions);
        }

        Assert.Contains("Group.ReadWrite.All", provider.RequiredPermissions(null));
    }

    // ---- plan ----

    [Fact]
    public async Task AConditionThatIsFalse_IsAnIgnoreRow_WithoutGraphWrites()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Group("g1", "{ \"displayName\": \"G\" }", ", \"condition\": false")));

        Check.Succeeded(run);
        Assert.Equal(OperationStatus.Ignored, run.Op("g1").Status);
        Check.NoWrites(run);
        Assert.Null(host.Directory.Find("/groups", "uniqueName", "g1"));
    }

    [Fact]
    public async Task AHiddenMembershipGroup_CannotChangeItsVisibility()
    {
        var host = new ProviderHost();
        host.Directory.AddGroup("hidden", "Hidden", g =>
        {
            g["visibility"] = "HiddenMembership";
            g["groupTypes"] = new JsonArray("Unified");
            g["mailEnabled"] = true;
            g["securityEnabled"] = false;
            g["mailNickname"] = "hidden";
        });

        var whatIf = await host.WhatIfAsync(host.Resources(Group("hidden", "{ \"displayName\": \"Hidden\", \"groupTypes\": [\"Unified\"], \"visibility\": \"Private\" }")));

        var row = whatIf.ChangeOfType(Type);
        Assert.True(row.HasError);
        Assert.Contains("HiddenMembership", row.UnsupportedReason, StringComparison.Ordinal);
        Check.NoWrites(whatIf);
    }

    [Fact]
    public async Task ConvertingToADynamicGroupWithoutARule_IsRefused()
    {
        var host = new ProviderHost();
        host.Directory.AddGroup("plain", "Plain", g => g["mailNickname"] = "plain");

        var whatIf = await host.WhatIfAsync(host.Resources(Group("plain", "{ \"displayName\": \"Plain\", \"groupTypes\": [\"DynamicMembership\"] }")));

        var row = whatIf.ChangeOfType(Type);
        Assert.True(row.HasError);
        Assert.Contains("membershipRule", row.UnsupportedReason, StringComparison.Ordinal);
    }

    // ---- licences ----

    [Fact]
    public async Task AssignedLicenses_ThatAreNotAnArray_AreARowError_AndAnUnknownPlaceholderIsAnUnknownRow()
    {
        var host = new ProviderHost();
        var provider = host.Provider(Type);
        var broken = await InstanceFactory.WithPropertiesAsync(host, Group("g1", "{ \"displayName\": \"G\" }"), "{ \"displayName\": \"G\" }");
        broken.EdgeProperties = new JsonObject { ["assignedLicenses"] = "E5" };
        var unknown = await InstanceFactory.WithPropertiesAsync(host, Group("g2", "{ \"displayName\": \"G\" }"), "{ \"displayName\": \"G\" }");
        unknown.EdgeProperties = new JsonObject { ["assignedLicenses"] = UnknownJson.Create("reference('x').licenses", UnknownOrigins.Reference) };

        var notAnArray = provider.Plan(broken, null, PlanOptions.Default);
        var pending = provider.Plan(unknown, null, PlanOptions.Default);

        Assert.True(notAnArray.HasError);
        Assert.Contains("assignedLicenses must be an array", notAnArray.UnsupportedReason, StringComparison.Ordinal);
        Assert.True(pending.IsUnknown);
        Assert.Contains("only known at deployment time", string.Join(";", pending.Reasons), StringComparison.Ordinal);
    }

    // ---- soft-deleted probe and delete warnings ----

    [Fact]
    public async Task AnUnreadableRecycleBin_DoesNotBlockTheCreate()
    {
        var host = new ProviderHost();
        host.Fake.Failures.Forbid("/directory/deletedItems", "GET");

        var run = await host.CreateAsync(host.Resources(Group("m365", "{ \"displayName\": \"M\", \"groupTypes\": [\"Unified\"] }")));

        Check.Succeeded(run);
        Assert.NotNull(host.Directory.Find("/groups", "uniqueName", "m365"));
    }

    [Theory]
    // Review 3I (intentional change): security groups are soft-deleted too (Learn group-delete), so neither created kind warns; the permanent kinds are adopt-only (see Review3I_GroupSoftDeleteTests).
    [InlineData("{ \"displayName\": \"Sec\" }", false)]
    [InlineData("{ \"displayName\": \"M365\", \"groupTypes\": [\"Unified\"] }", false)]
    public async Task DeleteWarnings_DependOnTheGroupKind(string properties, bool permanent)
    {
        var host = new ProviderHost();
        var document = host.Resources(Group("victim", properties));
        Check.Succeeded(await host.CreateAsync(document));

        var whatIf = await host.WhatIfAsync(document);

        var target = Assert.Single(EArm.Engine.Execution.TemplateDeleter.FindDeclaredObjects(whatIf.Plan));
        Assert.Equal(permanent, (target.Risk ?? string.Empty).Contains("deleted permanently", StringComparison.Ordinal));
    }
}
