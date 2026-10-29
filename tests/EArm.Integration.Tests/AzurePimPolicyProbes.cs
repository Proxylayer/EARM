using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Verification probes for the open Azure Resource Manager questions of <c>Microsoft.Entra/azureRoleManagementPolicies</c>, the PIM settings of Azure RBAC
/// roles (<c>Microsoft.Authorization/roleManagementPolicies</c>, api-version 2020-10-01), run before the provider is written
/// (<c>EARM_IT_ALLOW_AZURE_PIM_POLICIES=1</c> with <c>EARM_IT_AZURE_SCOPE</c>). A probe that writes only ever writes the policy of the configured role
/// (Reader by default) at the configured resource group, snapshotted first and restored in a <c>finally</c> (<see cref="AzurePimPolicyRestoreGuard"/>).
/// Each probe records what ARM answers (<see cref="ProbeReport"/>; statuses and throttling headers from <see cref="ArmResponseRecorder"/>) and asserts only
/// what the provider design relies on, naming the file to change when it fails. The probes run in method-name order, so AZ5 reads the headers of the
/// calls AZ1 to AZ4 made.
/// </summary>
[Collection(RealTenantCollection.Name)]
[Trait("Category", "Verify")]
[TestMethodOrderer(typeof(MethodNameTestMethodOrderer))]
public sealed partial class AzurePimPolicyProbes : IntegrationTestBase
{
    private const Needs AzureNeeds = Needs.AzurePimPolicies;

    /// <summary>The rule AZ3, AZ4 and AZ6 write: the maximum activation duration of an eligible assignment.</summary>
    private const string ExpirationRule = "Expiration_EndUser_Assignment";

    /// <summary>Where the provider's policy lookup will live (named in the fix hints; the provider is designed from these answers).</summary>
    private const string LookupFile = "src/EArm.Providers/AzureRoleManagementPolicyLookup.cs";

    /// <summary>Where the provider's update will live.</summary>
    private const string ProviderFile = "src/EArm.Providers/AzureRoleManagementPoliciesProvider.cs";

    public AzurePimPolicyProbes(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    private AzurePimPolicyTarget Target => Fixture.Azure.Target;

    private ArmClient Arm => Fixture.Azure.Arm;

    private ArmResponseRecorder Responses => Fixture.Azure.Responses;

    private AzurePimPolicyRestoreGuard Guard => Fixture.Azure.Policies;

    [Fact]
    public async Task It_Probe_AZ1_PolicyAssignmentExistsForTheRoleAtTheResourceGroup()
    {
        Fixture.Require(AzureNeeds);
        var report = new ProbeReport("az1");
        var target = Target;

        // (a) The policy assignments at the resource group, filtered by each form of the role definition id.
        var working = new List<string>();
        var policyNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, roleId) in RoleIdForms(target))
        {
            var (items, refusal) = await ListAsync(target.PolicyAssignmentsPath, AzurePimPolicyTarget.PimApiVersion, AzurePimPolicyRestoreGuard.RoleFilter(roleId));
            if (items is null)
            {
                report.Observe($"assignments filtered by the {label}: REFUSED {refusal}");
                continue;
            }

            var forRole = items.Where(a => AzurePimPolicyRestoreGuard.IsTargetRole(a["properties"].Text("roleDefinitionId"), target)).ToList();
            var atScope = forRole.Where(a => AzurePimPolicyRestoreGuard.IsTargetAssignment(a, target)).ToList();
            report.Observe($"assignments filtered by the {label}: {items.Count} item(s), {forRole.Count} for the role, {atScope.Count} at exactly {target.Scope}");
            foreach (var assignment in forRole)
            {
                report.Observe("  " + DescribeAssignment(assignment, target));
                if (PolicyNameOf(assignment) is { } name)
                {
                    policyNames.Add(name);
                }
            }

            if (atScope.Count > 0)
            {
                working.Add(label);
            }
        }

        report.Observe("role definition id forms that find the assignment at the resource group: " + (working.Count == 0 ? "NONE" : string.Join(", ", working)));
        report.Expect(
            working.Count > 0,
            $"no form of the role definition id finds a policy assignment for role {target.RoleDefinitionId} at {target.Scope}, a resource group the role was never used in with PIM. The provider needs a 'policy not available' path (plan a create-less error, not a PATCH) for a scope without one. Fix {LookupFile} (and docs/pim.md).");
        report.Expect(
            working.Contains(SubscriptionForm),
            $"the subscription-qualified role definition id does not find the assignment (the forms that do: {string.Join(", ", working)}). Fix AzurePimPolicyRestoreGuard.ReadAsync (tests/EArm.Integration.Tests) and {LookupFile} to filter with a form that works.");

