using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>Microsoft.Entra/azureRoleEligibilityScheduleRequests</c> over <see cref="FakeAzureResourceManager"/> under a real <see cref="ArmClient"/>: the Direct
/// schedule of exactly the declared scope adopted as NoChange (a parent's, a child's and a group-held one ignored, every page read), a differing schedule
/// reported and never rewritten, ONE AdminAssign request for a missing one (the qualified role, the default justification and schedule), RoleAssignmentExists
/// adopted, a policy refusal explained, management group scopes (tenant-level role ids), and AdminRemove on deployment delete.
/// </summary>
public sealed class AzureRoleEligibilityScheduleRequestsProviderTests
{
    private const string Type = AzureRoleEligibilityScheduleRequestsProvider.ResourceType;
    private const string Name = "5a1e2b3c-4d5e-4f60-8a7b-9c0d1e2f3a4b";
    private const string Subscription = "6e1f4c2a-0000-4000-8000-000000000003";
    private const string SubscriptionScope = "/subscriptions/" + Subscription;
    private const string Rg = SubscriptionScope + "/resourceGroups/rg-earm";
    private const string Mg = "/providers/Microsoft.Management/managementGroups/mg-platform";
    private const string Contributor = FakeAzureResourceManager.Contributor;
    private const string Principal = "7d3c2b1a-0000-4000-8000-0000000000c1";
    private const string OtherPrincipal = "7d3c2b1a-0000-4000-8000-0000000000c2";
    private const string QualifiedContributor = SubscriptionScope + "/providers/Microsoft.Authorization/roleDefinitions/" + Contributor;
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed record Env(ProviderHost Host, FakeAzureResourceManager Arm, AzureRoleEligibilityScheduleRequestsProvider Provider);

    private static Env Create()
    {
        var host = new ProviderHost();
        var arm = new FakeAzureResourceManager();
        arm.Scopes.Add(SubscriptionScope);
        arm.Scopes.Add(Rg);
        var provider = new AzureRoleEligibilityScheduleRequestsProvider(host.Catalog, arm.CreateClient()) { Clock = new FixedClock(Now) };
        host.Registry.Register(provider, replace: true);
        return new Env(host, arm, provider);
    }

    private static string Eligibility(string scope = SubscriptionScope, string role = "\"role\": \"Contributor\"", string principal = Principal, string extra = "", string name = Name)
    {
        return Templates.Resource(Type, name, "{ \"scope\": \"" + scope + "\", " + role + ", \"principalId\": \"" + principal + "\"" + extra + " }");
    }

