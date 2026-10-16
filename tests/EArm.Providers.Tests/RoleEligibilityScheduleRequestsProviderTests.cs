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
/// <c>Microsoft.Entra/roleEligibilityScheduleRequests</c>: PIM eligibility for directory roles, ARM-styled. Created with adminAssign, compared on the expiration
/// type and endDateTime (1 s tolerance, a past start never drift, AfterDuration create-time intent), changed in place with adminUpdate, removed with adminRemove;
/// justification / ticketInfo never compared; a changed tuple leaves the old eligibility retained; RoleAssignmentExists adopted; PIM policy refusals reported as
/// PimPolicyViolation; a group principal must be role-assignable; asynchronous request processing waited out.
/// </summary>
public sealed class RoleEligibilityScheduleRequestsProviderTests
{
    private const string Type = RoleEligibilityScheduleRequestsProvider.ResourceType;
    private const string Name = "6f1b8a2e-3c4d-4e5f-8a9b-0c1d2e3f4a5b";
    private const string Schedules = FakeRoleScheduleRequests.EligibilitySchedulesPath;
    private const string Requests = FakeRoleScheduleRequests.EligibilityRequestsPath;

    private static (ProviderHost Host, string AliceId) Setup()
    {
        var host = new ProviderHost();
        BuiltInRoles.Seed(host);
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        return (host, alice);
    }

    private static string Eligibility(string properties, string name = Name)
    {
        return Templates.Resource(Type, name, properties);
    }

    private static string Props(string principalId, string extra = "", string role = "\"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\"")
    {
        return "{ \"principalId\": \"" + principalId + "\", " + role + extra + " }";
    }

