using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Graph;
using EArm.Providers;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// PIM through the CLI: directory role eligibilities, PIM for Groups eligibilities and time-bound active role and group assignments (create, rerun NoChange,
/// adminUpdate of the end date, a changed tuple reported as retained, delete), the refusals that need no write (a dynamic group, declarations that fight a
/// time-bound active assignment) and the PIM part of the cleanup guard. Every test needs <c>EARM_IT_ALLOW_PIM=1</c> (Microsoft Entra ID P2 or ID
/// Governance) and <c>EARM_IT_DOMAIN</c> (the principals are disabled test users).
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class PimScenarioTests : IntegrationTestBase
{
    private const Needs PimNeeds = Needs.Domain | Needs.Pim;

    public PimScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Pim_RoleEligibilityLifecycleWithAdminUpdateTupleChangeAndDelete()
    {
        Fixture.Require(PimNeeds);
        string e1 = Guid.NewGuid().ToString("D");
        string e2 = Guid.NewGuid().ToString("D");
        var end = PimReads.DaysFromNow(20);
        var run = Fixture.NewRun("pimrole", "pim-role-eligibility.entra.json")
            .WithParameter("domain", Fixture.Domain)
            .WithParameter("e1Name", e1)
            .WithParameter("e2Name", e2)
            .WithParameter("e2End", PimReads.Iso(end));

        string messageCenterReader = await PimReads.RoleTemplateIdAsync(Graph, "Message Center Reader", Ct);
        string directoryReaders = await PimReads.RoleTemplateIdAsync(Graph, "Directory Readers", Ct);
        string reportsReader = await PimReads.RoleTemplateIdAsync(Graph, "Reports Reader", Ct);

        var preview = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
        AssertChange(preview, "/" + e1, "Create", "what-if before the first create");
        AssertChange(preview, "/" + e2, "Create", "what-if before the first create");

        var created = await Fixture.Cli.CreateAsync(run, Ct);
        AssertCreated(created, "create");
        string userId = Output(created, "userId");

        var schedules = await EventuallyAsync(
            () => PimReads.ByPrincipalAsync(Graph, RoleSchedulesLookup.EligibilitySchedulesPath, userId, Ct),
            found => PimReads.OfRole(found, messageCenterReader).Count == 1 && PimReads.OfRole(found, directoryReaders).Count == 1,
            seconds: 120,
            stepSeconds: 5);
        var afterDuration = Assert.Single(PimReads.OfRole(schedules, messageCenterReader));
        var afterDateTime = Assert.Single(PimReads.OfRole(schedules, directoryReaders));
        Observe($"AfterDuration P30D eligibility reads back as: {PimReads.DescribeSchedule(afterDuration)}");
        Assert.Equal("afterDateTime", PimReads.ExpirationType(afterDateTime), ignoreCase: true);
        AssertEndNear(end, afterDateTime, "the AfterDateTime eligibility after create");

        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "rerun after create");

        // adminUpdate: a new end date is a Modify of the same eligibility, never a recreate.
        var newEnd = end.AddDays(10);
        var moved = run.WithParameter("e2End", PimReads.Iso(newEnd));
        var modify = await Fixture.Cli.WhatIfAsync(moved, Ct, "--exit-code-on-changes");
        AssertChange(modify, "/" + e2, "Modify", "what-if after moving the end date");
        Assert.Contains(Row(modify, e2)["reasons"]?.AsArray() ?? [], r => r?.ToString().Contains("adminUpdate", StringComparison.Ordinal) == true);
        Assert.Equal("NoChange", modify.ChangeTypeOf("/" + e1));
        AssertCreated(await Fixture.Cli.CreateAsync(moved, Ct), "create with the moved end date");
        var updated = await EventuallyAsync(
            async () => PimReads.OfRole(await PimReads.ByPrincipalAsync(Graph, RoleSchedulesLookup.EligibilitySchedulesPath, userId, Ct), directoryReaders),
            found => found.Count == 1 && PimReads.EndDateTime(found[0]) is { } at && (at - newEnd).Duration() <= TimeSpan.FromSeconds(2),
            seconds: 120,
            stepSeconds: 5);
        AssertEndNear(newEnd, Assert.Single(updated), "the AfterDateTime eligibility after the adminUpdate");
        Observe($"adminUpdate kept the schedule id: {string.Equals(updated[0].Text("id"), afterDateTime.Text("id"), StringComparison.OrdinalIgnoreCase)} ({afterDateTime.Text("id")} -> {updated[0].Text("id")})");
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(moved, Ct, "--exit-code-on-changes"), "rerun after the adminUpdate");

        // A changed role is a new tuple: a new eligibility, and the old one is retained (reported, never removed).
        var retuned = moved.WithParameter("e2Role", "Reports Reader");
        var tuple = await Fixture.Cli.WhatIfAsync(retuned, Ct, "--exit-code-on-changes");
        AssertChange(tuple, "/" + e2, "Create", "what-if after changing the role");
        string retainedId = updated[0].RequireId();
        Assert.True(
            tuple.Changes.Any(c => (string?)c["orphan"] == "retained" && string.Equals((string?)c["graphId"], retainedId, StringComparison.OrdinalIgnoreCase)),
            $"The old Directory Readers eligibility ({retainedId}) is not reported as retained: {tuple.DescribeChanges()}");
        Assert.True(tuple.HasDiagnostic(EngineDiagnosticCodes.AssignmentRetained), "No AssignmentRetained warning for the old tuple.");
        AssertCreated(await Fixture.Cli.CreateAsync(retuned, Ct), "create with the changed role");
        var both = await EventuallyAsync(
            () => PimReads.ByPrincipalAsync(Graph, RoleSchedulesLookup.EligibilitySchedulesPath, userId, Ct),
            found => PimReads.OfRole(found, reportsReader).Count == 1,
            seconds: 120,
            stepSeconds: 5);
        Assert.Single(PimReads.OfRole(both, directoryReaders));

        // The retained eligibility is the operator's to remove (EARM never does): remove it as the AssignmentRetained warning advises.
        await PimReads.SendAsync(Graph, RoleSchedulesLookup.EligibilityRequestsPath, PimReads.RoleRequest(PimScheduleRequests.AdminRemove, userId, directoryReaders, null), Ct);
        await EventuallyAsync(
            async () => PimReads.OfRole(await PimReads.ByPrincipalAsync(Graph, RoleSchedulesLookup.EligibilitySchedulesPath, userId, Ct), directoryReaders),
            found => found.Count == 0,
            seconds: 120,
            stepSeconds: 5);

        // deployment delete: adminRemove of the declared eligibilities (delete order 2), then the user.
        AssertExit(0, await Fixture.Cli.DeleteAllAsync(retuned, Ct), "deployment delete");
        var left = await EventuallyAsync(
            () => PimReads.ByPrincipalAsync(Graph, RoleSchedulesLookup.EligibilitySchedulesPath, userId, Ct),
            found => found.Count == 0,
            seconds: 120,
            stepSeconds: 5);
        Assert.True(left.Count == 0, "Eligibilities remain after deployment delete: " + string.Join("; ", left.Select(PimReads.DescribeSchedule)));
    }

    [Fact]
    public async Task It_Pim_GroupEligibilityMemberAndOwnerWithDynamicGroupRefusal()
    {
        Fixture.Require(PimNeeds);
        string member = Guid.NewGuid().ToString("D");
        string owner = Guid.NewGuid().ToString("D");
        var end = PimReads.DaysFromNow(20);
        var run = Fixture.NewRun("pimgroup", "pim-group-eligibility.entra.json")
            .WithParameter("domain", Fixture.Domain)
            .WithParameter("memberName", member)
            .WithParameter("ownerName", owner)
            .WithParameter("ownerEnd", PimReads.Iso(end));

        var created = await Fixture.Cli.CreateAsync(run, Ct);
        AssertCreated(created, "create");
        string userId = Output(created, "userId");
        string groupId = Output(created, "groupId");

        var schedules = await EventuallyAsync(
            () => PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.EligibilitySchedulesPath, groupId, Ct),
            found => PimReads.OfAccess(found, userId, "member").Count == 1 && PimReads.OfAccess(found, userId, "owner").Count == 1,
            seconds: 120,
            stepSeconds: 5);
        var memberSchedule = Assert.Single(PimReads.OfAccess(schedules, userId, "member"));
        var ownerSchedule = Assert.Single(PimReads.OfAccess(schedules, userId, "owner"));
        Observe($"member eligibility: {PimReads.DescribeSchedule(memberSchedule)}; owner eligibility: {PimReads.DescribeSchedule(ownerSchedule)}");
        AssertEndNear(end, ownerSchedule, "the owner eligibility after create");

        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "rerun after create");

        var newEnd = end.AddDays(10);
        var moved = run.WithParameter("ownerEnd", PimReads.Iso(newEnd));
        var modify = await Fixture.Cli.WhatIfAsync(moved, Ct, "--exit-code-on-changes");
        AssertChange(modify, "/" + owner, "Modify", "what-if after moving the owner end date");
        Assert.Equal("NoChange", modify.ChangeTypeOf("/" + member));
        AssertCreated(await Fixture.Cli.CreateAsync(moved, Ct), "create with the moved owner end date");
        var updated = await EventuallyAsync(
            async () => PimReads.OfAccess(await PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.EligibilitySchedulesPath, groupId, Ct), userId, "owner"),
            found => found.Count == 1 && PimReads.EndDateTime(found[0]) is { } at && (at - newEnd).Duration() <= TimeSpan.FromSeconds(2),
            seconds: 120,
            stepSeconds: 5);
        AssertEndNear(newEnd, Assert.Single(updated), "the owner eligibility after the adminUpdate");
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(moved, Ct, "--exit-code-on-changes"), "rerun after the adminUpdate");

        // PIM for Groups cannot govern a dynamic membership group: refused at plan time (Prepare), before any request.
        string dynamicId = await CreateDynamicGroupAsync("pim-dyn");
        string variant = TemplateEditor.Write(Fixture.ScratchDirectory, "pim-dynamic.entra.json", DynamicGroupEligibilityTemplate(dynamicId, userId, out string dynamicName));
        var refused = await Fixture.Cli.WhatIfAsync(run.WithTemplate(variant), Ct, "--exit-code-on-changes");
        var row = Row(refused, dynamicName);
        Assert.True(
            (string?)row["changeType"] == "Unsupported" && ((string?)row["unsupportedReason"])?.Contains("dynamic membership", StringComparison.OrdinalIgnoreCase) == true,
            $"Expected the eligibility on a dynamic group to be refused (Unsupported, 'dynamic membership'): {row.ToJsonString()}");
        Assert.Empty(await PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.EligibilitySchedulesPath, dynamicId, Ct));

        AssertExit(0, await Fixture.Cli.DeleteAllAsync(moved, Ct), "deployment delete");
        var left = await EventuallyAsync(
            () => PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.EligibilitySchedulesPath, groupId, Ct),
            found => found.Count == 0,
            seconds: 120,
            stepSeconds: 5);
        Assert.True(left.Count == 0, "Group eligibilities remain after deployment delete: " + string.Join("; ", left.Select(PimReads.DescribeSchedule)));
    }

    [Fact]
    public async Task It_Pim_TimeBoundActiveRoleAndGroupAssignments()
    {
        Fixture.Require(PimNeeds);
        string role = Guid.NewGuid().ToString("D");
        string member = Guid.NewGuid().ToString("D");
        string owner = Guid.NewGuid().ToString("D");
        var end = PimReads.DaysFromNow(5);
        var run = Fixture.NewRun("pimactive", "pim-active.entra.json")
            .WithParameter("domain", Fixture.Domain)
            .WithParameter("roleName", role)
            .WithParameter("memberName", member)
            .WithParameter("ownerName", owner)
            .WithParameter("activeEnd", PimReads.Iso(end));
        string messageCenterReader = await PimReads.RoleTemplateIdAsync(Graph, "Message Center Reader", Ct);

        var created = await Fixture.Cli.CreateAsync(run, Ct);
        AssertCreated(created, "create");
        string userId = Output(created, "userId");
        string groupId = Output(created, "groupId");

        var roleSchedules = await EventuallyAsync(
            async () => PimReads.OfRole(await PimReads.ByPrincipalAsync(Graph, RoleSchedulesLookup.AssignmentSchedulesPath, userId, Ct), messageCenterReader),
            found => found.Count == 1,
            seconds: 120,
            stepSeconds: 5);
        var activeRole = Assert.Single(roleSchedules);
        Observe($"active role assignment: {PimReads.DescribeSchedule(activeRole)}");
        Assert.True(PimScheduleRequests.IsAssigned(activeRole) && !PimScheduleComparison.IsPermanent(activeRole), "The active role assignment is not a time-bound Assigned schedule: " + PimReads.DescribeSchedule(activeRole));

        var groupSchedules = await EventuallyAsync(
            () => PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.AssignmentSchedulesPath, groupId, Ct),
            found => PimReads.OfAccess(found, userId, "member").Count == 1 && PimReads.OfAccess(found, userId, "owner").Count == 1,
            seconds: 120,
            stepSeconds: 5);
        AssertEndNear(end, Assert.Single(PimReads.OfAccess(groupSchedules, userId, "owner")), "the active ownership after create");

        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "rerun after create");

        var newEnd = end.AddDays(2);
        var moved = run.WithParameter("activeEnd", PimReads.Iso(newEnd));
        var modify = await Fixture.Cli.WhatIfAsync(moved, Ct, "--exit-code-on-changes");
        AssertChange(modify, "/" + owner, "Modify", "what-if after moving the ownership end date");
        Assert.Equal("NoChange", modify.ChangeTypeOf("/" + role));
        Assert.Equal("NoChange", modify.ChangeTypeOf("/" + member));
        AssertCreated(await Fixture.Cli.CreateAsync(moved, Ct), "create with the moved end date");
        var lastWrite = DateTimeOffset.UtcNow;
        var updated = await EventuallyAsync(
            async () => PimReads.OfAccess(await PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.AssignmentSchedulesPath, groupId, Ct), userId, "owner"),
            found => found.Count == 1 && PimReads.EndDateTime(found[0]) is { } at && (at - newEnd).Duration() <= TimeSpan.FromSeconds(2),
            seconds: 120,
            stepSeconds: 5);
        AssertEndNear(newEnd, Assert.Single(updated), "the active ownership after the adminUpdate");
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(moved, Ct, "--exit-code-on-changes"), "rerun after the adminUpdate");

        // PIM refuses to remove an assignment younger than 5 minutes (400 ActiveDurationTooShort; Learn: pim-how-to-add-role-to-user): a delete before
        // then fails with ActiveAssignmentTooNew and exit 1, and the delete once the assignments are old enough removes them. Only the rest of the
        // 5 minutes is waited (the lifecycle above takes part of it).
        var early = await Fixture.Cli.DeleteAllAsync(moved, Ct);
        if (early.ExitCode != 0)
        {
            AssertExit(1, early, "deployment delete before the active assignments are 5 minutes old");
            Assert.Contains(EngineDiagnosticCodes.ActiveAssignmentTooNew, early.Combined, StringComparison.Ordinal);
            var remaining = lastWrite + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(15) - DateTimeOffset.UtcNow;
            Observe($"deployment delete refused with ActiveAssignmentTooNew; waiting {Math.Max(0, remaining.TotalSeconds):F0} s until the assignments are 5 minutes old");
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, Ct);
            }

            AssertExit(0, await Fixture.Cli.DeleteAllAsync(moved, Ct), "deployment delete once the active assignments are 5 minutes old");
        }
        else
        {
            Observe($"deployment delete succeeded {(DateTimeOffset.UtcNow - lastWrite).TotalSeconds:F0} s after the last write (PIM did not refuse it as too new)");
        }

        var roleLeft = await EventuallyAsync(
            async () => PimReads.OfRole(await PimReads.ByPrincipalAsync(Graph, RoleSchedulesLookup.AssignmentSchedulesPath, userId, Ct), messageCenterReader),
            found => found.Count == 0,
            seconds: 120,
            stepSeconds: 5);
        Assert.True(roleLeft.Count == 0, "The active role assignment remains after deployment delete: " + string.Join("; ", roleLeft.Select(PimReads.DescribeSchedule)));
        var groupLeft = await EventuallyAsync(
            async () => (await PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.AssignmentSchedulesPath, groupId, Ct)).Where(s => string.Equals(s.Text("principalId"), userId, StringComparison.OrdinalIgnoreCase)).ToList(),
            found => found.Count == 0,
            seconds: 120,
            stepSeconds: 5);
        Assert.True(groupLeft.Count == 0, "Active group assignments remain after deployment delete: " + string.Join("; ", groupLeft.Select(PimReads.DescribeSchedule)));
    }

    [Fact]
    public async Task It_Pim_ActiveAssignmentConflictsAreRefusedBeforeAnyWrite()
    {
        Fixture.Require(PimNeeds);
        var run = Fixture.NewRun("pimconflicts", "pim-active-conflicts.entra.json").WithParameter("domain", Fixture.Domain);

        // The planner finds template-level conflicts after expansion and before any Graph call: what-if fails validation (exit 2), nothing is written.
        var result = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
        AssertExit(2, result, "what-if of the conflicting declarations");
        var conflicts = result.Diagnostics
            .Where(d => (string?)d["code"] == EngineDiagnosticCodes.PimAssignmentConflict)
            .Select(d => (string?)d["message"] ?? string.Empty)
            .ToList();
        Observe("PimAssignmentConflict messages:" + Environment.NewLine + string.Join(Environment.NewLine, conflicts));
        string all = string.Join(Environment.NewLine, conflicts);
        Assert.True(conflicts.Count >= 4, $"Expected 4 PimAssignmentConflict errors (roleAssignments, directoryRoles, members, memberOf), got {conflicts.Count}:{Environment.NewLine}{all}{Environment.NewLine}{Tail(result.Combined)}");
        Assert.Contains(conflicts, m => m.Contains("same directory role assignment", StringComparison.Ordinal));
        Assert.Contains(conflicts, m => m.Contains("directoryRoles[0]", StringComparison.Ordinal));
        Assert.Contains(conflicts, m => m.Contains("authoritative members list", StringComparison.Ordinal));
        Assert.Contains(conflicts, m => m.Contains("memberOf", StringComparison.Ordinal));

        Assert.Null(await UserAsync(Fixture.Upn("pcu"), "id"));
        Assert.Null(await GroupByNameAsync(Fixture.Name("pcgrp"), "id"));
    }

    [Fact]
    public async Task It_Cleanup_SweepRemovesPimSchedulesOfPrefixedPrincipals()
    {
        Fixture.Require(PimNeeds);
        var user = await TenantObjects.CreateDisabledUserAsync(Fixture, "pim-sweep-user", Ct);
        string userId = user.RequireId();
        var group = await TenantObjects.CreateGroupAsync(Fixture, "pim-sweep-grp", Ct);
        string groupId = group.RequireId();
        string messageCenterReader = await PimReads.RoleTemplateIdAsync(Graph, "Message Center Reader", Ct);

        await PimReads.SendAsync(Graph, RoleSchedulesLookup.EligibilityRequestsPath, PimReads.RoleRequest(PimScheduleRequests.AdminAssign, userId, messageCenterReader, PimReads.ScheduleInfo(end: PimReads.DaysFromNow(7))), Ct);
        await PimReads.SendAsync(Graph, GroupSchedulesLookup.EligibilityRequestsPath, PimReads.GroupRequest(PimScheduleRequests.AdminAssign, groupId, userId, "member", PimReads.ScheduleInfo(end: PimReads.DaysFromNow(7))), Ct);
        await EventuallyAsync(
            () => PimReads.ByPrincipalAsync(Graph, RoleSchedulesLookup.EligibilitySchedulesPath, userId, Ct),
            found => found.Count > 0,
            seconds: 120,
            stepSeconds: 5);

        await Fixture.SweepNowAsync(Ct);

        string log = string.Join(Environment.NewLine, Fixture.CleanupLog);
        Assert.True(Fixture.CleanupLog.Any(line => line.StartsWith("pim: adminRemove accepted for role eligibility", StringComparison.Ordinal)), "The sweep did not remove the role eligibility:" + Environment.NewLine + log);
        Assert.True(Fixture.CleanupLog.Any(line => line.StartsWith("pim: adminRemove accepted for group eligibility", StringComparison.Ordinal)), "The sweep did not remove the group eligibility:" + Environment.NewLine + log);
        Assert.True(Fixture.Leftovers.Count == 0, "The sweep left objects behind:" + Environment.NewLine + string.Join(Environment.NewLine, Fixture.Leftovers));
        Assert.Null(await UserAsync(Fixture.Upn("pim-sweep-user"), "id"));
    }

    // ---- helpers ----

    private static string Output(CliResult created, string name)
    {
        return (string?)created.Json["outputs"]?[name]?["value"] ?? throw new InvalidOperationException($"No '{name}' output: {Tail(created.StandardOutput)}");
    }

    private static JsonObject Row(CliResult whatIf, string name)
    {
        return whatIf.Changes.FirstOrDefault(c => ((string?)c["resourceId"])?.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) == true)
            ?? throw new InvalidOperationException($"No what-if row for '{name}': {whatIf.DescribeChanges()}{Environment.NewLine}{Tail(whatIf.Combined)}");
    }

    private static void AssertEndNear(DateTimeOffset expected, JsonObject schedule, string what)
    {
        var end = PimReads.EndDateTime(schedule);
        Assert.True(
            end is { } at && (at - expected).Duration() <= TimeSpan.FromSeconds(2),
            $"{what}: expected endDateTime {PimReads.Iso(expected)} but read {PimReads.DescribeSchedule(schedule)}");
    }

    /// <summary>A dynamic membership security group whose rule matches nobody and whose processing is paused (never populated).</summary>
    private async Task<string> CreateDynamicGroupAsync(string suffix)
    {
        string name = Fixture.Name(suffix);
        var group = await Graph.CreateObjectAsync(
            "/groups",
            new JsonObject
            {
                ["displayName"] = name,
                ["mailEnabled"] = false,
                ["mailNickname"] = name,
                ["securityEnabled"] = true,
                ["groupTypes"] = new JsonArray("DynamicMembership"),
                ["membershipRule"] = $"(user.displayName -eq \"{name}-nobody\")",
                ["membershipRuleProcessingState"] = "Paused",
            },
            Ct);
        string id = group.RequireId();
        Fixture.Track("/groups/" + id);
        await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest("/groups/" + id).WithSelect("id"), Ct), found => found is not null);
        return id;
    }

    /// <summary>A one-resource template: a member eligibility of <paramref name="userId"/> on the dynamic group (literal ids; the name is a fresh GUID label).</summary>
    private static JsonObject DynamicGroupEligibilityTemplate(string dynamicGroupId, string userId, out string name)
    {
        name = Guid.NewGuid().ToString("D");
        return new JsonObject
        {
            ["$schema"] = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#",
            ["contentVersion"] = "1.0.0.0",
            ["parameters"] = new JsonObject
            {
                ["prefix"] = new JsonObject { ["type"] = "string" },
                ["domain"] = new JsonObject { ["type"] = "string" },
                ["memberName"] = new JsonObject { ["type"] = "string" },
                ["ownerName"] = new JsonObject { ["type"] = "string" },
                ["ownerEnd"] = new JsonObject { ["type"] = "string" },
            },
            ["resources"] = new JsonArray(new JsonObject
            {
                ["type"] = "Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests",
                ["apiVersion"] = "2026-10-01-preview",
                ["name"] = name,
                ["properties"] = new JsonObject
                {
                    ["groupId"] = dynamicGroupId,
                    ["principalId"] = userId,
                    ["accessId"] = "member",
                    ["scheduleInfo"] = new JsonObject { ["expiration"] = new JsonObject { ["type"] = "AfterDuration", ["duration"] = "P7D" } },
                },
            }),
        };
    }
}
