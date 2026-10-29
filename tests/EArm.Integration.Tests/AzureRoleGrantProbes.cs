using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Graph;
using EArm.Providers;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Verification probes for the Azure Resource Manager behaviour <c>Microsoft.Entra/azureRoleEligibilityScheduleRequests</c> and
/// <c>Microsoft.Entra/azureRoleAssignments</c> rely on (<see cref="AzureRoleEligibilityLookup"/>, <see cref="AzureRoleAssignmentLookup"/>), at the resource group
/// of <see cref="AzurePimPolicyProbes"/> (<c>EARM_IT_ALLOW_AZURE_PIM_POLICIES=1</c>, <c>EARM_IT_AZURE_SCOPE</c>). AG1 only reads; AG2 and AG3 also need
/// <c>EARM_IT_ALLOW_AZURE_ROLE_GRANTS=1</c> and <c>EARM_IT_AZURE_PRINCIPAL_ID</c> (a throw-away group or user): they grant that principal Reader at the resource
/// group (an active assignment, then an eligibility), check what the providers assume, and remove the grant in a <c>finally</c>. A grant the principal already
/// has is never written or removed. Each probe records what ARM answers (<see cref="ProbeReport"/>) and asserts only what the providers rely on, naming the
/// file to change when it fails.
/// </summary>
[Collection(RealTenantCollection.Name)]
[Trait("Category", "Verify")]
[TestMethodOrderer(typeof(MethodNameTestMethodOrderer))]
public sealed class AzureRoleGrantProbes : IntegrationTestBase
{
    private const string EligibilityFile = "src/EArm.Providers/AzureRoleEligibilityLookup.cs";

    private const string AssignmentFile = "src/EArm.Providers/AzureRoleAssignmentLookup.cs";

