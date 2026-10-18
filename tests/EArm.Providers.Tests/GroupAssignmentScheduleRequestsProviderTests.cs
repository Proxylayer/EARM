using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests</c>: time-bound ACTIVE group membership or ownership through PIM for Groups. Same
/// lifecycle as the group eligibility type against assignmentSchedules (the filter required), keeping assignmentType assigned; a permanent schedule under
/// the tuple is refused at plan time and on delete and is not listed as an orphan; a target group that declares an authoritative members (owners) list,
/// or a principal whose memberOf (ownerOf) names the group, is a plan-time error (PimAssignmentConflict), whichever way the group is referenced.
/// </summary>
public sealed class GroupAssignmentScheduleRequestsProviderTests
{
    private const string Type = GroupAssignmentScheduleRequestsProvider.ResourceType;
    private const string Name = "9d4e2f3a-5b6c-4d7e-8f90-a1b2c3d4e5f6";
    private const string Schedules = FakeGroupScheduleRequests.AssignmentSchedulesPath;
    private const string Requests = FakeGroupScheduleRequests.AssignmentRequestsPath;
    private const string SevenDays = ", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P7D\" } }";
    private const string Until2030 = ", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2030-01-01T00:00:00Z\" } }";

    private static (ProviderHost Host, string OpsId, string AliceId) Setup()
    {
        var host = new ProviderHost();
        string ops = host.Directory.AddGroup("pim-ops", "PIM Ops")["id"]!.GetValue<string>();
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        return (host, ops, alice);
    }

    private static string Active(string properties, string name = Name)
    {
        return Templates.Resource(Type, name, properties);
    }

    private static string Props(string groupId, string principalId, string extra = Until2030, string access = "member")
    {
        return "{ \"groupId\": \"" + groupId + "\", \"principalId\": \"" + principalId + "\", \"accessId\": \"" + access + "\"" + extra + " }";
    }

