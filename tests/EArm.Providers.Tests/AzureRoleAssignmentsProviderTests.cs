using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions.Functions;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>Microsoft.Entra/azureRoleAssignments</c> over <see cref="FakeAzureResourceManager"/> under a real <see cref="ArmClient"/>: an assignment of exactly the
/// declared scope adopted under any name (a parent's and a child's ignored, every page read through nextLink), a missing one created with ONE PUT named
/// guid(scope, role, principal) carrying principalType and description, RoleAssignmentExists adopted, management group scopes (tenant-level role ids), and
/// DELETE of the matched assignment on deployment delete.
/// </summary>
public sealed class AzureRoleAssignmentsProviderTests
{
    private const string Type = AzureRoleAssignmentsProvider.ResourceType;
    private const string Name = "8b2c3d4e-5f60-4a7b-8c9d-0e1f2a3b4c5d";
    private const string Subscription = "6e1f4c2a-0000-4000-8000-000000000003";
    private const string SubscriptionScope = "/subscriptions/" + Subscription;
    private const string Rg = SubscriptionScope + "/resourceGroups/rg-earm";
    private const string Mg = "/providers/Microsoft.Management/managementGroups/mg-platform";
    private const string Reader = FakeAzureResourceManager.Reader;
    private const string Principal = "7d3c2b1a-0000-4000-8000-0000000000c1";
    private const string OtherPrincipal = "7d3c2b1a-0000-4000-8000-0000000000c2";
    private const string QualifiedReader = SubscriptionScope + "/providers/Microsoft.Authorization/roleDefinitions/" + Reader;

    private sealed record Env(ProviderHost Host, FakeAzureResourceManager Arm, AzureRoleAssignmentsProvider Provider);

    private static Env Create()
    {
        var host = new ProviderHost();
        var arm = new FakeAzureResourceManager();
        arm.Scopes.Add(SubscriptionScope);
        arm.Scopes.Add(Rg);
        var provider = new AzureRoleAssignmentsProvider(host.Catalog, arm.CreateClient());
        host.Registry.Register(provider, replace: true);
        return new Env(host, arm, provider);
    }

    private static string Assignment(string scope = SubscriptionScope, string role = "\"role\": \"Reader\"", string principal = Principal, string extra = "")
    {
        return Templates.Resource(Type, Name, "{ \"scope\": \"" + scope + "\", " + role + ", \"principalId\": \"" + principal + "\"" + extra + " }");
    }