    public AzureRoleGrantProbes(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    private AzurePimPolicyTarget Target => Fixture.Azure.Target;

    private ArmClient Arm => Fixture.Azure.Arm;

    /// <summary>The probe principal's Reader grant at the resource group (the role the policy probes use is Reader by default; the grants always use Reader).</summary>
    private AzureRoleGrant ReaderGrant()
    {
        string scope = AzureScopeRules.NormalizeScope(Target.Scope)!;
        return new AzureRoleGrant(scope, AzureScopeRules.QualifiedRoleDefinitionId(scope, AzurePimPolicyTarget.ReaderRoleId)!, AzureScopeRules.NormalizePrincipalId(Fixture.Settings.AzurePrincipalId)!);
    }

    [Fact]
    public async Task It_Probe_AG1_TheGrantListsCarryScopeRoleAndPrincipal_AndAreNotFilteredByScope()
    {
        Fixture.Require(Needs.AzurePimPolicies);
        var report = new ProbeReport("ag1");
        string scope = AzureScopeRules.NormalizeScope(Target.Scope)!;

        foreach (var (label, path, apiVersion) in new[]
        {
            ("roleEligibilitySchedules", AzureRoleEligibilityLookup.SchedulesPath(scope), AzureRoleEligibilityLookup.SchedulesApiVersion),
            ("roleAssignments", AzureRoleAssignmentLookup.AssignmentsPath(scope), AzureRoleAssignmentLookup.ApiVersion),
        })
        {
            var items = new List<JsonObject>();
            try
            {
                await foreach (var item in Arm.ListAsync(path, apiVersion, null, Ct))
                {
                    items.Add(item);
                }
            }
            catch (GraphException ex)
            {
                report.Expect(false, $"GET {path}?api-version={apiVersion} is refused ({ProbeReport.Describe(ex)}); the provider reads it on every plan. Fix {(label == "roleAssignments" ? AssignmentFile : EligibilityFile)}.");
                continue;
            }

            var scopes = items.Select(i => AzureRoleGrant.PropertiesOf(i).Text("scope") ?? "(absent)").GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Select(g => $"{g.Key} x{g.Count()}");
            report.Observe($"{label} at {scope}: {items.Count} item(s); properties.scope: {string.Join(", ", scopes)}");
            if (label == "roleEligibilitySchedules")
            {
                var memberTypes = items.Select(i => AzureRoleGrant.PropertiesOf(i).Text("memberType") ?? "(absent)").Distinct(StringComparer.OrdinalIgnoreCase);
                report.Observe("memberType values: " + string.Join(", ", memberTypes));
            }

            var incomplete = items.Where(i => AzureRoleGrant.PropertiesOf(i) is var p && (p.Text("scope") is null || p.Text("roleDefinitionId") is null || p.Text("principalId") is null)).ToList();
            report.Expect(
                incomplete.Count == 0,
                $"{incomplete.Count} {label} item(s) lack properties.scope, roleDefinitionId or principalId; the provider matches on all three. Fix {(label == "roleAssignments" ? AssignmentFile : EligibilityFile)}.");
            var foreign = items.Where(i => AzureRoleGrant.PathIn(path, i.Text("id")) is null && AzureScopeRules.ScopeEquals(AzureRoleGrant.PropertiesOf(i).Text("scope"), scope)).ToList();
            report.Expect(
                foreign.Count == 0,
                $"{foreign.Count} {label} item(s) of exactly the scope have an id outside {path}; the provider rebuilds the path from the id and would refuse them. Fix {(label == "roleAssignments" ? AssignmentFile : EligibilityFile)}.");
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_AG2_ARoleAssignmentPutIsIdempotent_AndIsFoundAtTheScope()
    {
        Fixture.Require(Needs.AzurePimPolicies | Needs.AzureRoleGrants);
        var report = new ProbeReport("ag2");
        var grant = ReaderGrant();
        if (await AzureRoleAssignmentLookup.FindAsync(Arm, grant, Ct) is { } existing)
        {
            report.Observe($"the principal already has Reader at {grant.Scope} ({existing.Text("id")}); nothing is written");
            report.Complete();
            return;
        }

        string? created = null;
        try
        {
            var body = AzureRoleAssignmentLookup.CreateBody(grant, null, "earm integration probe AG2");
            var first = await AzureRoleAssignmentLookup.CreateAsync(Arm, grant, body, Ct);
            created = AzureRoleAssignmentLookup.AssignmentPath(grant, first?.Text("id"));
            report.Observe($"PUT: {first?.Text("id") ?? "(no id)"}");
            report.Expect(created is not null, $"the PUT answer's id is not in {AzureRoleAssignmentLookup.AssignmentsPath(grant.Scope)}; the provider adopts by that path. Fix {AssignmentFile}.");

            var again = await AzureRoleAssignmentLookup.CreateAsync(Arm, grant, body, Ct);
            report.Observe($"PUT again: {again?.Text("id") ?? "(no id)"}");
            var found = await EventuallyAsync(() => AzureRoleAssignmentLookup.FindAsync(Arm, grant, Ct), f => f is not null, seconds: 60);
            report.Expect(found is not null, $"the assignment is not listed at {grant.Scope} within 60 s of the PUT; the provider's read-back would miss it. Fix {AssignmentFile}.");
            if (found?.Text("name") is { } name)
            {
                report.Expect(string.Equals(name, AzureRoleAssignmentLookup.AssignmentName(grant), StringComparison.OrdinalIgnoreCase), $"the listed assignment is named {name}, not the deterministic {AzureRoleAssignmentLookup.AssignmentName(grant)}. Fix {AssignmentFile}.");
            }
        }
        catch (GraphException ex)
        {
            report.Expect(false, $"the role assignment PUT failed ({ProbeReport.Describe(ex)}). Fix {AssignmentFile}.");
        }
        finally
        {
            if (created is not null)
            {
                await Arm.DeleteAsync(created, AzureRoleAssignmentLookup.ApiVersion, Ct);
                report.Observe("DELETE " + created + ": done");
            }
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_AG3_AnEligibilityRequestIsListedAsDirect_AndASecondOneIsRoleAssignmentExists()
    {
        Fixture.Require(Needs.AzurePimPolicies | Needs.AzureRoleGrants);
        var report = new ProbeReport("ag3");
        var grant = ReaderGrant();
        if (await AzureRoleEligibilityLookup.FindAsync(Arm, grant, Ct) is { } existing)
        {
            report.Observe($"the principal is already eligible for Reader at {grant.Scope} ({existing.Text("id")}); nothing is written");
            report.Complete();
            return;
        }

        bool assigned = false;
        var schedule = new JsonObject
        {
            ["startDateTime"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
            ["expiration"] = new JsonObject { ["type"] = "AfterDuration", ["duration"] = "P1D" },
        };
        try
        {
            var body = AzureRoleEligibilityLookup.RequestBody(AzureRoleEligibilityLookup.AdminAssign, grant, "earm integration probe AG3", schedule);
            var answer = await AzureRoleEligibilityLookup.SubmitAsync(Arm, grant, body, Ct);
            assigned = true;
            var properties = answer is null ? null : AzureRoleGrant.PropertiesOf(answer);
            report.Observe($"AdminAssign: status {properties?.Text("status") ?? "(absent)"}, targetRoleEligibilityScheduleId {properties?.Text("targetRoleEligibilityScheduleId") ?? "(absent)"}");

            var found = await EventuallyAsync(() => AzureRoleEligibilityLookup.FindAsync(Arm, grant, Ct), f => f is not null, seconds: 90);
            report.Expect(found is not null, $"the eligibility is not listed as a Direct schedule of exactly {grant.Scope} within 90 s; the provider's read-back would miss it. Fix {EligibilityFile}.");
            try
            {
                await AzureRoleEligibilityLookup.SubmitAsync(Arm, grant, body, Ct);
                report.Expect(false, $"a second AdminAssign of the same eligibility was accepted; the provider adopts on RoleAssignmentExists. Fix {EligibilityFile} and AzureRoleEligibilityScheduleRequestsProvider.");
            }
            catch (GraphException ex)
            {
                report.Observe("second AdminAssign: " + ProbeReport.Describe(ex));
                report.Expect(PimScheduleRequests.IsAlreadyExists(ex), $"a second AdminAssign is refused with {ex.Code}, not RoleAssignmentExists; the provider's race adoption would not apply. Fix AzureRoleEligibilityScheduleRequestsProvider.");
            }
        }
        catch (GraphException ex)
        {
            report.Observe("AdminAssign refused: " + ProbeReport.Describe(ex) + " (a Reader policy that forbids a 1-day eligibility refuses it: see AzurePimPolicyProbes)");
        }
        finally
        {
            if (assigned)
            {
                try
                {
                    await AzureRoleEligibilityLookup.SubmitAsync(Arm, grant, AzureRoleEligibilityLookup.RequestBody(AzureRoleEligibilityLookup.AdminRemove, grant, "earm integration probe AG3 cleanup", null), Ct);
                    report.Observe("AdminRemove: done");
                }
                catch (GraphException ex)
                {
                    report.Observe("AdminRemove FAILED, remove the eligibility by hand: " + ProbeReport.Describe(ex));
                }
            }
        }

        report.Complete();
    }
}