    private static ResourceKey Key(string scope = SubscriptionScope, string role = QualifiedContributor, string principal = Principal)
    {
        return new ResourceKey { Type = Type, Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["scope"] = scope, ["roleDefinitionId"] = role, ["principalId"] = principal } };
    }

    private static List<RecordedHttpRequest> Lists(FakeAzureResourceManager arm)
    {
        return arm.Requests.Where(r => r.Method == "GET" && r.Uri.AbsolutePath.EndsWith("/roleEligibilitySchedules", StringComparison.Ordinal)).ToList();
    }

    // ---- adopt ----

    [Fact]
    public async Task ADirectEligibilityAtTheScope_IsAdopted_AsNoChange_WithoutARequest()
    {
        var env = Create();
        env.Arm.AddSchedule(SubscriptionScope, Contributor, Principal);

        var run = await env.Host.CreateAsync(env.Host.Resources(Eligibility()));

        Assert.True(run.Succeeded);
        var change = run.Change(Name);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.StartsWith(SubscriptionScope + "/providers/Microsoft.Authorization/roleEligibilitySchedules/", change.GraphId, StringComparison.Ordinal);
        Assert.Contains(change.Reasons, r => r.Contains("adopted as it is", StringComparison.Ordinal));
        Assert.Empty(env.Arm.EligibilityRequests);
        Assert.Contains("api-version=2020-10-01", Lists(env.Arm)[0].Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("$filter", Uri.UnescapeDataString(Lists(env.Arm)[0].Uri.Query), StringComparison.Ordinal);
        Assert.Empty(run.Requests);
    }

    [Theory]
    [InlineData(SubscriptionScope, "Direct", Rg)]
    [InlineData(Rg, "Direct", SubscriptionScope)]
    [InlineData(SubscriptionScope, "Group", SubscriptionScope)]
    [InlineData(SubscriptionScope, "Inherited", SubscriptionScope)]
    public async Task AParentsAChildsOrAGroupHeldEligibility_DoesNotCount_SoOneIsRequested(string liveScope, string memberType, string declaredScope)
    {
        var env = Create();
        env.Arm.AddSchedule(liveScope, Contributor, Principal, memberType);

        var run = await env.Host.CreateAsync(env.Host.Resources(Eligibility(scope: declaredScope)));

        Assert.True(run.Succeeded, run.Result?.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(ChangeType.Create, run.Change(Name).Type);
        var request = Assert.Single(env.Arm.EligibilityRequests);
        Assert.StartsWith(declaredScope + "/providers/Microsoft.Authorization/roleEligibilityScheduleRequests/", request.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherPrincipalsEligibility_DoesNotCount()
    {
        var env = Create();
        env.Arm.AddSchedule(SubscriptionScope, Contributor, OtherPrincipal);

        var change = (await env.Host.WhatIfAsync(env.Host.Resources(Eligibility()))).Change(Name);

        Assert.Equal(ChangeType.Create, change.Type);
    }

    [Fact]
    public async Task EveryPage_IsRead_UntilTheEligibilityIsFound()
    {
        var env = Create();
        env.Arm.PageSize = 2;
        env.Arm.AddSchedule(SubscriptionScope, Contributor, OtherPrincipal);
        env.Arm.AddSchedule(Rg, Contributor, Principal);
        env.Arm.AddSchedule(SubscriptionScope, FakeAzureResourceManager.Reader, Principal);
        env.Arm.AddSchedule(SubscriptionScope, Contributor, Principal);

        var change = (await env.Host.WhatIfAsync(env.Host.Resources(Eligibility()))).Change(Name);

        Assert.Equal(ChangeType.NoChange, change.Type);
        var lists = Lists(env.Arm);
        Assert.Equal(2, lists.Count);
        Assert.Contains("$skiptoken=2", lists[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALiveEndTheDeclarationDoesNotHave_IsReported_AndNotRewritten()
    {
        var env = Create();
        env.Arm.AddSchedule(SubscriptionScope, Contributor, Principal, endDateTime: "2027-10-28T12:00:00Z");

        var run = await env.Host.CreateAsync(env.Host.Resources(Eligibility()));

        Assert.True(run.Succeeded);
        var change = run.Change(Name);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.Contains(change.Reasons, r => r.Contains("the live eligibility ends 2027-10-28T12:00:00.0000000+00:00, but the declaration is permanent (NoExpiration)", StringComparison.Ordinal)
            && r.Contains("is not changed in place", StringComparison.Ordinal));
        Assert.Empty(env.Arm.EligibilityRequests);
    }

    [Fact]
    public async Task AMatchingEnd_IsNotReported()
    {
        var env = Create();
        env.Arm.AddSchedule(SubscriptionScope, Contributor, Principal, endDateTime: "2027-10-28T12:00:00.4Z");

        var change = (await env.Host.WhatIfAsync(env.Host.Resources(Eligibility(extra: ", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2027-10-28T12:00:00Z\" } }")))).Change(Name);

        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.DoesNotContain(change.Reasons, r => r.Contains("the live eligibility", StringComparison.Ordinal));
    }

    // ---- create ----

    [Fact]
    public async Task AMissingEligibility_IsOneAdminAssignRequest_WithTheQualifiedRole_TheDefaults_AndTheRerunIsNoChange()
    {
        var env = Create();
        var document = env.Host.Resources(Eligibility());

        var whatIf = await env.Host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);
        Assert.Contains(whatIf.Change(Name).Reasons, r => r.Contains("AdminAssign", StringComparison.Ordinal));
        Assert.Empty(env.Arm.EligibilityRequests);

        var run = await env.Host.CreateAsync(document);

        Assert.True(run.Succeeded, run.Result?.Diagnostics.FirstOrDefault()?.Message);
        var (path, body) = Assert.Single(env.Arm.EligibilityRequests);
        var request = env.Arm.Requests.Single(r => r.Method == "PUT");
        Assert.Contains("api-version=2022-04-01-preview", request.Uri.Query, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(path[(path.LastIndexOf('/') + 1)..], out _));
        var properties = body["properties"]!.AsObject();
        Assert.Equal("AdminAssign", properties["requestType"]!.GetValue<string>());
        Assert.Equal(QualifiedContributor, properties["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal(Principal, properties["principalId"]!.GetValue<string>());
        Assert.Equal("Deployed by earm", properties["justification"]!.GetValue<string>());
        Assert.Equal("NoExpiration", properties["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        Assert.Equal("2026-10-28T12:00:00.000Z", properties["scheduleInfo"]!["startDateTime"]!.GetValue<string>());
        Assert.Single(env.Arm.Schedules);
        Assert.StartsWith(SubscriptionScope + "/providers/Microsoft.Authorization/roleEligibilitySchedules/", run.Op(Name).GraphId, StringComparison.Ordinal);

        var rerun = await env.Host.WhatIfAsync(document);
        Assert.Equal(ChangeType.NoChange, rerun.Change(Name).Type);
    }

    [Fact]
    public async Task TheDeclaredJustificationAndSchedule_AreSent_InArmsCasing()
    {
        var env = Create();
        string extra = ", \"justification\": \"Platform team\", \"scheduleInfo\": { \"startDateTime\": \"2026-11-01T00:00:00Z\", \"expiration\": { \"type\": \"afterDuration\", \"duration\": \"P180D\" } }";

        var run = await env.Host.CreateAsync(env.Host.Resources(Eligibility(extra: extra)));

        Assert.True(run.Succeeded);
        var properties = Assert.Single(env.Arm.EligibilityRequests).Body["properties"]!.AsObject();
        Assert.Equal("Platform team", properties["justification"]!.GetValue<string>());
        Assert.Equal("2026-11-01T00:00:00.000Z", properties["scheduleInfo"]!["startDateTime"]!.GetValue<string>());
        Assert.Equal("AfterDuration", properties["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        Assert.Equal("P180D", properties["scheduleInfo"]!["expiration"]!["duration"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnAfterDateTimeExpiration_SendsItsEnd_AndAPastStart_IsNow()
    {
        var env = Create();
        string extra = ", \"scheduleInfo\": { \"startDateTime\": \"2020-01-01T00:00:00Z\", \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"2027-01-31T00:00:00Z\" } }";

        var run = await env.Host.CreateAsync(env.Host.Resources(Eligibility(extra: extra)));

        Assert.True(run.Succeeded);
        var schedule = Assert.Single(env.Arm.EligibilityRequests).Body["properties"]!["scheduleInfo"]!;
        Assert.Equal("2026-10-28T12:00:00.000Z", schedule["startDateTime"]!.GetValue<string>());
        Assert.Equal("AfterDateTime", schedule["expiration"]!["type"]!.GetValue<string>());
        Assert.Equal("2027-01-31T00:00:00.000Z", schedule["expiration"]!["endDateTime"]!.GetValue<string>());
    }

    [Fact]
    public async Task RoleAssignmentExists_AfterThePlan_IsAdopted()
    {
        var env = Create();
        var whatIf = await env.Host.WhatIfAsync(env.Host.Resources(Eligibility()));
        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);
        env.Arm.AddSchedule(SubscriptionScope, Contributor, Principal);

        var applied = await env.Provider.CreateAsync(whatIf.Plan.Instances.Single(), env.Host.Fake, ProviderHost.Ct);

        Assert.StartsWith(SubscriptionScope + "/providers/Microsoft.Authorization/roleEligibilitySchedules/", applied.GraphId, StringComparison.Ordinal);
        Assert.Single(env.Arm.EligibilityRequests);
        Assert.Single(env.Arm.Schedules);
    }

    [Fact]
    public async Task APolicyRefusal_IsPimPolicyViolation_NamingTheAzurePolicyType()
    {
        var env = Create();
        env.Arm.RequestRefusal = _ => (HttpStatusCode.BadRequest, "RoleAssignmentRequestPolicyValidationFailed", "The following policy rules failed: [\"ExpirationRule\"]");

        var run = await env.Host.CreateAsync(env.Host.Resources(Eligibility()));

        Assert.False(run.Succeeded);
        var op = run.Op(Name);
        Assert.Equal(EngineDiagnosticCodes.PimPolicyViolation, op.ErrorCode);
        Assert.Contains("ExpirationRule", op.Error, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Entra/azureRoleManagementPolicies", op.Error, StringComparison.Ordinal);
        Assert.Empty(env.Arm.Schedules);
    }

    // ---- role and scope forms ----

    [Fact]
    public async Task TheRoleName_IsResolvedAtTheScope_AndARoleDefinitionIdIsQualified()
    {
        var env = Create();

        var byName = await env.Host.WhatIfAsync(env.Host.Resources(Eligibility(scope: Rg)));
        var byId = await env.Host.WhatIfAsync(env.Host.Resources(Eligibility(scope: Rg, role: "\"roleDefinitionId\": \"" + Contributor.ToUpperInvariant() + "\"")));

        Assert.Equal(QualifiedContributor, byName.Plan.Instances.Single().Properties!["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal(QualifiedContributor, byId.Plan.Instances.Single().Properties!["roleDefinitionId"]!.GetValue<string>());
        Assert.Single(env.Arm.Requests, r => r.Uri.AbsolutePath == Rg + "/providers/Microsoft.Authorization/roleDefinitions");
    }

    [Fact]
    public async Task AManagementGroupScope_UsesTheTenantLevelRoleId_ForTheLookupAndTheRequest()
    {
        var env = Create();
        env.Arm.Scopes.Add(Mg);
        env.Arm.AddSchedule(Mg + "/subscriptions/x", Contributor, Principal);

        var run = await env.Host.CreateAsync(env.Host.Resources(Eligibility(scope: Mg + "/")));

        Assert.True(run.Succeeded, run.Result?.Diagnostics.FirstOrDefault()?.Message);
        Assert.Single(env.Arm.Requests, r => r.Uri.AbsolutePath == Mg + "/providers/Microsoft.Authorization/roleDefinitions");
        var (path, body) = Assert.Single(env.Arm.EligibilityRequests);
        Assert.StartsWith(Mg + "/providers/Microsoft.Authorization/roleEligibilityScheduleRequests/", path, StringComparison.Ordinal);
        Assert.Equal("/providers/Microsoft.Authorization/roleDefinitions/" + Contributor, body["properties"]!["roleDefinitionId"]!.GetValue<string>());

        var rerun = await env.Host.WhatIfAsync(env.Host.Resources(Eligibility(scope: Mg)));
        Assert.Equal(ChangeType.NoChange, rerun.Change(Name).Type);
    }

    [Fact]
    public async Task ANameThatIsNotAGuid_IsRefused_BeforeAnyResourceManagerCall()
    {
        var env = Create();

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => env.Host.WhatIfAsync(env.Host.Resources(Eligibility(name: "contributors-eligible"))));

        Assert.Contains("must be a GUID", error.Message, StringComparison.Ordinal);
        Assert.Empty(env.Arm.Requests);
    }

    [Fact]
    public async Task APrincipalKnownOnlyAtDeployment_PlansACreate_WithoutAScheduleRead()
    {
        var env = Create();
        string group = Templates.Resource("Microsoft.Entra/groups", "platform", "{ \"displayName\": \"Platform\", \"mailEnabled\": false, \"mailNickname\": \"platform\", \"securityEnabled\": true }");
        string eligibility = Templates.Resource(Type, Name, "{ \"scope\": \"" + SubscriptionScope + "\", \"role\": \"Contributor\", \"principalId\": \"[reference(resourceId('Microsoft.Entra/groups', 'platform')).id]\" }");

        var change = (await env.Host.WhatIfAsync(env.Host.Resources(group + ", " + eligibility))).Change(Name);

        Assert.Equal(ChangeType.Create, change.Type);
        Assert.True(change.IsUnknown);
        Assert.Null(change.UnsupportedReason);
        Assert.Empty(Lists(env.Arm));
    }

    // ---- delete ----

    [Fact]
    public async Task DeploymentDelete_SendsOneAdminRemoveRequest()
    {
        var env = Create();
        env.Arm.AddSchedule(SubscriptionScope, Contributor, Principal);

        var result = await env.Host.DeleteDeclaredAsync(env.Host.Resources(Eligibility()));

        Assert.True(result.Succeeded);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(DeletionStatus.Deleted, outcome.Status);
        var (_, body) = Assert.Single(env.Arm.EligibilityRequests);
        Assert.Equal("AdminRemove", body["properties"]!["requestType"]!.GetValue<string>());
        Assert.Equal(QualifiedContributor, body["properties"]!["roleDefinitionId"]!.GetValue<string>());
        Assert.Empty(env.Arm.Schedules);
    }

    [Fact]
    public async Task DeleteAsync_OfAnEligibilityAlreadyGone_IsNotAnError()
    {
        var env = Create();
        var live = new ExistingResource(SubscriptionScope + "/providers/Microsoft.Authorization/roleEligibilitySchedules/gone", new JsonObject(), new JsonObject());

        await env.Provider.DeleteAsync(Key(), live, env.Host.Fake, ProviderHost.Ct);

        Assert.Single(env.Arm.EligibilityRequests);
    }

    // ---- permissions, registries, order ----

    [Fact]
    public async Task RequiredPermissions_AreEmpty_AndRequiredAzureRoles_NameTheScope()
    {
        var env = Create();
        var whatIf = await env.Host.WhatIfAsync(env.Host.Resources(Eligibility(scope: SubscriptionScope + "/")));

        Assert.Empty(env.Provider.RequiredPermissions(whatIf.Plan.Instances.Single()));
        Assert.Equal(["Owner or User Access Administrator at '" + SubscriptionScope + "' (Azure role eligibility)"], env.Provider.RequiredAzureRoles(whatIf.Plan.Instances.Single()));
        Assert.Equal(["Owner or User Access Administrator at 'the declared scope' (Azure role eligibility)"], env.Provider.RequiredAzureRoles(null));
        Assert.IsAssignableFrom<IResourceManagerProvider>(env.Provider);
    }

    [Fact]
    public async Task TheOfflineRegistry_ServesTheType_ButAnyResourceManagerCallFails()
    {
        var provider = Assert.IsType<AzureRoleEligibilityScheduleRequestsProvider>(ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default).Get(Type));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadAsync(Key(), new FakeGraphClient(), ProviderHost.Ct));

        Assert.Contains("without a Resource Manager client", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProvider_IsAppliedInPhase5_AndDeletedInOrder2()
    {
        Assert.Equal(5, ApplyPhases.Of(Type));
        Assert.Equal(2, ApplyPhases.DeleteOrderOf(Type));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