        // (b) The policies collection at the resource group, filtered by the same role.
        var (policies, policiesRefusal) = await ListAsync(target.PoliciesPath, AzurePimPolicyTarget.PimApiVersion, AzurePimPolicyRestoreGuard.RoleFilter(target.SubscriptionRoleDefinitionId));
        if (policies is null)
        {
            report.Observe($"policies filtered by the subscription-qualified id: REFUSED {policiesRefusal}");
        }
        else
        {
            report.Observe($"policies filtered by the subscription-qualified id: {policies.Count} item(s)");
            foreach (var policy in policies)
            {
                report.Observe("  " + DescribePolicy(policy));
            }
        }

        // (c) The policy the assignment names, read by name (what the restore guard and the provider write), twice: is its name stable?
        var first = await Guard.ReadAsync(Ct);
        if (first is null)
        {
            report.Observe("the restore guard's lookup (subscription-qualified filter) finds no assignment; the policy read is skipped");
        }
        else
        {
            int rules = AzurePimPolicyRestoreGuard.RulesOf(first.Policy).Count;
            report.Observe($"GET {first.PolicyPath}: {DescribePolicy(first.Policy)}");
            report.Expect(rules > 0, $"GET of the policy {first.PolicyPath} returns no rules: the provider would read no live rule and report every declared rule as missing. Fix {LookupFile} (read the rules another way, for example from the assignment's effectiveRules).");
            var second = await Guard.ReadAsync(Ct);
            string firstName = first.Policy.Text("name") ?? "(none)";
            string secondName = second?.Policy.Text("name") ?? "(none)";
            report.Observe($"policy name across two lookups: {firstName} / {secondName} ({(string.Equals(firstName, secondName, StringComparison.OrdinalIgnoreCase) ? "stable" : "CHANGED")}); names seen in the assignments: {string.Join(", ", policyNames)}");
            report.Observe($"policy properties.scope {first.Policy["properties"].Text("scope") ?? "(absent)"} (the configured scope is {target.Scope})");
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_AZ2_RoleNameResolvesThroughRoleDefinitionsNotThePolicyFilter()
    {
        Fixture.Require(AzureNeeds);
        var report = new ProbeReport("az2");
        var target = Target;

        // The role's name, read by id, so the probe works for any configured role (Reader by default).
        string? roleName = null;
        try
        {
            var definition = await Arm.GetAsync(target.SubscriptionRoleDefinitionId, AzurePimPolicyTarget.RoleDefinitionApiVersion, Ct);
            roleName = definition?["properties"].Text("roleName");
            report.Observe($"GET {target.SubscriptionRoleDefinitionId}: roleName '{roleName ?? "(absent)"}', type {definition?["properties"].Text("type") ?? "(absent)"}");
        }
        catch (GraphException ex)
        {
            report.Expect(false, $"the role definition {target.SubscriptionRoleDefinitionId} cannot be read ({ProbeReport.Describe(ex)}); the provider cannot show or check a role's name. Fix {LookupFile}.");
        }

        if (roleName is not null)
        {
            string filter = $"roleName eq '{roleName.Replace("'", "''", StringComparison.Ordinal)}'";
            var (definitions, refusal) = await ListAsync(target.RoleDefinitionsPath, AzurePimPolicyTarget.RoleDefinitionApiVersion, filter);
            if (definitions is null)
            {
                report.Expect(false, $"GET {target.RoleDefinitionsPath}?$filter={filter} is refused ({refusal}); the provider must list the role definitions and match roleName itself. Fix {LookupFile}.");
            }
            else
            {
                var ids = definitions.Select(d => d.Text("name") ?? "(no name)").ToList();
                report.Observe($"role definitions with {filter}: {definitions.Count} ({string.Join(", ", ids)})");
                report.Expect(
                    ids.Contains(target.RoleDefinitionId, StringComparer.OrdinalIgnoreCase),
                    $"the roleName filter does not return role {target.RoleDefinitionId} for '{roleName}' (it returned {string.Join(", ", ids)}); the provider must resolve role names by listing the definitions and matching roleName itself. Fix {LookupFile}.");
            }

            // Does the policy assignment filter take a role name at all (expected not: the provider then resolves names itself)?
            foreach (string nameFilter in new[] { AzurePimPolicyRestoreGuard.RoleFilter(roleName), filter })
            {
                var (items, nameRefusal) = await ListAsync(target.PolicyAssignmentsPath, AzurePimPolicyTarget.PimApiVersion, nameFilter);
                report.Observe(items is null
                    ? $"policy assignments with $filter={nameFilter}: REFUSED {nameRefusal}"
                    : $"policy assignments with $filter={nameFilter}: ACCEPTED, {items.Count} item(s), {items.Count(a => AzurePimPolicyRestoreGuard.IsTargetAssignment(a, target))} for the role at the scope");
            }
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_AZ5_ThrottlingHeadersAreRecorded()
    {
        Fixture.Require(AzureNeeds);
        var report = new ProbeReport("az5");
        if (Responses.Count == 0)
        {
            // Run alone: one read so there is something to look at.
            await Guard.ReadAsync(Ct);
        }

        var exchanges = Responses.All;
        report.Observe($"{exchanges.Count} Azure Resource Manager response(s) recorded this run: {string.Join(", ", exchanges.GroupBy(e => $"{e.Method} {e.Status}").Select(g => $"{g.Key} x{g.Count()}"))}");
        foreach (var header in exchanges.SelectMany(e => e.Headers.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (header.Equals("x-ms-request-id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var byMethod in exchanges.Where(e => e.Headers.ContainsKey(header)).GroupBy(e => e.Method))
            {
                var values = byMethod.Select(e => e.Headers[header]).ToList();
                var numbers = values.Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : (int?)null).OfType<int>().ToList();
                report.Observe($"{header} on {byMethod.Key}: {values.Count} response(s), first {values[0]}, last {values[^1]}{(numbers.Count > 0 ? $", lowest {numbers.Min()}" : string.Empty)}");
            }
        }

        int withRequestId = exchanges.Count(e => e.Headers.ContainsKey("x-ms-request-id"));
        report.Observe($"x-ms-request-id on {withRequestId} of {exchanges.Count} response(s); 429 answers: {exchanges.Count(e => e.Status == 429)}; Retry-After on {exchanges.Count(e => e.Headers.ContainsKey("Retry-After"))}");
        report.Complete();
    }

    // ---- helpers ----

    private const string SubscriptionForm = "subscription-qualified id";

    /// <summary>The forms of the role definition id AZ1 filters with.</summary>
    private static IEnumerable<(string Label, string RoleId)> RoleIdForms(AzurePimPolicyTarget target)
    {
        yield return (SubscriptionForm, target.SubscriptionRoleDefinitionId);
        yield return ("resource-group-qualified id", target.ScopedRoleDefinitionId);
        yield return ("bare GUID", target.RoleDefinitionId);
    }

    /// <summary>Every item of a collection, or null and the error when ARM refuses the read.</summary>
    private async Task<(List<JsonObject>? Items, string? Refusal)> ListAsync(string path, string apiVersion, string? filter)
    {
        var items = new List<JsonObject>();
        try
        {
            await foreach (var item in Arm.ListAsync(path, apiVersion, filter, Ct))
            {
                items.Add(item);
            }

            return (items, null);
        }
        catch (GraphException ex)
        {
            return (null, ProbeReport.Describe(ex));
        }
    }

    /// <summary>The statuses (and request ids) of the responses recorded since <paramref name="mark"/>.</summary>
    private string DescribeResponses(int mark)
    {
        var exchanges = Responses.Since(mark);
        return exchanges.Count == 0
            ? "no response recorded"
            : string.Join(", ", exchanges.Select(e => $"{e.Method} {e.Status} (x-ms-request-id {e.Headers.GetValueOrDefault("x-ms-request-id") ?? "n/a"})"));
    }

    private static string? PolicyNameOf(JsonObject assignment)
    {
        string? policyId = assignment["properties"].Text("policyId");
        return policyId?[(policyId.LastIndexOf('/') + 1)..];
    }

    private static string DescribeAssignment(JsonObject assignment, AzurePimPolicyTarget target)
    {
        var properties = assignment["properties"] as JsonObject;
        string? scope = properties.Text("scope");
        string where = scope is null ? "(absent)" : string.Equals(scope.TrimEnd('/'), target.Scope, StringComparison.OrdinalIgnoreCase) ? "the resource group" : "INHERITED from " + scope;
        string effective = properties?["effectiveRules"] is JsonArray rules ? rules.Count.ToString(CultureInfo.InvariantCulture) + " effectiveRules" : "no effectiveRules";
        string assignmentProperties = properties?["policyAssignmentProperties"] is JsonObject extra
            ? "policyAssignmentProperties {" + string.Join(", ", extra.Select(p => p.Key)) + "}" + (extra["policy"]?["lastModifiedDateTime"] is { } modified ? $" (policy lastModifiedDateTime {modified})" : string.Empty)
            : "no policyAssignmentProperties";
        return $"assignment {assignment.Text("name") ?? "(no name)"}: scope {where}, roleDefinitionId {properties.Text("roleDefinitionId") ?? "(absent)"}, policyId {(properties.Text("policyId") is null ? "ABSENT" : "present, policy " + PolicyNameOf(assignment))}, {effective}, {assignmentProperties}";
    }

    private static string DescribePolicy(JsonObject policy)
    {
        var properties = policy["properties"];
        string rules = properties?["rules"] is JsonArray array ? array.Count.ToString(CultureInfo.InvariantCulture) : "no";
        string effective = properties?["effectiveRules"] is JsonArray effectiveRules ? effectiveRules.Count.ToString(CultureInfo.InvariantCulture) : "no";
        return $"policy {policy.Text("name") ?? "(no name)"}: properties.scope {properties.Text("scope") ?? "(absent)"}, {rules} rules, {effective} effectiveRules, isOrganizationDefault {properties?["isOrganizationDefault"]?.ToJsonString() ?? "(absent)"}, lastModifiedDateTime {properties.Text("lastModifiedDateTime") ?? "(absent)"}";
    }
}
