using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Providers;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Verification probes for the open PIM questions of phases 2-4 (<c>EARM_IT_ALLOW_PIM=1</c> and <c>EARM_IT_DOMAIN</c>). Each probe makes its own disabled
/// test user (and group) directly through Graph, records what Graph answers (<see cref="ProbeReport"/>) and asserts only the assumption the code relies on,
/// naming the file to change when it fails. The schedules a probe leaves behind are removed by the PIM part of the cleanup guard.
/// </summary>
[Collection(RealTenantCollection.Name)]
[Trait("Category", "Verify")]
public sealed class PimProbes : IntegrationTestBase
{
    private const Needs PimNeeds = Needs.Domain | Needs.Pim;

    public PimProbes(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Probe_P1_PimErrorCodesForExistsRemoveMissingAndPolicy()
    {
        Fixture.Require(PimNeeds);
        var report = new ProbeReport("p1");
        string userId = await NewUserAsync("probe-pim-err");
        string role = await PimReads.RoleTemplateIdAsync(Graph, "Message Center Reader", Ct);
        string requests = RoleSchedulesLookup.EligibilityRequestsPath;

        var assign = PimReads.RoleRequest(PimScheduleRequests.AdminAssign, userId, role, PimReads.ScheduleInfo(end: PimReads.DaysFromNow(7)));
        await PimReads.SendAsync(Graph, requests, assign, Ct);
        await WaitForRoleScheduleAsync(RoleSchedulesLookup.EligibilitySchedulesPath, userId, role, present: true);

        // A second adminAssign of the same tuple: the provider adopts on RoleAssignmentExists (or a 409).
        try
        {
            await PimReads.SendAsync(Graph, requests, assign, Ct, expectReplicationLag: false);
            report.Observe("a second adminAssign of an existing eligibility was ACCEPTED (no RoleAssignmentExists); CreateAsync then reads the schedule back, which is fine.");
        }
        catch (GraphException ex)
        {
            report.Observe("second adminAssign: " + ProbeReport.Describe(ex));
            report.Expect(
                PimScheduleRequests.IsAlreadyExists(ex),
                $"a second adminAssign answered {ProbeReport.Describe(ex)}, which PimScheduleRequests.IsAlreadyExists does not recognise: the create path would fail instead of adopting. Fix IsAlreadyExists in src/EArm.Providers/PimScheduleRequests.cs.");
        }

        // adminRemove, then the same adminRemove again: the delete path treats "already gone" as done.
        var remove = PimReads.RoleRequest(PimScheduleRequests.AdminRemove, userId, role, null);
        await PimReads.SendAsync(Graph, requests, remove, Ct);
        await WaitForRoleScheduleAsync(RoleSchedulesLookup.EligibilitySchedulesPath, userId, role, present: false);
        try
        {
            await PimReads.SendAsync(Graph, requests, remove, Ct, expectReplicationLag: false);
            report.Observe("an adminRemove of a missing eligibility was ACCEPTED; DeleteAsync is fine with that.");
        }
        catch (GraphException ex)
        {
            report.Observe("adminRemove of a missing eligibility: " + ProbeReport.Describe(ex));
            report.Expect(
                PimScheduleRequests.IsAlreadyGone(ex),
                $"an adminRemove of a missing eligibility answered {ProbeReport.Describe(ex)}, which PimScheduleRequests.IsAlreadyGone does not recognise: deployment delete would fail for an eligibility removed outside EARM. Fix GonePattern / IsAlreadyGone in src/EArm.Providers/PimScheduleRequests.cs.");
        }

        // A policy refusal: the default policies may forbid a permanent group eligibility or a 10-year active role assignment. Whatever Graph refuses
        // with 400 must be classified as a policy violation (PimPolicyViolation) by the provider.
        string groupId = (await TenantObjects.CreateGroupAsync(Fixture, "probe-pim-policy", Ct)).RequireId();
        await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest("/groups/" + groupId).WithSelect("id"), Ct), found => found is not null);
        bool refused = false;
        var attempts = new (string Label, string Requests, JsonObject Body, JsonObject Remove)[]
        {
            ("permanent group eligibility", GroupSchedulesLookup.EligibilityRequestsPath, PimReads.GroupRequest(PimScheduleRequests.AdminAssign, groupId, userId, "member", PimReads.ScheduleInfo()), PimReads.GroupRequest(PimScheduleRequests.AdminRemove, groupId, userId, "member", PimReads.ScheduleInfo())),
            ("10-year active role assignment", RoleSchedulesLookup.AssignmentRequestsPath, PimReads.RoleRequest(PimScheduleRequests.AdminAssign, userId, role, PimReads.ScheduleInfo(duration: "P3650D")), PimReads.RoleRequest(PimScheduleRequests.AdminRemove, userId, role, null)),
            ("permanent active role assignment", RoleSchedulesLookup.AssignmentRequestsPath, PimReads.RoleRequest(PimScheduleRequests.AdminAssign, userId, role, PimReads.ScheduleInfo()), PimReads.RoleRequest(PimScheduleRequests.AdminRemove, userId, role, null)),
        };
        foreach (var (label, path, body, removeAccepted) in attempts)
        {
            bool accepted = false;
            try
            {
                await PimReads.SendAsync(Graph, path, body, Ct);
                accepted = true;
                report.Observe($"{label}: ACCEPTED by the default policy (no refusal to classify); removed again before the next attempt.");
            }
            catch (GraphException ex) when (PimScheduleRequests.IsAlreadyExists(ex))
            {
                report.Observe($"{label}: {ProbeReport.Describe(ex)} (an earlier attempt is still listed; not a policy answer)");
            }
            catch (GraphException ex) when (ex.Status == System.Net.HttpStatusCode.BadRequest)
            {
                refused = true;
                report.Observe($"{label}: {ProbeReport.Describe(ex)}");
                report.Expect(
                    PimScheduleRequests.IsPolicyViolation(ex),
                    $"{label} was refused with {ProbeReport.Describe(ex)}, which PimScheduleRequests.IsPolicyViolation does not classify as a policy refusal (PimPolicyViolation). Fix PolicyViolationPattern in src/EArm.Providers/PimScheduleRequests.cs (or, if this 400 is not a policy refusal, the probe's attempt).");
            }

            if (!accepted)
            {
                continue;
            }

            try
            {
                await PimReads.SendAsync(Graph, path, removeAccepted, Ct);
                if (path == RoleSchedulesLookup.AssignmentRequestsPath)
                {
                    await WaitForRoleScheduleAsync(RoleSchedulesLookup.AssignmentSchedulesPath, userId, role, present: false);
                }
            }
            catch (GraphException ex)
            {
                report.Observe($"{label}: removing the accepted request failed ({ProbeReport.Describe(ex)}); the cleanup guard retries");
            }
        }