    private static JsonObject SeedSchedule(ProviderHost host, string principalId, string roleDefinitionId, string type = "afterDateTime", string? end = "2030-01-01T00:00:00Z", string scope = "/", string? duration = null, string memberType = "Direct")
    {
        return host.Directory.Add(Schedules, new JsonObject
        {
            ["principalId"] = principalId,
            ["roleDefinitionId"] = roleDefinitionId,
            ["directoryScopeId"] = scope,
            ["appScopeId"] = null,
            ["status"] = "Provisioned",
            ["memberType"] = memberType,
            ["scheduleInfo"] = new JsonObject
            {
                ["startDateTime"] = "2025-01-01T10:00:00.123Z",
                ["recurrence"] = null,
                ["expiration"] = new JsonObject { ["type"] = type, ["endDateTime"] = end, ["duration"] = duration },
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
    public async Task ANewEligibility_IsCreatedWithAdminAssign_PermanentByDefault_AndTheRerunIsNoChange()
    {
        var (host, alice) = Setup();
        var document = host.Resources(Eligibility(Props(alice)));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(SingleRequest(run));
        Assert.Equal("adminAssign", body["action"]!.GetValue<string>());
        Assert.Equal(alice, body["principalId"]!.GetValue<string>());
        Assert.Equal(BuiltInRoles.GlobalReader, body["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal("/", body["directoryScopeId"]!.GetValue<string>());
        Assert.Equal("noExpiration", body["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        Assert.Equal("Deployed by earm", body["justification"]!.GetValue<string>());
        var schedule = Assert.Single(host.Directory.Objects(Schedules));
        Assert.Equal(schedule["id"]!.GetValue<string>(), run.GraphIdOf("/providers/" + Type + "/" + Name));

        var rerun = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.NoChange, rerun.Change(Name).Type);
    }

    [Fact]
    public async Task ArmShapedProperties_AndTheSugar_AreAcceptedAndNormalisedToGraph()
    {
        var (host, alice) = Setup();
        string unit = host.Directory.Add(BuiltInRoles.UnitsPath, new JsonObject { ["displayName"] = "Orders AU" })["id"]!.GetValue<string>();
        string props = Props(alice, ", \"administrativeUnit\": \"Orders AU\", \"requestType\": \"AdminAssign\", \"justification\": \"JIT for Orders\", \"ticketInfo\": { \"ticketNumber\": \"CHG-1\", \"ticketSystem\": \"SNOW\" }, "
            + "\"scheduleInfo\": { \"startDateTime\": \"2025-01-01T00:00:00Z\", \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"2030-06-30T00:00:00Z\" } }", "\"role\": \"Security Reader\"");
        var document = host.Resources(Eligibility(props));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(SingleRequest(run));
        Assert.Equal(BuiltInRoles.SecurityReader, body["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal("/administrativeUnits/" + unit, body["directoryScopeId"]!.GetValue<string>());
        Assert.Equal("afterDateTime", body["scheduleInfo"]!["expiration"]!["type"]!.GetValue<string>());
        Assert.Equal("JIT for Orders", body["justification"]!.GetValue<string>());
        Assert.Equal("CHG-1", body["ticketInfo"]!["ticketNumber"]!.GetValue<string>());
        Assert.False(body.ContainsKey("requestType"));
        Assert.False(body.ContainsKey("role"));

        var rerun = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.NoChange, rerun.Change(Name).Type);
        Assert.Contains(rerun.Requests, r => r.Path == Schedules && r.UrlParameters!["$filter"].Contains("directoryScopeId eq '/administrativeUnits/" + unit + "'", StringComparison.Ordinal));
    }

    // ---- compare ----

    [Fact]
    public async Task TheSameEnd_WithinOneSecond_APastStart_AndDifferentMetadata_IsNoChange()
    {
        var (host, alice) = Setup();
        SeedSchedule(host, alice, BuiltInRoles.GlobalReader, end: "2030-01-01T00:00:00.400Z");
        string props = Props(alice, ", \"justification\": \"another reason\", \"ticketInfo\": { \"ticketNumber\": \"X\" }, \"scheduleInfo\": { \"startDateTime\": \"2024-03-01T00:00:00Z\", \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2030-01-01T00:00:00Z\" } }");

        var run = await host.WhatIfAsync(host.Resources(Eligibility(props)));

        var change = run.Change(Name);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.DoesNotContain(change.Deltas, d => d.HasEffect);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task AfterDuration_IsCreateTimeIntent_AndIsNotComparedWithTheComputedEnd()
    {
        var (host, alice) = Setup();
        SeedSchedule(host, alice, BuiltInRoles.GlobalReader, type: "afterDateTime", end: "2026-12-31T00:00:00Z");
        string props = Props(alice, ", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P365D\" } }");

        var run = await host.WhatIfAsync(host.Resources(Eligibility(props)));

        Assert.Equal(ChangeType.NoChange, run.Change(Name).Type);
    }

    [Fact]
    public async Task ALaterEnd_IsAModifyOfTheEndOnly_AppliedInPlaceWithAdminUpdate()
    {
        var (host, alice) = Setup();
        var seeded = SeedSchedule(host, alice, BuiltInRoles.GlobalReader);
        string props = Props(alice, ", \"justification\": \"extend\", \"scheduleInfo\": { \"expiration\": { \"type\": \"afterDateTime\", \"endDateTime\": \"2031-01-01T00:00:00Z\" } }");
        var document = host.Resources(Eligibility(props));

        var whatIf = await host.WhatIfAsync(document);
        var change = whatIf.Change(Name);
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.Equal(ChangeMode.Patch, change.Mode);
        Assert.Equal(["properties.scheduleInfo.expiration.endDateTime"], change.Deltas.SelectMany(Flatten).Where(d => d.HasEffect && (d.Children is null || d.Children.Count == 0)).Select(d => d.Path.ToString()).Distinct().ToArray());
        Assert.Contains(change.Reasons, r => r.Contains("adminUpdate", StringComparison.Ordinal));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(SingleRequest(run));
        Assert.Equal("adminUpdate", body["action"]!.GetValue<string>());
        Assert.Equal("extend", body["justification"]!.GetValue<string>());
        var live = Assert.Single(host.Directory.Objects(Schedules));
        Assert.Equal(seeded["id"]!.GetValue<string>(), live["id"]!.GetValue<string>());
        Assert.Equal("2031-01-01T00:00:00Z", live["scheduleInfo"]!["expiration"]!["endDateTime"]!.GetValue<string>());
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(Name).Type);
    }

    [Fact]
    public async Task AChangedExpirationType_IsAModify()
    {
        var (host, alice) = Setup();
        SeedSchedule(host, alice, BuiltInRoles.GlobalReader);

        var run = await host.WhatIfAsync(host.Resources(Eligibility(Props(alice))));

        Assert.Equal(ChangeType.Modify, run.Change(Name).Type);
    }

    [Fact]
    public async Task InheritedSchedules_AreNotTheDeclaredEligibility()
    {
        var (host, alice) = Setup();
        SeedSchedule(host, alice, BuiltInRoles.GlobalReader, type: "noExpiration", end: null, memberType: "Group");

        var run = await host.WhatIfAsync(host.Resources(Eligibility(Props(alice))));

        Assert.Equal(ChangeType.Create, run.Change(Name).Type);
    }

    // ---- tuple change, delete ----

    [Fact]
    public async Task AChangedRole_IsANewEligibility_AndTheOldOneIsReportedRetained()
    {
        var (host, alice) = Setup();
        var old = SeedSchedule(host, alice, BuiltInRoles.GlobalReader, type: "noExpiration", end: null);
        var document = host.Resources(Eligibility(Props(alice, role: "\"roleDefinitionId\": \"" + BuiltInRoles.SecurityReader + "\"")));

        var whatIf = await host.WhatIfAsync(document);

        Assert.Equal(ChangeType.Create, whatIf.Change(Name).Type);
        var retained = Assert.Single(whatIf.Plan.Orphans);
        Assert.Equal(OrphanReason.Retained, retained.Orphan);
        Assert.Equal(old["id"]!.GetValue<string>(), retained.GraphId);
        Assert.Contains(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.AssignmentRetained && d.Message.Contains("eligible", StringComparison.Ordinal));

        var run = await host.CreateAsync(document);
        Check.Succeeded(run);
        Assert.Equal(2, host.Directory.Objects(Schedules).Count);
        Assert.DoesNotContain(run.Writes, w => Check.Body(w)["action"]!.GetValue<string>() == "adminRemove");
    }

    [Fact]
    public async Task DeploymentDelete_RemovesTheEligibilityWithAdminRemove()
    {
        var (host, alice) = Setup();
        SeedSchedule(host, alice, BuiltInRoles.GlobalReader, type: "noExpiration", end: null);
        host.ClearRequests();

        var result = await host.DeleteDeclaredAsync(host.Resources(Eligibility(Props(alice))));

        Assert.True(result.Succeeded, string.Join("; ", result.Outcomes.Select(o => o.Error)));
        var remove = Assert.Single(host.Fake.Writes);
        Assert.Equal("adminRemove", Check.Body(remove)["action"]!.GetValue<string>());
        Assert.Empty(host.Directory.Objects(Schedules));
    }

    // ---- Graph quirks ----

    [Fact]
    public async Task RoleAssignmentExists_OnAPlannedCreate_IsAdoptedAfterAReRead()
    {
        var (host, alice) = Setup();
        var seeded = SeedSchedule(host, alice, BuiltInRoles.GlobalReader, type: "noExpiration", end: null);
        // The plan's read and the provider's re-read after the POST miss it (a lagging replica); the POST answers RoleAssignmentExists and the engine adopts.
        host.AbsenceCheck = new KeyAbsenceCheck { Delay = host.AbsenceDelays, ExtraReads = 0 };
        host.Fake.FlapLists(Schedules, false, false);

        var run = await host.CreateAsync(host.Resources(Eligibility(Props(alice))));

        Check.Succeeded(run);
        Assert.Equal("adminAssign", Check.Body(SingleRequest(run))["action"]!.GetValue<string>());
        Assert.Equal(seeded["id"]!.GetValue<string>(), run.GraphIdOf("/providers/" + Type + "/" + Name));
        Assert.Single(host.Directory.Objects(Schedules));
    }

    [Fact]
    public async Task APolicyThatForbidsPermanentEligibility_FailsWithPimPolicyViolation_CarryingGraphsText()
    {
        var (host, alice) = Setup();
        host.Directory.RoleEligibility.RequireExpiration = true;

        var run = await host.CreateAsync(host.Resources(Eligibility(Props(alice))));

        Assert.False(run.Succeeded);
        var op = run.Op(Name);
        Assert.Equal(EngineDiagnosticCodes.PimPolicyViolation, op.ErrorCode);
        Assert.Contains("ExpirationRule", op.Error, StringComparison.Ordinal);
        Assert.Empty(host.Directory.Objects(Schedules));
    }

    [Fact]
    public async Task AGroupThatIsNotRoleAssignable_IsRefusedBeforeAnyRequest()
    {
        var (host, _) = Setup();
        string group = host.Directory.AddGroup("plain", "Plain")["id"]!.GetValue<string>();
        var document = host.Resources(Eligibility(Props(group)));

        var whatIf = await host.WhatIfAsync(document);
        var run = await host.CreateAsync(document);

        Assert.NotNull(whatIf.Change(Name).UnsupportedReason);
        Assert.Contains("not role-assignable", whatIf.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.RoleEligibilityInvalid, run.Op(Name).ErrorCode);
        Assert.DoesNotContain(run.Writes, w => w.Path == Requests);
    }

    [Fact]
    public async Task ARoleAssignableGroup_CanBeMadeEligible()
    {
        var (host, _) = Setup();
        string group = host.Directory.AddGroup("pim-admins", "PIM Admins", g => g["isAssignableToRole"] = true)["id"]!.GetValue<string>();

        var run = await host.CreateAsync(host.Resources(Eligibility(Props(group))));

        Check.Succeeded(run);
    }

    [Fact]
    public async Task AsynchronousProcessing_IsWaitedOut_AndTheScheduleIdIsTheRequestsTarget()
    {
        var (host, alice) = Setup();
        host.Directory.RoleEligibility.ProcessingLagLists = 2;
        var document = host.Resources(Eligibility(Props(alice)));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Check.NoDiagnostic(run, EngineDiagnosticCodes.CreatedObjectNotVisible);
        var schedule = Assert.Single(host.Directory.Objects(Schedules));
        Assert.Equal(schedule["id"]!.GetValue<string>(), run.GraphIdOf("/providers/" + Type + "/" + Name));
        Assert.Equal(ChangeType.NoChange, (await host.WhatIfAsync(document)).Change(Name).Type);
    }

    // ---- refused declarations ----

    [Theory]
    [InlineData(", \"requestType\": \"[concat('Admin', 'Remove')]\"", "must be AdminAssign")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"[concat('After', 'DateTime')]\" } }", "endDateTime is required")]
    [InlineData(", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"[concat('365', ' days')]\" } }", "ISO 8601 duration")]
    [InlineData(", \"scheduleInfo\": { \"startDateTime\": \"2027-01-01T00:00:00Z\", \"expiration\": { \"type\": \"AfterDateTime\", \"endDateTime\": \"[concat('2026-01-01', 'T00:00:00Z')]\" } }", "must be after")]
    public async Task AComputedValueThatBreaksTheRules_IsAnUnsupportedRow(string extra, string message)
    {
        var (host, alice) = Setup();

        var run = await host.WhatIfAsync(host.Resources(Eligibility(Props(alice, extra))));

        Assert.Contains(message, run.Change(Name).UnsupportedReason, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    private static System.Collections.Generic.IEnumerable<PropertyDelta> Flatten(PropertyDelta delta)
    {
        yield return delta;
        foreach (var child in delta.Children ?? [])
        {
            foreach (var nested in Flatten(child))
            {
                yield return nested;
            }
        }
    }
}
