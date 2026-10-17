using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/roleManagementPolicies</c>: the PIM policy (role settings) of a directory role or of a PIM for Groups membership / ownership, shaped like
/// ARM's <c>Microsoft.Authorization/roleManagementPolicies</c> (<c>properties.rules[]</c>). The resource name is a deterministic GUID (a label); the policy is
/// named by its assignment (<c>scopeType</c>, <c>scopeId</c>, <c>roleDefinitionId</c>) or the sugar <c>role</c> (→ DirectoryRole, '/', the role template id)
/// or <c>group</c> + <c>accessId</c> (→ Group, the group's id, member | owner), and read live on every run.
/// <list type="bullet">
/// <item><b>Read</b>: the policy assignment of the scope with its policy and rules (<see cref="RoleManagementPolicyLookup"/>); the policy id is the Graph id.</item>
/// <item><b>Plan</b>: only the declared rule ids are managed (live-only rules are left alone); each declared rule is filled from the live rule
/// (<see cref="RoleManagementPolicyShape.FillFromLive"/>) and compared in ARM's shape. A declared rule id the live policy does not have is an error.</item>
/// <item><b>Create</b>: never (every policy exists, Graph creates none). A directory role without a policy is <see cref="EngineDiagnosticCodes.PimPolicyNotAvailable"/>;
/// so is a group PIM for Groups has not onboarded, unless a schedule request of the same deployment onboards it: then the policy is deferred to the next
/// run (an Ignore row with a <see cref="EngineDiagnosticCodes.PimPolicyNotAvailable"/> warning) and the requests go ahead. A group policy that opts in
/// (<c>properties.onboarding.temporaryEligiblePrincipalId</c>) is a Create row instead: the create onboards the group with a temporary eligibility for that
/// principal, removes it, and applies the rules to the policy listed afterwards (<see cref="GroupPolicyOnboarding"/>).</item>
/// <item><b>Update</b>: <c>PATCH /policies/roleManagementPolicies/{policyId}/rules/{ruleId}</c> per changed rule, with its <c>@odata.type</c>. A group's
/// first rule PATCH onboards the group, which re-ids its policy (Learn): a rule PATCH answered 404 re-reads the policy id and is sent once more.</item>
/// <item><b>Delete</b>: refused (<see cref="IDeleteRefusalSource"/>, <see cref="EngineDiagnosticCodes.NotDeletable"/>): a policy cannot be deleted; revert its
/// rules explicitly. Complete mode lists no orphans.</item>
/// </list>
/// Order: a policy is applied before the schedule requests of the same role or group the template declares (<see cref="IApplyOrderSource"/>), so they are
/// judged by the declared rules.
/// </summary>
[EntraProvider(ResourceType)]
public sealed partial class RoleManagementPoliciesProvider : ProviderBase, IDeleteRefusalSource, IOrphanCandidateSource, IApplyOrderSource
{
    public const string ResourceType = RoleManagementPolicyRules.ResourceType;

    private const string InvalidCode = EngineDiagnosticCodes.RoleManagementPolicyInvalid;

    /// <summary>The group policies a schedule request of the same deployment onboards (found by <see cref="AppliesBefore"/>).</summary>
    private readonly ConditionalWeakTable<ResourceInstance, object> _onboardedThisRun = new();

    /// <summary>The onboarding opt-in of each planned declaration (<c>properties.onboarding</c>, taken out of the properties by <see cref="Plan"/>).</summary>
    private readonly ConditionalWeakTable<ResourceInstance, OnboardingRequest> _onboardingRequests = new();

    private readonly GroupPolicyOnboarding _onboarding;

    public RoleManagementPoliciesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : this(definition, catalog, null)
    {
    }

