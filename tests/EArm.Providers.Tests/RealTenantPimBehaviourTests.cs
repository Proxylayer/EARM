using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// What a real tenant answered that the first fakes did not model: a 404 (not an empty list) from the schedule list of a principal or group PIM has not seen
/// yet and from the policy assignment list of a group PIM for Groups has not onboarded; 403 UnauthorizedAccessException from PIM for Groups when the caller's
/// role is only eligible (PimGroupAccessDenied); RoleAssignmentExists answered to an adminUpdate while a just-replaced schedule settles; a declared past start
/// that must not be sent on adminUpdate; an authentication method configuration that serves its old values for a while after the PATCH.
/// </summary>
public sealed class RealTenantPimBehaviourTests
{
    private const string RoleType = RoleEligibilityScheduleRequestsProvider.ResourceType;
    private const string GroupType = GroupEligibilityScheduleRequestsProvider.ResourceType;
    private const string Name = "6f1b8a2e-3c4d-4e5f-8a9b-0c1d2e3f4a5b";
    private const string RoleSchedules = FakeRoleScheduleRequests.EligibilitySchedulesPath;
    private const string GroupSchedules = FakeGroupScheduleRequests.EligibilitySchedulesPath;
    private const string GroupRequests = FakeGroupScheduleRequests.EligibilityRequestsPath;

    private static (ProviderHost Host, string AliceId) RoleSetup()
    {
        var host = new ProviderHost();
        BuiltInRoles.Seed(host);
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        return (host, alice);
    }

    private static (ProviderHost Host, string OpsId, string AliceId) GroupSetup()
    {
        var host = new ProviderHost();
        string ops = host.Directory.AddGroup("pim-ops", "PIM Ops")["id"]!.GetValue<string>();
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        return (host, ops, alice);
    }

    private static string RoleEligibility(string principalId, string extra = "")
    {
        return Templates.Resource(RoleType, Name, "{ \"principalId\": \"" + principalId + "\", \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\"" + extra + " }");
    }

    private static string GroupEligibility(string groupId, string principalId, string extra = "")
    {
        return Templates.Resource(GroupType, Name, "{ \"groupId\": \"" + groupId + "\", \"principalId\": \"" + principalId + "\", \"accessId\": \"member\"" + extra + " }");
    }

    private static JsonObject SeedRoleSchedule(ProviderHost host, string principalId)
    {
        return host.Directory.Add(RoleSchedules, new JsonObject
        {
            ["principalId"] = principalId,
            ["roleDefinitionId"] = BuiltInRoles.GlobalReader,
            ["directoryScopeId"] = "/",
            ["appScopeId"] = null,
            ["status"] = "Provisioned",
            ["memberType"] = "Direct",
            ["scheduleInfo"] = new JsonObject
            {
                ["startDateTime"] = "2025-01-01T10:00:00.123Z",
                ["recurrence"] = null,
                ["expiration"] = new JsonObject { ["type"] = "afterDateTime", ["endDateTime"] = "2030-01-01T00:00:00Z", ["duration"] = null },
            },
        });
    }

    // ---- 1: a 404 from the schedule list is "no schedule" ----

