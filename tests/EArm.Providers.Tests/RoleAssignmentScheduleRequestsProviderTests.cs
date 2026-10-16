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
using static EArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>Microsoft.Entra/roleAssignmentScheduleRequests</c>: time-bound ACTIVE directory role assignments through PIM, ARM-styled. Same lifecycle as the
/// eligibility type (adminAssign / adminUpdate / adminRemove, 1 s end tolerance, metadata never compared) against roleAssignmentSchedules, keeping
/// assignmentType Assigned; a permanent schedule under the tuple (the roleAssignments API's) is refused at plan time, on adoption and on delete and is not
/// listed as an orphan; the same tuple declared by roleAssignments or a principal's directoryRoles is a plan-time error (PimAssignmentConflict).
/// </summary>
public sealed class RoleAssignmentScheduleRequestsProviderTests
{
    private const string Type = RoleAssignmentScheduleRequestsProvider.ResourceType;
    private const string Name = "8c3d1e2f-4a5b-4c6d-9e0f-1a2b3c4d5e6f";
    private const string Schedules = FakeRoleScheduleRequests.AssignmentSchedulesPath;
    private const string Requests = FakeRoleScheduleRequests.AssignmentRequestsPath;
    private const string Until2030 = ", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"2030-01-01T00:00:00Z\" } }";

    private static (ProviderHost Host, string AliceId) Setup()
    {
        var host = new ProviderHost();
        BuiltInRoles.Seed(host);
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        return (host, alice);
    }

    private static string Active(string properties, string name = Name)
    {
        return Templates.Resource(Type, name, properties);
    }

    private static string Props(string principalId, string extra = Until2030, string role = "\"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\"")
    {
        return "{ \"principalId\": \"" + principalId + "\", " + role + extra + " }";
    }