    /// <param name="definition">The catalog definition of the type.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="settle">Paces the waits of a group onboarding (<see cref="GroupPolicyOnboarding"/>); <see cref="ReplicationWait.Default"/> when null.</param>
    public RoleManagementPoliciesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, ReplicationWait? settle)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(RoleManagementPoliciesProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }

        _onboarding = new GroupPolicyOnboarding(settle ?? ReplicationWait.Default, TimeProvider.System);
    }

    public RoleManagementPoliciesProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>Rules change in place; nothing is ever recreated.</summary>
    protected override bool RecreateOnImmutableChange => false;

    /// <summary>Nothing is created.</summary>
    public override bool CreateIsRepeatable => false;

    // ---- permissions ----

    /// <summary>
    /// RoleManagementPolicy.ReadWrite.Directory for a directory role policy, RoleManagementPolicy.ReadWrite.AzureADGroup for a group policy (both when the
    /// scope is computed), PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup for a group policy that opts in to onboarding (the temporary eligibility),
    /// and Directory.Read.All for the role and group lookups.
    /// </summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        var properties = desired?.Properties ?? desired?.Declaration.Properties as JsonObject;
        string? kind = properties is null ? null : RoleManagementPolicyRules.ScopeKind(properties, IsOpaqueOrExpression);
        var permissions = new List<string>();
        if (kind != RoleManagementPolicyRules.GroupScope)
        {
            permissions.Add("RoleManagementPolicy.ReadWrite.Directory");
        }

        if (kind != RoleManagementPolicyRules.DirectoryRoleScope)
        {
            permissions.Add("RoleManagementPolicy.ReadWrite.AzureADGroup");
            // The declaration too: Plan takes the opt-in out of the evaluated properties.
            if ((properties is not null && Get(properties, RoleManagementPolicyRules.OnboardingProperty) is not null)
                || (desired?.Declaration.Properties is JsonObject declared && Get(declared, RoleManagementPolicyRules.OnboardingProperty) is not null))
            {
                permissions.Add("PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup");
            }
        }

        permissions.Add("Directory.Read.All");
        return permissions;
    }

    // ---- order ----

    /// <summary>The schedule requests of the same role or group the template declares are applied after the policy (<see cref="RoleManagementPolicyApplyOrder"/>).</summary>
    public IEnumerable<ResourceInstance> AppliesBefore(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(declared);
        var requests = RoleManagementPolicyApplyOrder.GovernedRequests(instance, declared);
        if (requests.Count > 0 && instance.Declaration.Properties is JsonObject properties
            && RoleManagementPolicyRules.ScopeKind(properties, IsOpaqueOrExpression) == RoleManagementPolicyRules.GroupScope)
        {
            _onboardedThisRun.AddOrUpdate(instance, requests);
        }

        return requests;
    }

    // ---- prepare (sugar + live validation) ----

    public override async Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before preparing.");
        if (RoleManagementPolicyRules.Check(properties, IsOpaque) is { Count: > 0 } findings)
        {
            throw Invalid(desired, string.Join("; ", findings.Select(f => f.Message)));
        }

        switch (RoleManagementPolicyRules.ScopeKind(properties, IsOpaque))
        {
            case RoleManagementPolicyRules.DirectoryRoleScope:
                await PrepareRoleAsync(desired, properties, graph, cancellationToken).ConfigureAwait(false);
                break;
            case RoleManagementPolicyRules.GroupScope:
                await PrepareGroupAsync(desired, properties, graph, cancellationToken).ConfigureAwait(false);
                break;
        }

        Normalize(properties);
    }

    private static async Task PrepareRoleAsync(ResourceInstance desired, JsonObject properties, IGraphClient graph, CancellationToken cancellationToken)
    {
        if (Text(properties, "role") is { } role)
        {
            var resolved = await RoleAssignmentsLookup.ResolveRoleAsync(graph, role, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            Set(properties, "roleDefinitionId", JsonValue.Create(resolved.Id ?? throw Invalid(desired, resolved.Error!)));
            Remove(properties, "role");
        }
        else if (Unknown(properties, "role") is { } unknownRole)
        {
            Set(properties, "roleDefinitionId", unknownRole);
            Remove(properties, "role");
        }

        Set(properties, "scopeType", JsonValue.Create(RoleManagementPolicyRules.DirectoryRoleScope));
        Set(properties, "scopeId", JsonValue.Create(RoleManagementPolicyRules.RootScopeId));
    }

    private static async Task PrepareGroupAsync(ResourceInstance desired, JsonObject properties, IGraphClient graph, CancellationToken cancellationToken)
    {
        string? declaredGroup = Text(properties, "group") ?? Text(properties, "scopeId");
        if (declaredGroup is not null)
        {
            var found = await GroupSchedulesLookup.ReadGroupAsync(graph, declaredGroup, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                ?? throw Invalid(desired, $"no group with uniqueName or id '{declaredGroup}' exists in the tenant; deploy a Microsoft.Entra/groups resource for it (and reference it), or name an existing group.");
            if (GroupSchedulesLookup.UnsupportedGroupReason(found) is { } reason)
            {
                throw Invalid(desired, reason);
            }

            Set(properties, "scopeId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(JsonObjects.GetId(found) ?? throw Invalid(desired, $"the group '{declaredGroup}' was read without an id."))));
            Remove(properties, "group");
        }
        else if (Unknown(properties, "group") is { } unknownGroup)
        {
            Set(properties, "scopeId", unknownGroup);
            Remove(properties, "group");
        }

        if (Get(properties, "accessId") is { } access)
        {
            Set(properties, "roleDefinitionId", JsonValues.Clone(access));
            Remove(properties, "accessId");
        }

        Set(properties, "scopeType", JsonValue.Create(RoleManagementPolicyRules.GroupScope));
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        var scope = ScopeOf(key, null);
        var assignment = await RoleManagementPolicyLookup.FindAsync(graph, scope, key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        return assignment is null ? null : ToExisting(assignment, scope);
    }

    // ---- delete ----

    /// <summary>Never deletes: Graph cannot delete a PIM policy (<see cref="EngineDiagnosticCodes.NotDeletable"/>).</summary>
    public override Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        throw new ProviderException(EngineDiagnosticCodes.NotDeletable, $"{TypeName} {key.Describe()}: {RefusalText}");
    }

    public string? DescribeDeleteRefusal(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        return RefusalText;
    }

    private const string RefusalText = "a PIM role management policy cannot be deleted (every role and onboarded group always has one), so it is kept with its current rules; to undo a setting, declare the rule with the value to restore and deploy";

    // ---- orphan report ----

    /// <summary>Policies always exist and are never created, so none can be an orphan: the listing is empty (no Graph call).</summary>
    public OrphanScope OrphanScope => OrphanScope.Collection;

    public Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        return Task.FromResult<IReadOnlyList<OrphanCandidate>>([]);
    }

    /// <summary>A declaration's onboarding opt-in: the principal of the temporary eligibility (<c>null</c> while it is only known at deployment).</summary>
    private sealed record OnboardingRequest(string? PrincipalId);

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType) ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }
}