        if (!refused)
        {
            report.Observe("no attempt was refused: this tenant's default policies allow all of them, so the PimPolicyViolation mapping stays UNVERIFIED (tighten a policy in a dev tenant and re-run).");
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_P2_AfterDurationReadBackTargetScheduleIdAndAdminUpdate()
    {
        Fixture.Require(PimNeeds);
        var report = new ProbeReport("p2");
        string userId = await NewUserAsync("probe-pim-dur");
        string role = await PimReads.RoleTemplateIdAsync(Graph, "Message Center Reader", Ct);

        var response = await PimReads.SendAsync(Graph, RoleSchedulesLookup.EligibilityRequestsPath, PimReads.RoleRequest(PimScheduleRequests.AdminAssign, userId, role, PimReads.ScheduleInfo(duration: "P30D")), Ct);
        report.Observe($"adminAssign response: id={response.Text("id")} targetScheduleId={response.Text("targetScheduleId")} status={response.Text("status")} scheduleInfo={response?["scheduleInfo"]?.ToJsonString()}");
        var schedule = Assert.Single(await WaitForRoleScheduleAsync(RoleSchedulesLookup.EligibilitySchedulesPath, userId, role, present: true));
        report.Observe("AfterDuration P30D reads back as: " + PimReads.DescribeSchedule(schedule));

        string? type = PimReads.ExpirationType(schedule);
        report.Expect(
            type is not null && (type.Equals("afterDuration", StringComparison.OrdinalIgnoreCase) || type.Equals("afterDateTime", StringComparison.OrdinalIgnoreCase)),
            $"an AfterDuration eligibility reads back with expiration type '{type}'; PimScheduleComparison.AlignSchedule only treats afterDuration and afterDateTime as the same kind, so every rerun would show drift. Fix AlignSchedule in src/EArm.Providers/PimScheduleComparison.cs.");
        string? standIn = response.Text("targetScheduleId") ?? response.Text("id");
        report.Expect(
            string.Equals(standIn, schedule.Text("id"), StringComparison.OrdinalIgnoreCase),
            $"the adminAssign response's targetScheduleId ({response.Text("targetScheduleId")}, id {response.Text("id")}) is not the schedule's id ({schedule.Text("id")}); the stand-in used until the schedule is listed would carry a wrong id. Fix StandInFor in src/EArm.Providers/PimScheduleRequestsProvider.cs.");

        // adminUpdate to a fractional-second end: the comparison tolerates TimeTolerance, the update keeps one Direct schedule for the tuple.
        // Variant "omit" leaves startDateTime out (what UpdateAsync sends for a declared past start); variant "live start" reuses the schedule's own
        // start. Both repeat RoleAssignmentExists (answered while a just-replaced schedule settles) with the provider's doubling backoff.
        var end = PimReads.DaysFromNow(40);
        var scheduleInfo = PimReads.ScheduleInfo(end: end);
        scheduleInfo["expiration"]!["endDateTime"] = PimReads.Iso(end).Replace("Z", ".500Z", StringComparison.Ordinal);
        scheduleInfo.Remove("startDateTime");
        var (update, omitOutcome) = await TryAdminUpdateAsync(userId, role, scheduleInfo, "omit startDateTime");
        report.Observe(omitOutcome);
        if (update is null && schedule.Nested("scheduleInfo")?.Text("startDateTime") is { } liveStart)
        {
            var withLiveStart = (JsonObject)scheduleInfo.DeepClone();
            withLiveStart["startDateTime"] = liveStart;
            (update, string liveOutcome) = await TryAdminUpdateAsync(userId, role, withLiveStart, "live startDateTime " + liveStart);
            report.Observe(liveOutcome);
        }

        report.Expect(
            omitOutcome.Contains("ACCEPTED", StringComparison.Ordinal),
            $"an adminUpdate without startDateTime was not accepted ({omitOutcome}); PimScheduleRequestsProvider.UpdateSchedule leaves a declared past start out, so switch it to the variant this probe found working (src/EArm.Providers/PimScheduleRequestsProvider.cs).");
        Assert.NotNull(update);
        report.Observe($"adminUpdate response: id={update.Text("id")} targetScheduleId={update.Text("targetScheduleId")} status={update.Text("status")}");
        var declaredEnd = end.AddMilliseconds(500);
        var updated = await EventuallyAsync(
            async () => PimReads.OfRole(await PimReads.ByPrincipalAsync(Graph, RoleSchedulesLookup.EligibilitySchedulesPath, userId, Ct), role),
            found => found.Count == 1 && PimReads.EndDateTime(found[0]) is { } at && (at - end).Duration() <= TimeSpan.FromMinutes(1),
            seconds: 120,
            stepSeconds: 5);
        report.Observe("after adminUpdate: " + string.Join("; ", updated.Select(PimReads.DescribeSchedule)));
        report.Observe($"the schedule id is {(updated.Count == 1 && string.Equals(updated[0].Text("id"), schedule.Text("id"), StringComparison.OrdinalIgnoreCase) ? "STABLE" : "NOT stable")} across adminUpdate ({schedule.Text("id")} -> {string.Join(",", updated.Select(u => u.Text("id")))}); UpdateAsync re-reads by tuple either way.");
        var direct = updated.Where(PimScheduleRequests.IsDirectAndActive).ToList();
        report.Expect(
            direct.Count == 1,
            $"after an adminUpdate the tuple lists {direct.Count} Direct, active eligibilities; FindAsync takes the first one, so the read could see the old schedule. Fix RoleSchedulesLookup.FindAsync (src/EArm.Providers/RoleSchedulesLookup.cs) or PimScheduleRequests.IsDirectAndActive.");
        if (direct.Count == 1 && PimReads.EndDateTime(direct[0]) is { } liveEnd)
        {
            var tolerance = PimScheduleRequestsProvider<RoleScheduleTuple>.TimeTolerance;
            report.Observe($"declared end {PimReads.Iso(end).Replace("Z", ".500Z", StringComparison.Ordinal)} reads back as {direct[0].Nested("scheduleInfo.expiration").Text("endDateTime")} (off by {(liveEnd - declaredEnd).Duration().TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms)");
            report.Expect(
                (liveEnd - declaredEnd).Duration() <= tolerance,
                $"Graph stores a fractional-second endDateTime {(liveEnd - declaredEnd).Duration().TotalMilliseconds} ms away from the declared one, more than TimeTolerance ({tolerance.TotalSeconds} s): every rerun of such a template would show drift. Widen TimeTolerance in src/EArm.Providers/PimScheduleRequestsProvider.cs.");
        }

        await PimReads.SendAsync(Graph, RoleSchedulesLookup.EligibilityRequestsPath, PimReads.RoleRequest(PimScheduleRequests.AdminRemove, userId, role, null), Ct);
        report.Complete();
    }

    /// <summary>
    /// One adminUpdate of the role eligibility, repeated on RoleAssignmentExists with the provider's schedule (5 s, doubling, within 90 s): the response, or
    /// <c>null</c> with the refusal; the outcome text says ACCEPTED or REFUSED and how many RoleAssignmentExists answers came first.
    /// </summary>
    private async Task<(JsonObject? Response, string Outcome)> TryAdminUpdateAsync(string userId, string role, JsonObject scheduleInfo, string variant)
    {
        var delay = TimeSpan.FromSeconds(5);
        var waited = TimeSpan.Zero;
        int exists = 0;
        while (true)
        {
            try
            {
                var response = await PimReads.SendAsync(Graph, RoleSchedulesLookup.EligibilityRequestsPath, PimReads.RoleRequest(PimScheduleRequests.AdminUpdate, userId, role, scheduleInfo), Ct);
                return (response, $"adminUpdate ({variant}): ACCEPTED after {exists} RoleAssignmentExists answer(s), {waited.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s of backoff");
            }
            catch (GraphException ex) when (PimScheduleRequests.IsAlreadyExists(ex) && waited + delay <= TimeSpan.FromSeconds(90))
            {
                exists++;
                await Task.Delay(delay, Ct);
                waited += delay;
                delay += delay;
            }
            catch (GraphException ex)
            {
                return (null, $"adminUpdate ({variant}): REFUSED after {exists} RoleAssignmentExists answer(s): {ex.Message}");
            }
        }
    }

    [Fact]
    public async Task It_Probe_P3_GroupEligibilityMemberTypeFiltersAndRemoveWithoutSchedule()
    {
        Fixture.Require(PimNeeds);
        var report = new ProbeReport("p3");
        string userId = await NewUserAsync("probe-pim-ge");
        string groupId = await NewGroupAsync("probe-pim-ge-grp");
        var tuple = new GroupScheduleTuple(groupId, userId, "member");

        await PimReads.SendAsync(Graph, GroupSchedulesLookup.EligibilityRequestsPath, PimReads.GroupRequest(PimScheduleRequests.AdminAssign, groupId, userId, "member", PimReads.ScheduleInfo(duration: "P7D")), Ct);
        var listed = await EventuallyAsync(
            async () => PimReads.OfAccess(await PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.EligibilitySchedulesPath, groupId, Ct), userId, "member"),
            found => found.Count > 0,
            seconds: 120,
            stepSeconds: 5);
        var schedule = Assert.Single(listed);
        report.Observe($"group eligibility as listed: memberType='{schedule.Text("memberType")}' accessId='{schedule.Text("accessId")}' status='{schedule.Text("status")}' {PimReads.DescribeSchedule(schedule)}");
        report.Expect(
            PimScheduleRequests.IsDirectAndActive(schedule),
            $"a fresh direct group eligibility (memberType '{schedule.Text("memberType")}', status '{schedule.Text("status")}') is not accepted by PimScheduleRequests.IsDirectAndActive, so the provider would never find it. Fix IsDirectAndActive (FailedStatuses, memberType casing) in src/EArm.Providers/PimScheduleRequests.cs.");
        report.Expect(
            GroupSchedulesLookup.Matches(schedule, tuple),
            $"GroupSchedulesLookup.Matches does not match the live schedule to its tuple (accessId '{schedule.Text("accessId")}'). Fix Matches in src/EArm.Providers/GroupSchedulesLookup.cs.");

        // The combined filter FindAsync sends.
        string combined = $"groupId eq '{groupId}' and principalId eq '{userId}'";
        try
        {
            var found = await PimReads.SchedulesAsync(Graph, GroupSchedulesLookup.EligibilitySchedulesPath, combined, Ct);
            report.Observe($"$filter={combined}: {found.Count} schedule(s)");
            report.Expect(
                found.Any(s => string.Equals(s.Text("id"), schedule.Text("id"), StringComparison.OrdinalIgnoreCase)),
                $"$filter={combined} does not return the eligibility; GroupSchedulesLookup.FindAsync relies on it. Fix FindAsync in src/EArm.Providers/GroupSchedulesLookup.cs (filter on groupId only, match principalId client-side).");
        }
        catch (GraphException ex)
        {
            report.Expect(false, $"$filter={combined} is refused ({ProbeReport.Describe(ex)}); GroupSchedulesLookup.FindAsync relies on it. Fix FindAsync in src/EArm.Providers/GroupSchedulesLookup.cs (filter on groupId only, match principalId client-side).");
        }

        string withAccess = $"groupId eq '{groupId}' and accessId eq 'member'";
        try
        {
            report.Observe($"$filter={withAccess}: accepted, {(await PimReads.SchedulesAsync(Graph, GroupSchedulesLookup.EligibilitySchedulesPath, withAccess, Ct)).Count} schedule(s) (the code matches accessId client-side and does not need it)");
        }
        catch (GraphException ex)
        {
            report.Observe($"$filter={withAccess}: refused ({ProbeReport.Describe(ex)}); the code matches accessId client-side, so nothing depends on it");
        }

        // Does adminRemove need scheduleInfo for groups? The provider always sends the live schedule (RemoveSchedule).
        try
        {
            await PimReads.SendAsync(Graph, GroupSchedulesLookup.EligibilityRequestsPath, PimReads.GroupRequest(PimScheduleRequests.AdminRemove, groupId, userId, "member", null), Ct, expectReplicationLag: false);
            report.Observe("adminRemove WITHOUT scheduleInfo was accepted for a group eligibility: GroupScheduleRequestsProvider.RemoveSchedule is optional (harmless).");
        }
        catch (GraphException ex)
        {
            report.Observe("adminRemove without scheduleInfo: " + ProbeReport.Describe(ex) + " -> retrying with the live schedule, as the provider sends it");
            try
            {
                await PimReads.SendAsync(Graph, GroupSchedulesLookup.EligibilityRequestsPath, PimReads.GroupRequest(PimScheduleRequests.AdminRemove, groupId, userId, "member", PimScheduleComparison.ScheduleView(schedule)), Ct);
                report.Observe("adminRemove WITH the live scheduleInfo was accepted: the provider's RemoveSchedule is required.");
            }
            catch (GraphException retry)
            {
                report.Expect(false, $"adminRemove of a group eligibility is refused also with the live scheduleInfo ({ProbeReport.Describe(retry)}): deployment delete cannot remove group eligibilities. Fix RemoveSchedule in src/EArm.Providers/GroupScheduleRequestsProvider.cs.");
            }
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_P4_PimActiveGroupMemberIsListedInMembers()
    {
        Fixture.Require(PimNeeds);
        var report = new ProbeReport("p4");
        string userId = await NewUserAsync("probe-pim-am");
        string groupId = await NewGroupAsync("probe-pim-am-grp");

        await PimReads.SendAsync(Graph, GroupSchedulesLookup.AssignmentRequestsPath, PimReads.GroupRequest(PimScheduleRequests.AdminAssign, groupId, userId, "member", PimReads.ScheduleInfo(duration: "P1D")), Ct);
        var started = DateTime.UtcNow;
        var members = await EventuallyAsync(() => IdsOfAsync($"/groups/{groupId}/members"), found => found.Contains(userId), seconds: 150, stepSeconds: 5);
        report.Observe(members.Contains(userId)
            ? $"the PIM active member is listed by GET /groups/{{id}}/members after {(DateTime.UtcNow - started).TotalSeconds:F0} s"
            : "the PIM active member is NOT listed by GET /groups/{id}/members after 150 s");
        report.Expect(
            members.Contains(userId),
            "a PIM active group member is not a member in /groups/{id}/members: the refusal of a time-bound active membership next to a pruning members list (DeclarationConflicts.GroupAssignment) rests on it being a real member. Revisit src/EArm.Providers/DeclarationConflicts.cs and docs/providers.md (the conflict may be unnecessary).");

        var schedules = PimReads.OfAccess(await PimReads.ByGroupAsync(Graph, GroupSchedulesLookup.AssignmentSchedulesPath, groupId, Ct), userId, "member");
        report.Observe("active membership schedule: " + string.Join("; ", schedules.Select(PimReads.DescribeSchedule)));
        await PimReads.SendAsync(Graph, GroupSchedulesLookup.AssignmentRequestsPath, PimReads.GroupRequest(PimScheduleRequests.AdminRemove, groupId, userId, "member", schedules.Count > 0 ? PimScheduleComparison.ScheduleView(schedules[0]) : null), Ct);
        var after = await EventuallyAsync(() => IdsOfAsync($"/groups/{groupId}/members"), found => !found.Contains(userId), seconds: 120, stepSeconds: 5);
        report.Observe(after.Contains(userId) ? "the member is STILL listed 120 s after the adminRemove" : "the member left /members after the adminRemove");
        report.Complete();
    }

    [Fact]
    public async Task It_Probe_P5_PlainRoleAssignmentAppearsAsPermanentAssignmentSchedule()
    {
        Fixture.Require(PimNeeds);
        var report = new ProbeReport("p5");
        string userId = await NewUserAsync("probe-pim-plain");
        string role = await PimReads.RoleTemplateIdAsync(Graph, "Message Center Reader", Ct);

        var assignment = await Graph.CreateObjectAsync(
            "/roleManagement/directory/roleAssignments",
            new JsonObject { ["principalId"] = userId, ["roleDefinitionId"] = role, ["directoryScopeId"] = "/" },
            Ct);
        report.Observe("roleAssignments POST: id=" + assignment.Text("id"));
        var schedules = await WaitForRoleScheduleAsync(RoleSchedulesLookup.AssignmentSchedulesPath, userId, role, present: true);
        foreach (var schedule in schedules)
        {
            report.Observe($"roleAssignmentSchedules row: createdUsing={schedule.Text("createdUsing")} assignmentType={schedule.Text("assignmentType")} memberType={schedule.Text("memberType")} {PimReads.DescribeSchedule(schedule)}");
        }

        report.Expect(
            schedules.Count == 1 && PimScheduleComparison.IsPermanent(schedules[0]) && PimScheduleRequests.IsAssigned(schedules[0]) && PimScheduleRequests.IsDirectAndActive(schedules[0]),
            "a plain (permanent) roleAssignments entry does not read back as one Direct, Assigned, permanent roleAssignmentSchedule; RoleAssignmentScheduleRequestsProvider.LiveConflict would not refuse to adopt it. Fix LiveConflict in src/EArm.Providers/RoleAssignmentScheduleRequestsProvider.cs (or PimScheduleComparison.IsPermanent / PimScheduleRequests.IsAssigned).");
        if (schedules.Count == 1)
        {
            report.Observe($"createdUsing {(string.Equals(schedules[0].Text("createdUsing"), assignment.Text("id"), StringComparison.OrdinalIgnoreCase) ? "EQUALS" : "differs from")} the roleAssignment id");
        }

        await Graph.DeleteAsync(new GraphRequest("/roleManagement/directory/roleAssignments/" + assignment.RequireId()).WithHint(RetryHint.ExpectReplicationLag), Ct);
        report.Complete();
    }

    // ---- helpers ----

    private async Task<string> NewUserAsync(string suffix)
    {
        string id = (await TenantObjects.CreateDisabledUserAsync(Fixture, suffix, Ct)).RequireId();
        await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest("/users/" + id).WithSelect("id"), Ct), found => found is not null);
        return id;
    }

    private async Task<string> NewGroupAsync(string suffix)
    {
        string id = (await TenantObjects.CreateGroupAsync(Fixture, suffix, Ct)).RequireId();
        await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest("/groups/" + id).WithSelect("id"), Ct), found => found is not null);
        return id;
    }

    /// <summary>The schedules of the principal for the role in <paramref name="schedulesPath"/>, once at least one is (or none is) listed.</summary>
    private Task<List<JsonObject>> WaitForRoleScheduleAsync(string schedulesPath, string principalId, string roleDefinitionId, bool present)
    {
        return EventuallyAsync(
            async () => PimReads.OfRole(await PimReads.ByPrincipalAsync(Graph, schedulesPath, principalId, Ct), roleDefinitionId),
            found => present ? found.Count > 0 : found.Count == 0,
            seconds: 120,
            stepSeconds: 5);
    }
}
