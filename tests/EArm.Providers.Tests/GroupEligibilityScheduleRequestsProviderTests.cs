using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests</c>: PIM for Groups eligibility. Created with adminAssign (onboarding the group),
/// compared on the expiration type and endDateTime (1 s tolerance, a past start never drift, AfterDuration create-time intent), changed in place with
/// adminUpdate, removed with adminRemove (carrying the live schedule); justification / ticketInfo never compared; the group sugar resolved by uniqueName;
/// dynamic and on-premises synced groups and principals that are not users or groups refused in Prepare; a changed tuple and undeclared eligibilities of a
/// declared target group reported retained; RoleAssignmentExists adopted; a policy refusal reported as PimPolicyViolation with the group-policy hint.
/// </summary>
public sealed class GroupEligibilityScheduleRequestsProviderTests
{
    private const string Type = GroupEligibilityScheduleRequestsProvider.ResourceType;
    private const string Name = "7a2c9b3f-4d5e-4f60-9b0c-1d2e3f4a5b6c";
    private const string Schedules = FakeGroupEligibility.SchedulesPath;
    private const string Requests = FakeGroupEligibility.RequestsPath;

    private static (ProviderHost Host, string OpsId, string AliceId) Setup(Action<JsonObject>? configureOps = null)
    {
        var host = new ProviderHost();
        string ops = host.Directory.AddGroup("pim-ops", "PIM Ops", configureOps)["id"]!.GetValue<string>();
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        return (host, ops, alice);
    }

    private static string Eligibility(string properties, string name = Name)
    {
        return Templates.Resource(Type, name, properties);
    }

    private static string Props(string groupId, string principalId, string extra = "", string access = "member")
    {
        return "{ \"groupId\": \"" + groupId + "\", \"principalId\": \"" + principalId + "\", \"accessId\": \"" + access + "\"" + extra + " }";
    }

    private static JsonObject SeedSchedule(ProviderHost host, string groupId, string principalId, string accessId = "member", string type = "afterDateTime", string? end = "2030-01-01T00:00:00Z", string memberType = "direct")
    {
        return host.Directory.Add(Schedules, new JsonObject
        {
            ["id"] = groupId + "_" + accessId + "_" + Guid.NewGuid().ToString("D"),
            ["groupId"] = groupId,
            ["principalId"] = principalId,
            ["accessId"] = accessId,
            ["status"] = "Provisioned",
            ["memberType"] = memberType,
            ["scheduleInfo"] = new JsonObject
            {
                ["startDateTime"] = "2025-01-01T10:00:00.123Z",
                ["recurrence"] = null,
                ["expiration"] = new JsonObject { ["type"] = type, ["endDateTime"] = end, ["duration"] = null },
            },
        });
    }

    private static FakeGraphRequest SingleRequest(Run run)
    {
        var write = Assert.Single(run.Writes);
        Assert.Equal("POST", write.Method);
        Assert.Equal(Requests, write.Path);
        return write;
    }

    // ---- create ----