    [Fact]
    public async Task ARoleScheduleList_Answering404ForAnUnseenPrincipal_IsNoSchedule_AndTheEligibilityIsCreated()
    {
        var (host, alice) = RoleSetup();
        host.Directory.RoleEligibility.UnseenIds.Add(alice);
        var document = host.Resources(RoleEligibility(alice));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Single(host.Directory.Objects(RoleSchedules));
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(Name).Type);
    }

    [Fact]
    public async Task AGroupScheduleList_Answering404ForAnUnseenGroup_IsNoSchedule_AndTheEligibilityIsCreated()
    {
        var (host, ops, alice) = GroupSetup();
        host.Directory.GroupEligibility.UnseenIds.Add(ops);
        var document = host.Resources(GroupEligibility(ops, alice));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Single(host.Directory.Objects(GroupSchedules));
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(Name).Type);
    }

    [Fact]
    public async Task TheFake_Answers404ForAnUnseenId_AndTheLookupsReadItAsNoSchedule()
    {
        var (host, ops, alice) = GroupSetup();
        host.Directory.GroupEligibility.UnseenIds.Add(alice);
        host.Directory.RoleEligibility.UnseenIds.Add(alice);

        var raw = await Assert.ThrowsAsync<GraphException>(async () =>
        {
            await foreach (var _ in host.Fake.GetAllPagesAsync(new GraphRequest(GroupSchedules, GraphApi.V1).WithFilter("groupId eq '" + ops + "' and principalId eq '" + alice + "'"), ProviderHost.Ct))
            {
            }
        });
        Assert.Equal(HttpStatusCode.NotFound, raw.Status);

        Assert.Null(await GroupSchedulesLookup.FindAsync(host.Fake, GroupSchedules, _ => true, new GroupScheduleTuple(ops, alice, "member"), GraphApi.V1, RetryHint.Default, ProviderHost.Ct));
        Assert.Null(await RoleSchedulesLookup.FindAsync(host.Fake, RoleSchedules, _ => true, new RoleScheduleTuple(alice, BuiltInRoles.GlobalReader, "/", IsAppScope: false), GraphApi.V1, RetryHint.Default, ProviderHost.Ct));
        Assert.Empty(await RoleSchedulesLookup.ReadPrincipalSchedulesAsync(host.Fake, RoleSchedules, _ => true, alice, GraphApi.V1, RetryHint.Default, ProviderHost.Ct));
    }

    // ---- 2: a 404 from the policy assignment list of a group that is not onboarded is "not listed" ----

    [Fact]
    public async Task TheGroupPolicyAssignmentList_Answering404BeforeOnboarding_IsNotListed()
    {
        var (host, ops, _) = GroupSetup();
        host.Directory.RoleManagementPolicies.PoliciesBeforeOnboarding = false;
        var scope = new PolicyScope(RoleManagementPolicyRules.GroupScope, ops, "member");

        var raw = await Assert.ThrowsAsync<GraphException>(async () =>
        {
            await foreach (var _ in host.Fake.GetAllPagesAsync(new GraphRequest(RoleManagementPolicyLookup.AssignmentsPath, GraphApi.V1).WithFilter(RoleManagementPolicyLookup.Filter(scope)), ProviderHost.Ct))
            {
            }
        });
        Assert.Equal(HttpStatusCode.NotFound, raw.Status);
        Assert.Null(await RoleManagementPolicyLookup.FindAsync(host.Fake, scope, GraphApi.V1, RetryHint.Default, ProviderHost.Ct));
    }

    [Fact]
    public async Task TheGroupPolicyAssignmentList_Answering403UnauthorizedAccessExceptionBeforeOnboarding_IsNotListed()
    {
        // Real tenant, probe PP1: before onboarding the lookup answers 403 UnauthorizedAccessException, not 404 or an empty collection.
        var (host, ops, _) = GroupSetup();
        host.Fake.Failures.Add("GET", RoleManagementPolicyLookup.AssignmentsPath, PimGroupForbidden, times: -1);

        Assert.Null(await RoleManagementPolicyLookup.FindAsync(host.Fake, new PolicyScope(RoleManagementPolicyRules.GroupScope, ops, "member"), GraphApi.V1, RetryHint.Default, ProviderHost.Ct));
    }

    [Fact]
    public async Task TheDirectoryRolePolicyAssignmentList_Answering403_StaysAnError()
    {
        var (host, _) = RoleSetup();
        host.Fake.Failures.Add("GET", RoleManagementPolicyLookup.AssignmentsPath, PimGroupForbidden, times: -1);

        await Assert.ThrowsAsync<GraphException>(() => RoleManagementPolicyLookup.FindAsync(host.Fake, new PolicyScope(RoleManagementPolicyRules.DirectoryRoleScope, "/", BuiltInRoles.GlobalReader), GraphApi.V1, RetryHint.Default, ProviderHost.Ct));
    }

    // ---- 3: 403 from PIM for Groups is PimGroupAccessDenied ----

    [Fact]
    public async Task A403FromTheGroupScheduleList_IsNoSchedule_AndThePersisting403OnTheRequest_IsPimGroupAccessDenied()
    {
        // PIM for Groups answers a group it does not know yet (and a caller without the role) with 403 UnauthorizedAccessException on the list: the read
        // is "no schedule"; only the write that keeps answering 403 fails, naming the active roles needed.
        var (host, ops, alice) = GroupSetup();
        host.Directory.GroupEligibility.AccessDenied = true;
        var document = host.Resources(GroupEligibility(ops, alice));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);
        Assert.Null(whatIf.Change(Name).UnsupportedReason);

        var run = await host.CreateAsync(document);

        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.PimGroupAccessDenied, run.Op(Name).ErrorCode);
        string error = run.Op(Name).Error ?? string.Empty;
        Assert.Contains("ACTIVE (not just eligible)", error, StringComparison.Ordinal);
        Assert.Contains("Privileged Role Administrator alone does not cover", error, StringComparison.Ordinal);
        Assert.Contains("403", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGroupScheduleList_Answering403ForAGroupPimDoesNotKnow_IsNoSchedule_AndTheEligibilityIsCreatedUnderTheReplicationHint()
    {
        var (host, ops, alice) = GroupSetup();
        host.Fake.Failures.Add("GET", GroupSchedules, PimGroupForbidden, times: -1);
        var document = host.Resources(GroupEligibility(ops, alice));

        Assert.Null(await GroupSchedulesLookup.FindAsync(host.Fake, GroupSchedules, _ => true, new GroupScheduleTuple(ops, alice, "member"), GraphApi.V1, RetryHint.Default, ProviderHost.Ct));
        Assert.Empty(await GroupSchedulesLookup.ReadGroupSchedulesAsync(host.Fake, GroupSchedules, _ => true, ops, GraphApi.V1, RetryHint.Default, ProviderHost.Ct));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var assign = Assert.Single(run.Writes);
        Assert.Equal("adminAssign", Check.Body(assign)["action"]!.GetValue<string>());
        Assert.Equal(RetryHint.ExpectReplicationLag, assign.Hint);
        Assert.Single(host.Directory.Objects(GroupSchedules));
    }

    [Fact]
    public async Task A403FromTheGroupScheduleList_IsAnEmptyOrphanListing_NotAnIncompleteReport()
    {
        var (host, ops, alice) = GroupSetup();
        host.Fake.Failures.Add("GET", GroupSchedules, PimGroupForbidden, times: -1);

        var whatIf = await host.WhatIfAsync(host.Resources(GroupEligibility(ops, alice)));

        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);
        Assert.Empty(whatIf.Plan.Orphans);
        Assert.DoesNotContain(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.OrphanReportIncomplete || d.Code == EngineDiagnosticCodes.PimGroupAccessDenied);
    }

    [Fact]
    public async Task ADirectoryRoleScheduleList_Answering403_StaysAnError()
    {
        var (host, alice) = RoleSetup();
        host.Fake.Failures.Add("GET", RoleSchedules, PimGroupForbidden, times: -1);

        await Assert.ThrowsAsync<GraphException>(() => RoleSchedulesLookup.FindAsync(host.Fake, RoleSchedules, _ => true, new RoleScheduleTuple(alice, BuiltInRoles.GlobalReader, "/", IsAppScope: false), GraphApi.V1, RetryHint.Default, ProviderHost.Ct));
    }

    [Fact]
    public void TheAccessDeniedMessage_SaysWhetherTheClientRepeatedThe403()
    {
        var notRepeated = new GraphException(HttpStatusCode.Forbidden, "UnauthorizedAccessException", "Attempted to perform an unauthorized operation.", RetryHint.Default);
        var repeated = new GraphException(HttpStatusCode.Forbidden, "UnauthorizedAccessException", "Attempted to perform an unauthorized operation.", RetryHint.ExpectReplicationLag) { IsReplication = true };

        string once = GroupSchedulesLookup.AccessDenied(notRepeated, "x", null).Message;
        string retried = GroupSchedulesLookup.AccessDenied(repeated, "x", null).Message;

        Assert.Contains("EARM did not repeat this request", once, StringComparison.Ordinal);
        Assert.DoesNotContain("kept refusing", once, StringComparison.Ordinal);
        Assert.Contains("EARM repeated the request on the replication schedule", retried, StringComparison.Ordinal);
        Assert.Contains("most likely a missing role", retried, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A403FromTheGroupRequest_IsPimGroupAccessDenied()
    {
        var (host, ops, alice) = GroupSetup();
        host.Fake.Failures.Forbid(GroupRequests, "POST");

        var run = await host.CreateAsync(host.Resources(GroupEligibility(ops, alice)));

        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.PimGroupAccessDenied, run.Op(Name).ErrorCode);
        Assert.Contains("Groups Administrator, Identity Governance Administrator, User Administrator or Directory Writers", run.Op(Name).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A403UnauthorizedAccessExceptionThatPersists_IsPimGroupAccessDenied_MentioningAJustCreatedGroup()
    {
        var (host, ops, alice) = GroupSetup();
        host.Fake.Failures.Add("POST", GroupRequests, PimGroupForbidden, times: -1);

        var run = await host.CreateAsync(host.Resources(GroupEligibility(ops, alice)));

        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.PimGroupAccessDenied, run.Op(Name).ErrorCode);
        Assert.Contains("ACTIVE (not just eligible)", run.Op(Name).Error, StringComparison.Ordinal);
        Assert.Contains("if the group (or the principal) was just created, PIM may need more time", run.Op(Name).Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARemoveAnsweredActiveDurationTooShort_FailsWithActiveAssignmentTooNew_WithoutWaiting()
    {
        var (host, ops, alice) = GroupSetup();
        var document = host.Resources(GroupEligibility(ops, alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDuration\", \"duration\": \"P30D\" } }"));
        Check.Succeeded(await host.CreateAsync(document));
        host.Fake.Failures.Add("POST", GroupRequests, r => new GraphException(HttpStatusCode.BadRequest, "ActiveDurationTooShort", "The Active duration is too short. Miniumum Required is 5 minutes.", r.Hint), times: -1);

        var deletion = await host.DeleteDeclaredAsync(document);

        Assert.False(deletion.Succeeded);
        var outcome = Assert.Single(deletion.Outcomes);
        Assert.Equal(EngineDiagnosticCodes.ActiveAssignmentTooNew, outcome.ErrorCode);
        Assert.Contains("at least 5 minutes old", outcome.Error, StringComparison.Ordinal);
        Assert.Single(host.Directory.Objects(GroupSchedules));
        Assert.Single(host.Fake.Writes, w => w.Method == "POST" && w.Path.Contains(GroupRequests, StringComparison.Ordinal) && w.Body?["action"]?.GetValue<string>() == "adminRemove");
    }

    private static GraphException PimGroupForbidden(FakeGraphRequest request)
    {
        // The real client repeats this answer on the replication schedule first (HttpGraphClientResponseTests); the fake has no transport retries.
        return new GraphException(HttpStatusCode.Forbidden, "UnauthorizedAccessException", "Attempted to perform an unauthorized operation.", request.Hint);
    }

    [Fact]
    public async Task A403FromADirectoryRoleSchedule_StaysAGraphError()
    {
        var (host, alice) = RoleSetup();
        host.Fake.Failures.Forbid(FakeRoleScheduleRequests.EligibilityRequestsPath, "POST");

        var run = await host.CreateAsync(host.Resources(RoleEligibility(alice)));

        Assert.False(run.Succeeded);
        Assert.NotEqual(EngineDiagnosticCodes.PimGroupAccessDenied, run.Op(Name).ErrorCode);
    }

    // ---- 5: adminUpdate never sends a past start, and repeats RoleAssignmentExists ----

    [Fact]
    public async Task AnAdminUpdate_LeavesOutADeclaredPastStart_AndKeepsAFutureOne()
    {
        var (host, alice) = RoleSetup();
        SeedRoleSchedule(host, alice);
        var past = host.Resources(RoleEligibility(alice, ", \"scheduleInfo\": { \"startDateTime\": \"2024-03-01T00:00:00Z\", \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2031-01-01T00:00:00Z\" } }"));

        var run = await host.CreateAsync(past);

        Check.Succeeded(run);
        var body = Check.Body(Assert.Single(run.Writes));
        Assert.Equal("adminUpdate", body["action"]!.GetValue<string>());
        Assert.False(((JsonObject)body["scheduleInfo"]!).ContainsKey("startDateTime"));
        Assert.Equal("2031-01-01T00:00:00Z", body["scheduleInfo"]!["expiration"]!["endDateTime"]!.GetValue<string>());
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(past)).Change(Name).Type);

        string future = DateTimeOffset.UtcNow.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        var later = host.Resources(RoleEligibility(alice, ", \"scheduleInfo\": { \"startDateTime\": \"" + future + "\", \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2031-01-01T00:00:00Z\" } }"));
        var second = await host.CreateAsync(later);

        Check.Succeeded(second);
        Assert.Equal(future, Check.Body(Assert.Single(second.Writes))["scheduleInfo"]!["startDateTime"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnAdminUpdate_AnsweredRoleAssignmentExists_IsRepeatedOnTheSettleSchedule()
    {
        var (host, alice) = RoleSetup();
        SeedRoleSchedule(host, alice);
        host.Directory.RoleEligibility.ExistsOnUpdate = 2;
        var document = host.Resources(RoleEligibility(alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2031-01-01T00:00:00Z\" } }"));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Equal(3, run.Writes.Count);
        Assert.Equal(2, host.SettleDelays.Count);
        Assert.Equal("2031-01-01T00:00:00Z", Assert.Single(host.Directory.Objects(RoleSchedules))["scheduleInfo"]!["expiration"]!["endDateTime"]!.GetValue<string>());
    }

    [Fact]
    public async Task AGroupAdminUpdate_AnsweredRoleAssignmentExists_IsRepeatedToo()
    {
        var (host, ops, alice) = GroupSetup();
        var document = host.Resources(GroupEligibility(ops, alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2030-01-01T00:00:00Z\" } }"));
        Check.Succeeded(await host.CreateAsync(document));
        host.Directory.GroupEligibility.ExistsOnUpdate = 1;
        var changed = host.Resources(GroupEligibility(ops, alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2031-01-01T00:00:00Z\" } }"));

        var run = await host.CreateAsync(changed);

        Check.Succeeded(run);
        Assert.Equal(2, run.Writes.Count);
        Assert.Equal(1, host.SettleDelays.Count);
    }

    [Fact]
    public async Task AnAdminUpdate_ThatKeepsAnsweringRoleAssignmentExists_FailsAfterTheBoundedBackoff()
    {
        var (host, alice) = RoleSetup();
        SeedRoleSchedule(host, alice);
        host.Directory.RoleEligibility.ExistsOnUpdate = 100;
        var document = host.Resources(RoleEligibility(alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2031-01-01T00:00:00Z\" } }"));

        var run = await host.CreateAsync(document);

        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, run.Op(Name).ErrorCode);
        Assert.Contains("RoleAssignmentExists", run.Op(Name).Error, StringComparison.Ordinal);
        // Default settle schedule: 5 + 10 + 20 + 40 s of waits (the next, 80 s, would pass the 90 s window), so five requests.
        Assert.Equal(4, host.SettleDelays.Count);
        Assert.Equal(5, run.Writes.Count);
    }

    // ---- 6: the read after an authentication method PATCH waits for the declared values ----

    [Fact]
    public async Task AnAuthenticationMethodUpdate_ReadsAgainUntilTheDeclaredValuesShow()
    {
        var host = new ProviderHost();
        host.Directory.AuthenticationMethodStaleReads = 2;
        var document = host.Resources(Templates.Resource(AuthenticationMethodConfigurationsProvider.ResourceType, "sms", "{ \"state\": \"enabled\" }"));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Equal(2, host.SettleDelays.Count);
        var patchIndex = run.Requests.ToList().FindIndex(r => r.Method == "PATCH");
        Assert.Equal(3, run.Requests.Skip(patchIndex + 1).Count(r => r.Method == "GET" && r.Path.EndsWith("/Sms", StringComparison.Ordinal)));
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change("/sms").Type);
    }

    [Fact]
    public async Task AnAuthenticationMethodUpdate_ThatNeverShows_TakesTheLastReadAfterTheWindow()
    {
        var host = new ProviderHost();
        host.Directory.AuthenticationMethodStaleReads = 1000;
        var document = host.Resources(Templates.Resource(AuthenticationMethodConfigurationsProvider.ResourceType, "sms", "{ \"state\": \"enabled\" }"));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        // 90 s window / 5 s interval.
        Assert.Equal(18, host.SettleDelays.Count);
    }
}