    private static JsonObject SeedSchedule(ProviderHost host, string principalId, string roleDefinitionId, string type = "afterDateTime", string? end = "2030-01-01T00:00:00Z", string assignmentType = "Assigned", string memberType = "Direct")
    {
        return host.Directory.Add(Schedules, new JsonObject
        {
            ["principalId"] = principalId,
            ["roleDefinitionId"] = roleDefinitionId,
            ["directoryScopeId"] = "/",
            ["appScopeId"] = null,
            ["status"] = "Provisioned",
            ["assignmentType"] = assignmentType,
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

    // ---- create / compare / update ----

    [Fact]
    public async Task ANewActiveAssignment_IsCreatedWithAdminAssign_AgainstTheAssignmentEndpoints_AndTheRerunIsNoChange()
    {
        var (host, alice) = Setup();
        var document = host.Resources(Active(Props(alice)));

        Assert.Equal(ChangeType.Create, (await host.WhatIfAsync(document)).Change(Name).Type);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(SingleRequest(run));
        Assert.Equal("adminAssign", body["action"]!.GetValue<string>());
        Assert.Equal(alice, body["principalId"]!.GetValue<string>());
        Assert.Equal("/", body["directoryScopeId"]!.GetValue<string>());
        Assert.Equal("afterDateTime", body["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        Assert.Equal("Deployed by earm", body["justification"]!.GetValue<string>());
        var schedule = Assert.Single(host.Directory.Objects(Schedules));
        Assert.Equal("Assigned", schedule["assignmentType"]!.GetValue<string>());
        Assert.Equal(schedule["id"]!.GetValue<string>(), run.GraphIdOf("/providers/" + Type + "/" + Name));
        Assert.Empty(host.Directory.Objects(FakeRoleScheduleRequests.EligibilitySchedulesPath));

        var rerun = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.NoChange, rerun.Change(Name).Type);
        Assert.Contains(rerun.Requests, r => r.Path == Schedules && r.UrlParameters!["$filter"].Contains("principalId eq '" + alice + "'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArmShapedProperties_AndTheSugar_AreAcceptedAndNormalised()
    {
        var (host, alice) = Setup();
        string props = Props(alice, ", \"requestType\": \"AdminAssign\", \"justification\": \"surge\", \"ticketInfo\": { \"ticketNumber\": \"CHG-9\" }, \"scheduleInfo\": { \"expiration\": { \"type\": \"afterduration\", \"duration\": \"P30D\" } }", role: "\"role\": \"Global Reader\"");
        var document = host.Resources(Active(props));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(SingleRequest(run));
        Assert.Equal(BuiltInRoles.GlobalReader, body["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal("afterDuration", body["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        Assert.Equal("surge", body["justification"]!.GetValue<string>());
        Assert.False(body.ContainsKey("requestType"));
        Assert.False(body.ContainsKey("role"));
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(Name).Type);
    }

    [Fact]
    public async Task ActivationsAndInheritedSchedules_AreNotTheDeclaredAssignment()
    {
        var (host, alice) = Setup();
        SeedSchedule(host, alice, BuiltInRoles.GlobalReader, assignmentType: "Activated");
        SeedSchedule(host, alice, BuiltInRoles.GlobalReader, memberType: "Group");

        var run = await host.WhatIfAsync(host.Resources(Active(Props(alice))));

        Assert.Equal(ChangeType.Create, run.Change(Name).Type);
    }

    [Fact]
    public async Task ALaterEnd_IsAModify_AppliedInPlaceWithAdminUpdate()
    {
        var (host, alice) = Setup();
        var seeded = SeedSchedule(host, alice, BuiltInRoles.GlobalReader);
        var document = host.Resources(Active(Props(alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2031-01-01T00:00:00Z\" } }")));

        var change = (await host.WhatIfAsync(document)).Change(Name);
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.Equal(ChangeMode.Patch, change.Mode);
        Assert.Contains(change.Reasons, r => r.Contains("active assignment is changed in place with a PIM adminUpdate", StringComparison.Ordinal));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Equal("adminUpdate", Check.Body(SingleRequest(run))["action"]!.GetValue<string>());
        var live = Assert.Single(host.Directory.Objects(Schedules));
        Assert.Equal(seeded["id"]!.GetValue<string>(), live["id"]!.GetValue<string>());
        Assert.Equal("2031-01-01T00:00:00Z", live["scheduleInfo"]!["expiration"]!["endDateTime"]!.GetValue<string>());
    }

    // ---- orphans, delete ----

    [Fact]
    public async Task AChangedRole_IsANewAssignment_TheOldTimeBoundOneIsRetained_PermanentOnesAreNotListed()
    {
        var (host, alice) = Setup();
        var old = SeedSchedule(host, alice, BuiltInRoles.SecurityReader);
        SeedSchedule(host, alice, BuiltInRoles.DirectoryReaders, type: "noExpiration", end: null);

        var whatIf = await host.WhatIfAsync(host.Resources(Active(Props(alice))));

        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);
        var retained = Assert.Single(whatIf.Plan.Orphans);
        Assert.Equal(OrphanReason.Retained, retained.Orphan);
        Assert.Equal(old["id"]!.GetValue<string>(), retained.GraphId);
        Assert.Contains(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.AssignmentRetained && d.Message.Contains("active:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeploymentDelete_RemovesTheAssignmentWithAdminRemove()
    {
        var (host, alice) = Setup();
        SeedSchedule(host, alice, BuiltInRoles.GlobalReader);
        host.ClearRequests();

        var result = await host.DeleteDeclaredAsync(host.Resources(Active(Props(alice))));

        Assert.True(result.Succeeded, string.Join("; ", result.Outcomes.Select(o => o.Error)));
        var remove = Check.Body(Assert.Single(host.Fake.Writes));
        Assert.Equal("adminRemove", remove["action"]!.GetValue<string>());
        Assert.Equal(Requests, Assert.Single(host.Fake.Writes).Path);
        Assert.Empty(host.Directory.Objects(Schedules));
    }

    // ---- a permanent assignment under the tuple ----

    [Fact]
    public async Task APermanentLiveAssignment_IsRefusedAtPlanTime_PointingAtRoleAssignments()
    {
        var (host, alice) = Setup();
        SeedSchedule(host, alice, BuiltInRoles.GlobalReader, type: "noExpiration", end: null);
        var document = host.Resources(Active(Props(alice)));

        var whatIf = await host.WhatIfAsync(document);
        var run = await host.CreateAsync(document);

        Assert.Contains("permanent active assignment", whatIf.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Entra/roleAssignments", whatIf.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Assert.False(run.Succeeded);
        Assert.DoesNotContain(run.Writes, w => w.Path == Requests);
        Assert.Equal("noExpiration", Assert.Single(host.Directory.Objects(Schedules))["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task APermanentAssignment_FoundOnlyWhenACreateIsAdopted_IsRefused_AndLeftUntouched()
    {
        // Hidden from the plan read only: adminAssign answers RoleAssignmentExists and the provider's re-read finds the permanent assignment.
        var (host, alice) = Setup();
        SeedSchedule(host, alice, BuiltInRoles.GlobalReader, type: "noExpiration", end: null);
        host.AbsenceCheck = new KeyAbsenceCheck { Delay = host.AbsenceDelays, ExtraReads = 0 };
        host.Fake.FlapLists(Schedules, false);

        var run = await host.CreateAsync(host.Resources(Active(Props(alice))));

        Assert.False(run.Succeeded);
        Assert.Contains("permanent active assignment", run.Op(Name).Error, StringComparison.Ordinal);
        Assert.Equal("adminAssign", Check.Body(SingleRequest(run))["action"]!.GetValue<string>());
        Assert.Equal("noExpiration", Assert.Single(host.Directory.Objects(Schedules))["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeploymentDelete_KeepsAPermanentAssignmentUnderTheTuple()
    {
        var (host, alice) = Setup();
        SeedSchedule(host, alice, BuiltInRoles.GlobalReader, type: "noExpiration", end: null);
        host.ClearRequests();

        var result = await host.DeleteDeclaredAsync(host.Resources(Active(Props(alice))));

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(DeletionStatus.Refused, outcome.Status);
        Assert.Contains("permanent active assignment", outcome.Error, StringComparison.Ordinal);
        Assert.True(result.Succeeded);
        Assert.Empty(host.Fake.Writes);
        Assert.Single(host.Directory.Objects(Schedules));
    }

    // ---- template conflicts ----

    [Theory]
    [InlineData("\"role\": \"Global Reader\"", "\"roleDefinitionId\": \"[roleTemplate('Global Reader')]\"")]
    [InlineData("\"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\"", "\"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\", \"directoryScopeId\": \"/\"")]
    public async Task TheSameTuple_InARoleAssignmentsResource_IsAPlanTimeConflict(string standaloneRole, string activeRole)
    {
        var (host, _) = Setup();
        var document = host.Resources(Join(Alice(), Standalone(AliceRef + ", " + standaloneRole), Active("{ " + AliceRef + ", " + activeRole + Until2030 + " }")));

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(document));

        var conflict = Assert.Single(error.Errors);
        Assert.Equal(EngineDiagnosticCodes.PimAssignmentConflict, conflict.Code);
        Assert.Contains("declare the same directory role assignment", conflict.Message, StringComparison.Ordinal);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task TheSameRole_InThePrincipalsDirectoryRoles_IsAPlanTimeConflict()
    {
        var (host, _) = Setup();
        var document = host.Resources(Join(Alice(DirectoryRoles("{ \"role\": \"Global Reader\" }")), Active("{ " + AliceRef + ", \"role\": \"Global Reader\"" + Until2030 + " }")));

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => host.CreateAsync(document));

        var conflict = Assert.Single(error.Errors);
        Assert.Equal(EngineDiagnosticCodes.PimAssignmentConflict, conflict.Code);
        Assert.Contains("directoryRoles[0]", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADifferentRoleOrScope_IsNoConflict()
    {
        var (host, _) = Setup();
        string unitScope = ", \"directoryScopeId\": \"/administrativeUnits/1b2c3d4e-0000-4000-8000-000000000999\"";
        var document = host.Resources(Join(
            Alice(DirectoryRoles("{ \"role\": \"Security Reader\" }")),
            Standalone(AliceRef + ", \"role\": \"Global Reader\"" + unitScope),
            Active("{ " + AliceRef + ", \"role\": \"Global Reader\"" + Until2030 + " }")));

        var run = await host.WhatIfAsync(document);

        Assert.Equal(ChangeType.Create, run.Change(Name).Type);
    }

    // ---- computed values ----

    [Fact]
    public async Task AComputedNoExpiration_IsAnUnsupportedRow()
    {
        var (host, alice) = Setup();

        var run = await host.WhatIfAsync(host.Resources(Active(Props(alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"[concat('No', 'Expiration')]\" } }"))));

        Assert.Contains("NoExpiration is not allowed", run.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Check.NoWrites(run);
    }
}