    [Fact]
    public async Task ANewEligibility_IsCreatedWithAdminAssign_PermanentByDefault_OnboardsTheGroup_AndTheRerunIsNoChange()
    {
        var (host, ops, alice) = Setup();
        var document = host.Resources(Eligibility(Props(ops, alice)));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(SingleRequest(run));
        Assert.Equal("adminAssign", body["action"]!.GetValue<string>());
        Assert.Equal("member", body["accessId"]!.GetValue<string>());
        Assert.Equal(ops, body["groupId"]!.GetValue<string>());
        Assert.Equal(alice, body["principalId"]!.GetValue<string>());
        Assert.Equal("noExpiration", body["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        Assert.Equal("Deployed by earm", body["justification"]!.GetValue<string>());
        var schedule = Assert.Single(host.Directory.Objects(Schedules));
        Assert.Equal(schedule["id"]!.GetValue<string>(), run.GraphIdOf("/providers/" + Type + "/" + Name));
        Assert.Contains(ops, host.Directory.GroupEligibility.OnboardedGroups);

        var rerun = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.NoChange, rerun.Change(Name).Type);
        Assert.All(rerun.Requests.Where(r => r.Path == Schedules), r => Assert.Contains("groupId eq '" + ops + "'", r.UrlParameters!["$filter"], StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheGroupSugar_IsResolvedByUniqueName_AndTheAccessIdAndRequestTypeAreNormalised()
    {
        var (host, ops, alice) = Setup();
        string props = "{ \"group\": \"pim-ops\", \"principalId\": \"" + alice + "\", \"accessId\": \"Owner\", \"requestType\": \"adminassign\", \"justification\": \"JIT owners\", "
            + "\"ticketInfo\": { \"ticketNumber\": \"CHG-1\", \"ticketSystem\": \"SNOW\" }, \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"2030-06-30T00:00:00Z\" } } }";
        var document = host.Resources(Eligibility(props));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(SingleRequest(run));
        Assert.Equal(ops, body["groupId"]!.GetValue<string>());
        Assert.Equal("owner", body["accessId"]!.GetValue<string>());
        Assert.Equal("afterDateTime", body["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        Assert.Equal("JIT owners", body["justification"]!.GetValue<string>());
        Assert.Equal("CHG-1", body["ticketInfo"]!["ticketNumber"]!.GetValue<string>());
        Assert.False(body.ContainsKey("group"));
        Assert.False(body.ContainsKey("requestType"));
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(Name).Type);
    }

    [Fact]
    public async Task AGroupPrincipal_CanBeMadeEligible()
    {
        var (host, ops, _) = Setup();
        string admins = host.Directory.AddGroup("pim-admins", "PIM Admins")["id"]!.GetValue<string>();

        var run = await host.CreateAsync(host.Resources(Eligibility(Props(ops, admins))));

        Check.Succeeded(run);
    }

    // ---- compare ----

    [Fact]
    public async Task TheSameEnd_WithinOneSecond_APastStart_AndDifferentMetadata_IsNoChange()
    {
        var (host, ops, alice) = Setup();
        SeedSchedule(host, ops, alice, end: "2030-01-01T00:00:00.400Z");
        string props = Props(ops, alice, ", \"justification\": \"another reason\", \"ticketInfo\": { \"ticketNumber\": \"X\" }, \"scheduleInfo\": { \"startDateTime\": \"2024-03-01T00:00:00Z\", \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2030-01-01T00:00:00Z\" } }");

        var run = await host.WhatIfAsync(host.Resources(Eligibility(props)));

        var change = run.Change(Name);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.DoesNotContain(change.Deltas, d => d.HasEffect);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task AfterDuration_IsCreateTimeIntent()
    {
        var (host, ops, alice) = Setup();
        SeedSchedule(host, ops, alice, end: "2026-12-31T00:00:00Z");

        var run = await host.WhatIfAsync(host.Resources(Eligibility(Props(ops, alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P365D\" } }"))));

        Assert.Equal(ChangeType.NoChange, run.Change(Name).Type);
    }

    [Fact]
    public async Task ALaterEnd_IsAModify_AppliedInPlaceWithAdminUpdate()
    {
        var (host, ops, alice) = Setup();
        var seeded = SeedSchedule(host, ops, alice);
        var document = host.Resources(Eligibility(Props(ops, alice, ", \"justification\": \"extend\", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2031-01-01T00:00:00Z\" } }")));

        var whatIf = await host.WhatIfAsync(document);
        var change = whatIf.Change(Name);
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.Equal(ChangeMode.Patch, change.Mode);
        Assert.Contains(change.Reasons, r => r.Contains("adminUpdate", StringComparison.Ordinal));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(SingleRequest(run));
        Assert.Equal("adminUpdate", body["action"]!.GetValue<string>());
        var live = Assert.Single(host.Directory.Objects(Schedules));
        Assert.Equal(seeded["id"]!.GetValue<string>(), live["id"]!.GetValue<string>());
        Assert.Equal("2031-01-01T00:00:00Z", live["scheduleInfo"]!["expiration"]!["endDateTime"]!.GetValue<string>());
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(Name).Type);
    }

    [Fact]
    public async Task TheOtherAccessId_AndInheritedSchedules_AreNotTheDeclaredEligibility()
    {
        var (host, ops, alice) = Setup();
        SeedSchedule(host, ops, alice, accessId: "owner", type: "noExpiration", end: null);
        SeedSchedule(host, ops, alice, type: "noExpiration", end: null, memberType: "group");

        var run = await host.WhatIfAsync(host.Resources(Eligibility(Props(ops, alice))));

        Assert.Equal(ChangeType.Create, run.Change(Name).Type);
    }

    // ---- tuple change, orphans, delete ----

    [Fact]
    public async Task AChangedAccessId_IsANewEligibility_AndTheOldOneIsReportedRetained()
    {
        var (host, ops, alice) = Setup();
        var old = SeedSchedule(host, ops, alice, accessId: "member", type: "noExpiration", end: null);
        var document = host.Resources(Eligibility(Props(ops, alice, access: "owner")));

        var whatIf = await host.WhatIfAsync(document);

        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);
        var retained = Assert.Single(whatIf.Plan.Orphans);
        Assert.Equal(OrphanReason.Retained, retained.Orphan);
        Assert.Equal(old["id"]!.GetValue<string>(), retained.GraphId);
        Assert.Contains(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.AssignmentRetained && d.Message.Contains("eligible member", StringComparison.Ordinal));

        var run = await host.CreateAsync(document);
        Check.Succeeded(run);
        Assert.Equal(2, host.Directory.Objects(Schedules).Count);
        Assert.DoesNotContain(run.Writes, w => Check.Body(w)["action"]!.GetValue<string>() == "adminRemove");
    }

    [Fact]
    public async Task UndeclaredEligibilitiesOfADeclaredTargetGroup_AreListedAsRetained_OthersAreNot()
    {
        var (host, ops, alice) = Setup();
        string bob = host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        string other = host.Directory.AddGroup("other", "Other")["id"]!.GetValue<string>();
        SeedSchedule(host, ops, alice);
        var bobs = SeedSchedule(host, ops, bob, type: "noExpiration", end: null);
        SeedSchedule(host, other, bob, type: "noExpiration", end: null);

        var whatIf = await host.WhatIfAsync(host.Resources(Eligibility(Props(ops, alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2030-01-01T00:00:00Z\" } }"))));

        Assert.Equal(ChangeType.NoChange, whatIf.Change(Name).Type);
        var retained = Assert.Single(whatIf.Plan.Orphans);
        Assert.Equal(OrphanReason.Retained, retained.Orphan);
        Assert.Equal(bobs["id"]!.GetValue<string>(), retained.GraphId);
        Assert.Contains(whatIf.Requests, r => r.Path == Schedules && r.UrlParameters!["$filter"] == "groupId eq '" + ops + "'");
    }

    [Fact]
    public async Task DeploymentDelete_RemovesTheEligibilityWithAdminRemove_CarryingTheLiveSchedule()
    {
        var (host, ops, alice) = Setup();
        SeedSchedule(host, ops, alice, type: "noExpiration", end: null);
        host.ClearRequests();

        var result = await host.DeleteDeclaredAsync(host.Resources(Eligibility(Props(ops, alice))));

        Assert.True(result.Succeeded, string.Join("; ", result.Outcomes.Select(o => o.Error)));
        var remove = Check.Body(Assert.Single(host.Fake.Writes));
        Assert.Equal("adminRemove", remove["action"]!.GetValue<string>());
        Assert.Equal("noExpiration", remove["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        Assert.Empty(host.Directory.Objects(Schedules));
    }

    // ---- Graph quirks ----

    [Fact]
    public async Task RoleAssignmentExists_OnAPlannedCreate_IsAdoptedAfterAReRead()
    {
        var (host, ops, alice) = Setup();
        var seeded = SeedSchedule(host, ops, alice, type: "noExpiration", end: null);
        host.AbsenceCheck = new KeyAbsenceCheck { Delay = host.AbsenceDelays, ExtraReads = 0 };
        host.Fake.FlapLists(Schedules, false, false);

        var run = await host.CreateAsync(host.Resources(Eligibility(Props(ops, alice))));

        Check.Succeeded(run);
        Assert.Equal("adminAssign", Check.Body(SingleRequest(run))["action"]!.GetValue<string>());
        Assert.Equal(seeded["id"]!.GetValue<string>(), run.GraphIdOf("/providers/" + Type + "/" + Name));
        Assert.Single(host.Directory.Objects(Schedules));
    }

    [Fact]
    public async Task AGroupPolicyThatForbidsPermanentEligibility_FailsWithPimPolicyViolation_PointingAtThePolicyOrAnExpiration()
    {
        var (host, ops, alice) = Setup();
        host.Directory.GroupEligibility.RequireExpiration = true;

        var run = await host.CreateAsync(host.Resources(Eligibility(Props(ops, alice))));

        Assert.False(run.Succeeded);
        var op = run.Op(Name);
        Assert.Equal(EngineDiagnosticCodes.PimPolicyViolation, op.ErrorCode);
        Assert.Contains("ExpirationRule", op.Error, StringComparison.Ordinal);
        Assert.Contains("the group's PIM policy", op.Error, StringComparison.Ordinal);
        Assert.Contains("roleManagementPolicies", op.Error, StringComparison.Ordinal);
        Assert.Contains("AfterDuration", op.Error, StringComparison.Ordinal);
        Assert.Empty(host.Directory.Objects(Schedules));
        Assert.Empty(host.Directory.GroupEligibility.OnboardedGroups);
    }

    [Fact]
    public async Task TheScheduleList_RequiresAGroupOrPrincipalFilter()
    {
        var host = new ProviderHost();

        var error = await Assert.ThrowsAsync<EArm.Graph.GraphException>(() => host.Fake.GetAsync(new EArm.Graph.GraphRequest(Schedules, EArm.Graph.GraphApi.V1), ProviderHost.Ct));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.Status);
    }

    // ---- refused declarations ----

    [Theory]
    [InlineData("DynamicMembership", false, "dynamic membership")]
    [InlineData(null, true, "synchronised from on-premises")]
    public async Task ADynamicOrSyncedGroup_IsRefusedBeforeAnyRequest(string? groupType, bool synced, string message)
    {
        var (host, ops, alice) = Setup(g =>
        {
            if (groupType is not null)
            {
                g["groupTypes"] = new JsonArray(groupType);
                g["membershipRule"] = "user.department -eq \"Ops\"";
            }

            g["onPremisesSyncEnabled"] = synced;
        });
        var document = host.Resources(Eligibility(Props(ops, alice)));

        var whatIf = await host.WhatIfAsync(document);
        var run = await host.CreateAsync(document);

        Assert.Contains(message, whatIf.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.GroupEligibilityInvalid, run.Op(Name).ErrorCode);
        Assert.DoesNotContain(run.Writes, w => w.Path == Requests);
    }

    [Fact]
    public async Task AServicePrincipal_AnUnknownGroup_AndTheGroupItself_AreRefused()
    {
        var (host, ops, alice) = Setup();
        string sp = host.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "Robot")["id"]!.GetValue<string>();

        var servicePrincipal = await host.WhatIfAsync(host.Resources(Eligibility(Props(ops, sp))));
        var missingGroup = await host.WhatIfAsync(host.Resources(Eligibility("{ \"group\": \"no-such-group\", \"principalId\": \"" + alice + "\", \"accessId\": \"member\" }")));
        var self = await host.WhatIfAsync(host.Resources(Eligibility(Props(ops, ops))));

        Assert.Contains("not a user or a group", servicePrincipal.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("no group with uniqueName or id 'no-such-group'", missingGroup.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("the target group itself", self.Change(Name).UnsupportedReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(", \"requestType\": \"[concat('Admin', 'Remove')]\"", "must be AdminAssign")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"[concat('After', 'DateTime')]\" } }", "endDateTime is required")]
    public async Task AComputedValueThatBreaksTheRules_IsAnUnsupportedRow(string extra, string message)
    {
        var (host, ops, alice) = Setup();

        var run = await host.WhatIfAsync(host.Resources(Eligibility(Props(ops, alice, extra))));

        Assert.Contains(message, run.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task AComputedAccessIdThatIsNotMemberOrOwner_IsAnUnsupportedRow()
    {
        var (host, ops, alice) = Setup();

        var run = await host.WhatIfAsync(host.Resources(Eligibility(Props(ops, alice, access: "[concat('Mem', 'bers')]"))));

        Assert.Contains("must be member or owner", run.Change(Name).UnsupportedReason, StringComparison.Ordinal);
    }
}