    private static string OpsGroup(string? lists = null)
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"pim-ops\", \"properties\": { \"displayName\": \"PIM Ops\", \"securityEnabled\": true, \"mailEnabled\": false, \"mailNickname\": \"pim-ops\"" + (lists is null ? string.Empty : ", " + lists) + " } }";
    }

    private static string Alice(string? edges = null)
    {
        return DirectoryTemplates.Alice(edges);
    }

    private static JsonObject SeedSchedule(ProviderHost host, string groupId, string principalId, string accessId = "member", string type = "afterDateTime", string? end = "2030-01-01T00:00:00Z", string assignmentType = "assigned")
    {
        return host.Directory.Add(Schedules, new JsonObject
        {
            ["id"] = groupId + "_" + accessId + "_" + Guid.NewGuid().ToString("D"),
            ["groupId"] = groupId,
            ["principalId"] = principalId,
            ["accessId"] = accessId,
            ["status"] = "Provisioned",
            ["memberType"] = "direct",
            ["assignmentType"] = assignmentType,
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

    private static async Task<EArm.Core.Diagnostics.Diagnostic> Conflict(ProviderHost host, string resources)
    {
        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources(resources)));
        var conflict = Assert.Single(error.Errors);
        Assert.Equal(EngineDiagnosticCodes.PimAssignmentConflict, conflict.Code);
        Assert.Empty(host.Fake.Writes);
        return conflict;
    }

    // ---- lifecycle ----

    [Fact]
    public async Task ANewActiveMembership_IsCreatedWithAdminAssign_OnboardsTheGroup_AndTheRerunIsNoChange()
    {
        var (host, ops, alice) = Setup();
        var document = host.Resources(Active(Props(ops, alice, SevenDays)));

        Assert.Equal(ChangeType.Create, (await host.WhatIfAsync(document)).Change(Name).Type);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(SingleRequest(run));
        Assert.Equal("adminAssign", body["action"]!.GetValue<string>());
        Assert.Equal("member", body["accessId"]!.GetValue<string>());
        Assert.Equal("afterDuration", body["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        var schedule = Assert.Single(host.Directory.Objects(Schedules));
        Assert.Equal("assigned", schedule["assignmentType"]!.GetValue<string>());
        Assert.Contains(ops, host.Directory.GroupAssignmentSchedules.OnboardedGroups);
        Assert.Empty(host.Directory.Objects(FakeGroupScheduleRequests.EligibilitySchedulesPath));

        var rerun = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.NoChange, rerun.Change(Name).Type);
        Assert.All(rerun.Requests.Where(r => r.Path == Schedules), r => Assert.Contains("groupId eq '" + ops + "'", r.UrlParameters!["$filter"], StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheGroupSugar_AndAnOwnerAssignment_AreResolvedAndNormalised()
    {
        var (host, ops, alice) = Setup();
        var document = host.Resources(Active("{ \"group\": \"pim-ops\", \"principalId\": \"" + alice + "\", \"accessId\": \"Owner\", \"requestType\": \"adminassign\"" + Until2030 + " }"));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(SingleRequest(run));
        Assert.Equal(ops, body["groupId"]!.GetValue<string>());
        Assert.Equal("owner", body["accessId"]!.GetValue<string>());
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(Name).Type);
    }

    [Fact]
    public async Task ActivationsAndTheOtherAccessId_AreNotTheDeclaredAssignment()
    {
        var (host, ops, alice) = Setup();
        SeedSchedule(host, ops, alice, assignmentType: "activated");
        SeedSchedule(host, ops, alice, accessId: "owner");

        var run = await host.WhatIfAsync(host.Resources(Active(Props(ops, alice))));

        Assert.Equal(ChangeType.Create, run.Change(Name).Type);
    }

    [Fact]
    public async Task ALaterEnd_IsAModify_AppliedInPlaceWithAdminUpdate()
    {
        var (host, ops, alice) = Setup();
        SeedSchedule(host, ops, alice);
        var document = host.Resources(Active(Props(ops, alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2031-01-01T00:00:00Z\" } }")));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Equal("adminUpdate", Check.Body(SingleRequest(run))["action"]!.GetValue<string>());
        Assert.Equal("2031-01-01T00:00:00Z", Assert.Single(host.Directory.Objects(Schedules))["scheduleInfo"]!["expiration"]!["endDateTime"]!.GetValue<string>());
    }

    private static EArm.Graph.GraphException LastAdmin(FakeGraphRequest request)
    {
        // What a real tenant answered to the adminUpdate of a group's only owner assignment.
        return new EArm.Graph.GraphException(System.Net.HttpStatusCode.BadRequest, "CannotDeleteLastAdminAssignment", "Cannot delete the last admin assignment.", request.Hint);
    }

    [Fact]
    public async Task AnAdminUpdateOfTheOnlyOwnerAssignment_AnsweredCannotDeleteLastAdminAssignment_IsPimLastOwnerAssignment()
    {
        var (host, ops, alice) = Setup();
        SeedSchedule(host, ops, alice, accessId: "owner");
        host.Fake.Failures.Add("POST", Requests, LastAdmin, times: -1);
        var document = host.Resources(Active(Props(ops, alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2031-01-01T00:00:00Z\" } }", access: "owner")));

        var run = await host.CreateAsync(document);

        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.PimLastOwnerAssignment, run.Op(Name).ErrorCode);
        Assert.Contains("only owner assignment of a group", run.Op(Name).Error, StringComparison.Ordinal);
        Assert.Contains("add another owner", run.Op(Name).Error, StringComparison.Ordinal);
        Assert.Contains("adminUpdate", run.Op(Name).Error, StringComparison.Ordinal);
        Assert.Equal("2030-01-01T00:00:00Z", Assert.Single(host.Directory.Objects(Schedules))["scheduleInfo"]!["expiration"]!["endDateTime"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnAdminRemoveOfTheOnlyOwnerAssignment_AnsweredCannotDeleteLastAdminAssignment_IsPimLastOwnerAssignment()
    {
        var (host, ops, alice) = Setup();
        var document = host.Resources(Active(Props(ops, alice, access: "owner")));
        Check.Succeeded(await host.CreateAsync(document));
        host.Fake.Failures.Add("POST", Requests, LastAdmin, times: -1);

        var deletion = await host.DeleteDeclaredAsync(document);

        Assert.False(deletion.Succeeded);
        var outcome = Assert.Single(deletion.Outcomes);
        Assert.Equal(EngineDiagnosticCodes.PimLastOwnerAssignment, outcome.ErrorCode);
        Assert.Contains("adminRemove", outcome.Error, StringComparison.Ordinal);
        Assert.Single(host.Directory.Objects(Schedules));
    }

    [Fact]
    public async Task UndeclaredTimeBoundAssignmentsOfADeclaredTargetGroup_AreRetained_PermanentMembershipsAreNotListed()
    {
        var (host, ops, alice) = Setup();
        string bob = host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        string carol = host.Directory.AddUser("carol@contoso.com", "Carol")["id"]!.GetValue<string>();
        SeedSchedule(host, ops, alice);
        var bobs = SeedSchedule(host, ops, bob);
        SeedSchedule(host, ops, carol, type: "noExpiration", end: null);

        var whatIf = await host.WhatIfAsync(host.Resources(Active(Props(ops, alice))));

        Assert.Equal(ChangeType.NoChange, whatIf.Change(Name).Type);
        var retained = Assert.Single(whatIf.Plan.Orphans);
        Assert.Equal(bobs["id"]!.GetValue<string>(), retained.GraphId);
        Assert.Contains(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.AssignmentRetained && d.Message.Contains("active member", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeploymentDelete_RemovesTheAssignmentWithAdminRemove_CarryingTheLiveSchedule()
    {
        var (host, ops, alice) = Setup();
        SeedSchedule(host, ops, alice);
        host.ClearRequests();

        var result = await host.DeleteDeclaredAsync(host.Resources(Active(Props(ops, alice))));

        Assert.True(result.Succeeded, string.Join("; ", result.Outcomes.Select(o => o.Error)));
        var write = Assert.Single(host.Fake.Writes);
        Assert.Equal(Requests, write.Path);
        var remove = Check.Body(write);
        Assert.Equal("adminRemove", remove["action"]!.GetValue<string>());
        Assert.Equal("afterDateTime", remove["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        Assert.Empty(host.Directory.Objects(Schedules));
    }

    // ---- a permanent membership under the tuple ----

    [Fact]
    public async Task APermanentMembership_IsRefusedAtPlanTime_AndKeptOnDelete()
    {
        var (host, ops, alice) = Setup();
        SeedSchedule(host, ops, alice, type: "noExpiration", end: null);
        var document = host.Resources(Active(Props(ops, alice)));

        var whatIf = await host.WhatIfAsync(document);
        var deleted = await host.DeleteDeclaredAsync(document);

        Assert.Contains("already a permanent member of the group", whatIf.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("members", whatIf.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Assert.Equal(DeletionStatus.Refused, Assert.Single(deleted.Outcomes).Status);
        Assert.Single(host.Directory.Objects(Schedules));
        Assert.DoesNotContain(host.Fake.Writes, w => w.Path == Requests);
    }

    // ---- template conflicts: a pruning members / owners list ----

    [Theory]
    [InlineData("\"groupId\": \"[reference('pim-ops').id]\"")]
    [InlineData("\"groupId\": \"[reference(resourceId('Microsoft.Entra/groups', 'pim-ops')).id]\"")]
    [InlineData("\"group\": \"pim-ops\"")]
    public async Task AnActiveMembership_InAGroupThatDeclaresMembers_IsAPlanTimeConflict(string group)
    {
        var (host, _, alice) = Setup();

        var conflict = await Conflict(host, DirectoryTemplates.Join(OpsGroup("\"members\": [\"" + alice + "\"]"), Active("{ " + group + ", \"principalId\": \"" + alice + "\", \"accessId\": \"member\"" + Until2030 + " }")));

        Assert.Contains("authoritative members list", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("edge pass would remove it", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnActiveOwnership_InAGroupThatDeclaresOwners_IsAPlanTimeConflict_ButMembersDoNotMatter()
    {
        var (host, _, alice) = Setup();
        string owner = Active("{ \"groupId\": \"[reference('pim-ops').id]\", \"principalId\": \"" + alice + "\", \"accessId\": \"owner\"" + Until2030 + " }");

        var conflict = await Conflict(host, DirectoryTemplates.Join(OpsGroup("\"owners\": [\"33333333-0000-0000-0000-000000000001\"]"), owner));
        var fine = await host.WhatIfAsync(host.Resources(DirectoryTemplates.Join(OpsGroup("\"members\": []"), owner)));

        Assert.Contains("authoritative owners list", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(ChangeType.Create, fine.Change(Name).Type);
    }

    [Fact]
    public async Task AComputedAccessId_ConflictsWithEitherList()
    {
        var (host, _, alice) = Setup();

        var conflict = await Conflict(host, DirectoryTemplates.Join(OpsGroup("\"owners\": [\"33333333-0000-0000-0000-000000000001\"]"), Active("{ \"groupId\": \"[reference('pim-ops').id]\", \"principalId\": \"" + alice + "\", \"accessId\": \"[toLower('MEMBER')]\"" + Until2030 + " }")));

        Assert.Contains("owners", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePrincipalsMemberOf_NamingTheGroup_IsAPlanTimeConflict()
    {
        var (host, _, _) = Setup();
        string resources = DirectoryTemplates.Join(
            OpsGroup(),
            Alice("\"memberOf\": [\"[reference('pim-ops').id]\"]"),
            Active("{ \"groupId\": \"[reference('pim-ops').id]\", \"principalId\": \"[reference('alice@contoso.com').id]\", \"accessId\": \"member\"" + Until2030 + " }"));

        var conflict = await Conflict(host, resources);

        Assert.Contains("memberOf", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("permanently", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGroupWithoutMembersOrOwners_AndAnUndeclaredGroup_AreNoConflict()
    {
        var (host, ops, alice) = Setup();
        string other = host.Directory.AddGroup("other", "Other")["id"]!.GetValue<string>();
        string resources = DirectoryTemplates.Join(
            OpsGroup(),
            "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"other\", \"properties\": { \"displayName\": \"Other\", \"securityEnabled\": true, \"mailEnabled\": false, \"mailNickname\": \"other\", \"members\": [] } }",
            Active("{ \"groupId\": \"[reference('pim-ops').id]\", \"principalId\": \"" + alice + "\", \"accessId\": \"member\"" + Until2030 + " }"));

        var run = await host.WhatIfAsync(host.Resources(resources));

        Assert.Equal(ChangeType.Create, run.Change(Name).Type);
        Assert.NotEqual(ops, other);
    }

    // ---- computed values ----

    [Fact]
    public async Task AComputedNoExpiration_IsAnUnsupportedRow()
    {
        var (host, ops, alice) = Setup();

        var run = await host.WhatIfAsync(host.Resources(Active(Props(ops, alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"[concat('No', 'Expiration')]\" } }"))));

        Assert.Contains("NoExpiration is not allowed", run.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Check.NoWrites(run);
    }
}