    private static ResourceKey Key(string scope = SubscriptionScope, string role = QualifiedReader, string principal = Principal)
    {
        return new ResourceKey { Type = Type, Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["scope"] = scope, ["roleDefinitionId"] = role, ["principalId"] = principal } };
    }

    private static List<RecordedHttpRequest> Lists(FakeAzureResourceManager arm)
    {
        return arm.Requests.Where(r => r.Method == "GET" && r.Uri.AbsolutePath.EndsWith("/roleAssignments", StringComparison.Ordinal)).ToList();
    }

    // ---- adopt ----

    [Fact]
    public async Task AnAssignmentAtTheScope_UnderAnyName_IsAdopted_AsNoChange()
    {
        var env = Create();
        string portalName = env.Arm.AddAssignment(SubscriptionScope, Reader, Principal);

        var run = await env.Host.CreateAsync(env.Host.Resources(Assignment()));

        Assert.True(run.Succeeded);
        var change = run.Change(Name);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.Equal(SubscriptionScope + "/providers/Microsoft.Authorization/roleAssignments/" + portalName, change.GraphId);
        Assert.Empty(env.Arm.AssignmentPuts);
        var list = Assert.Single(Lists(env.Arm));
        Assert.Contains("api-version=2022-04-01", list.Uri.Query, StringComparison.Ordinal);
        Assert.Empty(run.Requests);
    }

    [Theory]
    [InlineData(SubscriptionScope, Rg)]
    [InlineData(Rg, SubscriptionScope)]
    public async Task AParentsOrAChildsAssignment_DoesNotCount_SoOneIsCreated(string liveScope, string declaredScope)
    {
        var env = Create();
        env.Arm.AddAssignment(liveScope, Reader, Principal);

        var run = await env.Host.CreateAsync(env.Host.Resources(Assignment(scope: declaredScope)));

        Assert.True(run.Succeeded, run.Result?.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(ChangeType.Create, run.Change(Name).Type);
        var (path, _) = Assert.Single(env.Arm.AssignmentPuts);
        Assert.StartsWith(declaredScope + "/providers/Microsoft.Authorization/roleAssignments/", path, StringComparison.Ordinal);
        Assert.Equal(2, env.Arm.Assignments.Count);
    }

    [Fact]
    public async Task EveryPage_IsRead_FollowingNextLink()
    {
        var env = Create();
        env.Arm.PageSize = 1;
        env.Arm.AddAssignment(SubscriptionScope, Reader, OtherPrincipal);
        env.Arm.AddAssignment(Rg, Reader, Principal);
        env.Arm.AddAssignment(SubscriptionScope, FakeAzureResourceManager.Contributor, Principal);
        string match = env.Arm.AddAssignment(SubscriptionScope, Reader, Principal);

        var change = (await env.Host.WhatIfAsync(env.Host.Resources(Assignment()))).Change(Name);

        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.EndsWith(match, change.GraphId, StringComparison.Ordinal);
        Assert.Equal(4, Lists(env.Arm).Count);
    }

    // ---- create ----

    [Fact]
    public async Task AMissingAssignment_IsOnePut_NamedByTheGrant_WithPrincipalTypeAndDescription_AndTheRerunIsNoChange()
    {
        var env = Create();
        var document = env.Host.Resources(Assignment(extra: ", \"principalType\": \"group\", \"description\": \"Platform read access\""));

        var run = await env.Host.CreateAsync(document);

        Assert.True(run.Succeeded, run.Result?.Diagnostics.FirstOrDefault()?.Message);
        var (path, body) = Assert.Single(env.Arm.AssignmentPuts);
        string expectedName = Hashing.GuidV5(Hashing.GuidNamespace, SubscriptionScope + "-" + QualifiedReader + "-" + Principal).ToString("D");
        Assert.Equal(SubscriptionScope + "/providers/Microsoft.Authorization/roleAssignments/" + expectedName, path);
        Assert.Equal(expectedName, AzureRoleAssignmentLookup.AssignmentName(new AzureRoleGrant(SubscriptionScope, QualifiedReader, Principal)));
        var properties = body["properties"]!.AsObject();
        Assert.Equal(QualifiedReader, properties["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal(Principal, properties["principalId"]!.GetValue<string>());
        Assert.Equal("Group", properties["principalType"]!.GetValue<string>());
        Assert.Equal("Platform read access", properties["description"]!.GetValue<string>());
        Assert.Equal(path, run.Op(Name).GraphId);

        var rerun = await env.Host.WhatIfAsync(document);
        Assert.Equal(ChangeType.NoChange, rerun.Change(Name).Type);
    }

    [Fact]
    public async Task WithoutPrincipalTypeOrDescription_NeitherIsSent()
    {
        var env = Create();

        var run = await env.Host.CreateAsync(env.Host.Resources(Assignment(principal: Principal.ToUpperInvariant())));

        Assert.True(run.Succeeded);
        var properties = Assert.Single(env.Arm.AssignmentPuts).Body["properties"]!.AsObject();
        Assert.False(properties.ContainsKey("principalType"));
        Assert.False(properties.ContainsKey("description"));
        Assert.Equal(Principal, properties["principalId"]!.GetValue<string>());
    }

    [Fact]
    public async Task RoleAssignmentExists_UnderAnotherName_AfterThePlan_IsAdopted()
    {
        var env = Create();
        var whatIf = await env.Host.WhatIfAsync(env.Host.Resources(Assignment()));
        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);
        string other = env.Arm.AddAssignment(SubscriptionScope, Reader, Principal);

        var applied = await env.Provider.CreateAsync(whatIf.Plan.Instances.Single(), env.Host.Fake, ProviderHost.Ct);

        Assert.Equal(SubscriptionScope + "/providers/Microsoft.Authorization/roleAssignments/" + other, applied.GraphId);
        Assert.Single(env.Arm.AssignmentPuts);
        Assert.Single(env.Arm.Assignments);
    }

    [Fact]
    public async Task AnInvalidPrincipalType_IsRefused()
    {
        var env = Create();

        var run = await env.Host.CreateAsync(env.Host.Resources(Assignment(extra: ", \"principalType\": \"[concat('Robot')]\"")));

        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.RoleAssignmentInvalid, run.Op(Name).ErrorCode);
        Assert.Contains("must be User, Group or ServicePrincipal", run.Op(Name).Error, StringComparison.Ordinal);
        Assert.Empty(env.Arm.AssignmentPuts);
    }

    // ---- management group ----

    [Fact]
    public async Task AManagementGroupScope_UsesTheTenantLevelRoleId()
    {
        var env = Create();
        env.Arm.Scopes.Add(Mg);

        var run = await env.Host.CreateAsync(env.Host.Resources(Assignment(scope: Mg)));

        Assert.True(run.Succeeded, run.Result?.Diagnostics.FirstOrDefault()?.Message);
        var (path, body) = Assert.Single(env.Arm.AssignmentPuts);
        Assert.StartsWith(Mg + "/providers/Microsoft.Authorization/roleAssignments/", path, StringComparison.Ordinal);
        Assert.Equal("/providers/Microsoft.Authorization/roleDefinitions/" + Reader, body["properties"]!["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal(ChangeType.NoChange, (await env.Host.WhatIfAsync(env.Host.Resources(Assignment(scope: Mg)))).Change(Name).Type);
    }

    // ---- delete ----

    [Fact]
    public async Task DeploymentDelete_DeletesTheMatchedAssignment()
    {
        var env = Create();
        string portalName = env.Arm.AddAssignment(SubscriptionScope, Reader, Principal);
        env.Arm.AddAssignment(SubscriptionScope, Reader, OtherPrincipal);

        var result = await env.Host.DeleteDeclaredAsync(env.Host.Resources(Assignment()));

        Assert.True(result.Succeeded);
        Assert.Equal(DeletionStatus.Deleted, Assert.Single(result.Outcomes).Status);
        Assert.Equal(SubscriptionScope + "/providers/Microsoft.Authorization/roleAssignments/" + portalName, Assert.Single(env.Arm.AssignmentDeletes));
        Assert.Equal(OtherPrincipal, Assert.Single(env.Arm.Assignments).PrincipalId);
    }

    [Fact]
    public async Task DeleteAsync_RefusesAnAssignmentOfAnotherScope()
    {
        var env = Create();
        var live = new ExistingResource(Rg + "/providers/Microsoft.Authorization/roleAssignments/" + Guid.NewGuid(), new JsonObject(), new JsonObject());

        var error = await Assert.ThrowsAsync<ProviderException>(() => env.Provider.DeleteAsync(Key(), live, env.Host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.ReadFailed, error.Code);
        Assert.Empty(env.Arm.AssignmentDeletes);
    }

    // ---- permissions, registries, order ----

    [Fact]
    public async Task RequiredPermissions_AreEmpty_AndRequiredAzureRoles_NameTheScope()
    {
        var env = Create();
        var whatIf = await env.Host.WhatIfAsync(env.Host.Resources(Assignment(scope: Rg)));

        Assert.Empty(env.Provider.RequiredPermissions(null));
        Assert.Equal(["Owner, User Access Administrator or Role Based Access Control Administrator at '" + Rg + "' (Azure role assignment)"], env.Provider.RequiredAzureRoles(whatIf.Plan.Instances.Single()));
    }

    [Fact]
    public async Task TheOfflineRegistry_ServesTheType_ButAnyResourceManagerCallFails()
    {
        var provider = Assert.IsType<AzureRoleAssignmentsProvider>(ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default).Get(Type));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadAsync(Key(), new FakeGraphClient(), ProviderHost.Ct));

        Assert.Contains("without a Resource Manager client", error.Message, StringComparison.Ordinal);
        Assert.True(provider.CreateIsRepeatable);
        Assert.False(provider.KeyReadLagsCreate);
    }

    [Fact]
    public void TheProvider_IsAppliedInPhase5_AndDeletedInOrder2()
    {
        Assert.Equal(5, ApplyPhases.Of(Type));
        Assert.Equal(2, ApplyPhases.DeleteOrderOf(Type));
    }
}
