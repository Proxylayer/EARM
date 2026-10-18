using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>appRoleAssignments</c> prepare step with what-if placeholders: a resource application that does not exist yet, a role value only
/// known at deployment time, and the plan rows that result (unknown, never an error), plus the planner's refusal of shapes that skipped offline validation.
/// </summary>
public sealed class AppRoleAssignmentPrepareTests
{
    private const string Type = AppRoleAssignmentsProvider.ResourceType;
    private const string RoleId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001";

    private static JsonNode Unknown(string symbol)
    {
        return UnknownJson.Create(symbol, UnknownOrigins.Reference);
    }

    private static async Task<(ProviderHost Host, AppRoleAssignmentsProvider Provider, string ResourceId, string PrincipalId)> ArrangeAsync()
    {
        var host = new ProviderHost();
        string resourceId = host.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "Orders API", sp => sp["appRoles"] = new JsonArray(new JsonObject
        {
            ["id"] = RoleId,
            ["value"] = "Orders.Read",
            ["isEnabled"] = true,
            ["allowedMemberTypes"] = new JsonArray("User", "Application"),
        }))["id"]!.GetValue<string>();
        string principalId = host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        return (host, host.Provider<AppRoleAssignmentsProvider>(Type), resourceId, principalId);
    }

    private static Task<ResourceInstance> InstanceAsync(ProviderHost host, string resourceId, string principalId)
    {
        string valid = Templates.Resource(Type, Guid.NewGuid().ToString("D"), "{ \"principalId\": \"" + principalId + "\", \"resourceId\": \"" + resourceId + "\", \"appRoleId\": \"" + RoleId + "\" }");
        return InstanceFactory.WithPropertiesAsync(host, valid, "{ }");
    }

    [Fact]
    public async Task AnUnknownResourceApplication_BecomesAnUnknownResourceId_AndASugarRoleBecomesAnUnknownRoleId()
    {
        var (host, provider, _, principalId) = await ArrangeAsync();
        var instance = await InstanceAsync(host, Guid.NewGuid().ToString("D"), principalId);
        instance.Properties = new JsonObject { ["principalId"] = principalId, ["resourceAppId"] = Unknown("reference('api').appId"), ["appRole"] = "Orders.Read" };

        await provider.PrepareAsync(instance, host.Fake, ProviderHost.Ct);

        var properties = instance.Properties;
        Assert.False(properties.ContainsKey("resourceAppId"));
        Assert.False(properties.ContainsKey("appRole"));
        Assert.True(UnknownJson.IsUnknown(properties["resourceId"]));
        Assert.True(UnknownJson.IsUnknown(properties["appRoleId"]));
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task AnUnknownResourceApplication_WithADeclaredRoleId_KeepsTheRoleId()
    {
        var (host, provider, _, principalId) = await ArrangeAsync();
        var instance = await InstanceAsync(host, Guid.NewGuid().ToString("D"), principalId);
        instance.Properties = new JsonObject { ["principalId"] = principalId, ["resourceAppId"] = Unknown("reference('api').appId"), ["appRoleId"] = RoleId };

        await provider.PrepareAsync(instance, host.Fake, ProviderHost.Ct);

        Assert.Equal(RoleId, instance.Properties!["appRoleId"]!.GetValue<string>());
        Assert.True(UnknownJson.IsUnknown(instance.Properties["resourceId"]));
    }

    [Fact]
    public async Task AnUnknownRoleValue_OnAnExistingResource_BecomesAnUnknownRoleId()
    {
        var (host, provider, resourceId, principalId) = await ArrangeAsync();
        var instance = await InstanceAsync(host, resourceId, principalId);
        instance.Properties = new JsonObject { ["principalId"] = principalId, ["resourceId"] = resourceId, ["appRole"] = Unknown("reference('x').role") };

        await provider.PrepareAsync(instance, host.Fake, ProviderHost.Ct);

        Assert.False(instance.Properties.ContainsKey("appRole"));
        Assert.True(UnknownJson.IsUnknown(instance.Properties["appRoleId"]));
        Assert.Equal(resourceId, instance.Properties["resourceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnknownRoleValue_NextToADeclaredRoleId_KeepsTheDeclaredId()
    {
        var (host, provider, resourceId, principalId) = await ArrangeAsync();
        var instance = await InstanceAsync(host, resourceId, principalId);
        instance.Properties = new JsonObject { ["principalId"] = principalId, ["resourceId"] = resourceId, ["appRoleId"] = RoleId, ["appRole"] = Unknown("reference('x').role") };

        await provider.PrepareAsync(instance, host.Fake, ProviderHost.Ct);

        Assert.Equal(RoleId, instance.Properties!["appRoleId"]!.GetValue<string>());
        Assert.False(instance.Properties.ContainsKey("appRole"));
    }

    [Fact]
    public async Task ADeclaredResourceId_AndRoleId_AreValidatedAgainstTheLiveResource()
    {
        var (host, provider, resourceId, principalId) = await ArrangeAsync();
        var unknownRole = await InstanceAsync(host, resourceId, principalId);
        unknownRole.Properties = new JsonObject { ["principalId"] = principalId, ["resourceId"] = resourceId, ["appRoleId"] = Guid.NewGuid().ToString("D") };
        var missingResource = await InstanceAsync(host, resourceId, principalId);
        missingResource.Properties = new JsonObject { ["principalId"] = principalId, ["resourceId"] = Guid.NewGuid().ToString("D"), ["appRoleId"] = RoleId };
        var valid = await InstanceAsync(host, resourceId, principalId);
        valid.Properties = new JsonObject { ["principalId"] = principalId, ["resourceId"] = resourceId, ["appRoleId"] = RoleId };

        var role = await Assert.ThrowsAsync<ProviderException>(() => provider.PrepareAsync(unknownRole, host.Fake, ProviderHost.Ct));
        var resource = await Assert.ThrowsAsync<ProviderException>(() => provider.PrepareAsync(missingResource, host.Fake, ProviderHost.Ct));
        await provider.PrepareAsync(valid, host.Fake, ProviderHost.Ct);

        Assert.Equal(AppRoleAssignmentsProvider.InvalidAssignment, role.Code);
        Assert.Contains("has no app role with id", role.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", resource.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlan_ForAnUnpreparedSugarDeclaration_StandsInUnknownPlaceholders_AndIsNotAnError()
    {
        var (host, provider, _, principalId) = await ArrangeAsync();
        var instance = await InstanceAsync(host, Guid.NewGuid().ToString("D"), principalId);
        instance.Properties = new JsonObject { ["principalId"] = principalId, ["resourceAppId"] = Guid.NewGuid().ToString("D"), ["appRole"] = "Orders.Read" };

        var change = provider.Plan(instance, null, PlanOptions.Default);

        Assert.False(change.HasError, change.UnsupportedReason);
        Assert.Equal(ChangeType.Create, change.Type);
        Assert.True(UnknownJson.IsUnknown(instance.Properties["resourceId"]));
        Assert.True(UnknownJson.IsUnknown(instance.Properties["appRoleId"]));
    }

    [Fact]
    public async Task APlan_ForAnAssignmentFoundByItsKey_IsNoChange_AndAnyDifference_IsAReplacementByTheSharedPlan()
    {
        var (host, provider, resourceId, principalId) = await ArrangeAsync();
        var live = new JsonObject { ["principalId"] = principalId, ["resourceId"] = resourceId, ["appRoleId"] = RoleId };
        var same = await InstanceAsync(host, resourceId, principalId);
        same.Properties = (JsonObject)live.DeepClone();
        var other = await InstanceAsync(host, resourceId, principalId);
        other.Properties = new JsonObject { ["principalId"] = principalId, ["resourceId"] = resourceId, ["appRoleId"] = Guid.Empty.ToString("D") };

        var unchanged = provider.Plan(same, new ExistingResource("ara1", live, live), PlanOptions.Default);
        var replaced = provider.Plan(other, new ExistingResource("ara1", live, live), PlanOptions.Default);

        Assert.Equal(ChangeType.NoChange, unchanged.Type);
        Assert.Equal(ChangeType.Modify, replaced.Type);
        Assert.Equal(ChangeMode.Recreate, replaced.Mode);
        Assert.False(replaced.HasError);
        Assert.Contains(replaced.Reasons, r => r.Contains("deleted and created again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APlan_ForAnIgnoredInstance_IsAnIgnoreRow()
    {
        var (host, provider, resourceId, principalId) = await ArrangeAsync();
        var instance = await InstanceAsync(host, resourceId, principalId);
        instance.Condition = false;

        var change = provider.Plan(instance, null, PlanOptions.Default);

        Assert.Equal(ChangeType.Ignore, change.Type);
    }
}
