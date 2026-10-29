using System;
using System.Collections.Generic;
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
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/azureRoleManagementPolicies</c>: the PIM policy (role settings) of an Azure RBAC role at an Azure scope, managed through Azure Resource
/// Manager (<c>Microsoft.Authorization/roleManagementPolicies</c>, api-version <see cref="AzureRoleManagementPolicyLookup.PolicyApiVersion"/>): the Azure twin
/// of <see cref="RoleManagementPoliciesProvider"/>, whose declared rule shape is already ARM's, so the rules are compared and sent as declared (no Graph
/// mapping). The resource name is a deterministic GUID (a label); the policy is named by <c>scope</c> (a management group, a subscription, a resource group or a
/// resource) and <c>roleDefinitionId</c> (normalised to the subscription-qualified id, or the tenant-level id at a management group) or the sugar <c>role</c>
/// (a role name, resolved at plan time), and read live on every run.
/// <list type="bullet">
/// <item><b>Read</b>: the policy assignment of the role AT the scope, then its policy (<see cref="AzureRoleManagementPolicyLookup"/>); the policy's resource path is the Graph id.</item>
/// <item><b>Plan</b>: only the declared rule ids are managed (live-only rules are left alone); each declared rule is filled from the live rule
/// (<see cref="RoleManagementPolicyShape.FillFromLive"/>: ARM resets a member a PATCHed rule leaves out) and compared in the shared comparable shape. A
/// declared rule id the live policy does not have is an error listing the policy's ids. Unchanged rules are decided here, never sent (ARM bumps the policy's
/// lastModifiedDateTime even for a PATCH that changes nothing).</item>
/// <item><b>Create</b>: never. ARM has a policy for every role at every subscription, resource group and resource scope (a Reader policy exists at a
/// brand-new resource group), so a missing one is <see cref="EngineDiagnosticCodes.PimPolicyNotAvailable"/>. At a management group ARM lists a role's
/// policy only once the first eligibility or assignment request of that role exists there: when an <c>azureRoleEligibilityScheduleRequests</c> of the same
/// scope and role is declared in the deployment, the policy is deferred to the end of the run (an Ignore row marked
/// <see cref="ResourceChange.RetryAtEndOfRun"/>, with a <see cref="EngineDiagnosticCodes.PimPolicyNotAvailable"/> warning) and planned again once the
/// request has run; otherwise it is <see cref="EngineDiagnosticCodes.PimPolicyNotAvailable"/>, naming the cause and the fix.</item>
/// <item><b>Update</b>: ONE <c>PATCH {policyId}</c> of <c>properties.rules</c> carrying only the changed declared rules, each complete; ARM leaves the other
/// rules untouched and answers with the whole policy, which is the re-read.</item>
/// <item><b>Delete</b>: refused (<see cref="IDeleteRefusalSource"/>, <see cref="EngineDiagnosticCodes.NotDeletable"/>): a policy cannot be deleted; revert
/// its rules explicitly. Complete mode lists no orphans.</item>
/// </list>
/// Calls go to Azure Resource Manager (<see cref="IResourceManagerProvider"/>), through the run's <see cref="ArmClient"/> that
/// <see cref="ProviderDiscovery.CreateRegistry(ResourceTypeCatalog, ReplicationWait, ArmClient)"/> passes in; the offline registries have none, and there any
/// Resource Manager call fails with an <see cref="InvalidOperationException"/> (validation and permissions need none). Order: a policy is applied before the
/// <c>azureRoleEligibilityScheduleRequests</c> of the same scope and role the template declares (<see cref="IApplyOrderSource"/>,
/// <see cref="AzureRoleManagementPolicyApplyOrder"/>), so they are judged by the declared rules.
/// </summary>
[EntraProvider(ResourceType)]
public sealed partial class AzureRoleManagementPoliciesProvider : ProviderBase, IResourceManagerProvider, IDeleteRefusalSource, IOrphanCandidateSource, IApplyOrderSource
{
    public const string ResourceType = AzureRoleManagementPolicyRules.ResourceType;

    private const string InvalidCode = EngineDiagnosticCodes.RoleManagementPolicyInvalid;

    private const string RefusalText = "an Azure PIM role management policy cannot be deleted (every role has one at every scope), so it is kept with its current rules; to undo a setting, declare the rule with the value to restore and deploy";

    private readonly ArmClient? _resourceManager;

    /// <summary>The policies an eligibility request of the same deployment requests the role of (found by <see cref="AppliesBefore"/>).</summary>
    private readonly ConditionalWeakTable<ResourceInstance, object> _requestedThisRun = new();

    /// <param name="definition">The catalog definition of the type.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="resourceManager">The run's Azure Resource Manager client; <c>null</c> in an offline registry (validate, permissions).</param>
    public AzureRoleManagementPoliciesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, ArmClient? resourceManager)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(AzureRoleManagementPoliciesProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }

        _resourceManager = resourceManager;
    }

    /// <summary>A provider without a Resource Manager client (offline: it validates and plans, and fails any Resource Manager call).</summary>
    public AzureRoleManagementPoliciesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : this(definition, catalog, null)
    {
    }

    public AzureRoleManagementPoliciesProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog, null)
    {
    }

    public AzureRoleManagementPoliciesProvider(ResourceTypeCatalog catalog, ArmClient? resourceManager)
        : this(RequireDefinition(catalog), catalog, resourceManager)
    {
    }

    /// <summary>Rules change in place; nothing is ever recreated.</summary>
    protected override bool RecreateOnImmutableChange => false;

    /// <summary>Nothing is created.</summary>
    public override bool CreateIsRepeatable => false;

    /// <summary>The Resource Manager client of an online run.</summary>
    private ArmClient ResourceManager => _resourceManager
        ?? throw new InvalidOperationException($"{TypeName} calls Azure Resource Manager, but this provider registry was built without a Resource Manager client (an offline registry); build it with ProviderDiscovery.CreateRegistry(catalog, settle, resourceManager).");

    // ---- permissions ----

    /// <summary>No Microsoft Graph permission: the policy is written through Azure Resource Manager, an Azure RBAC grant at the scope (see <c>earm permissions</c>).</summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        return [];
    }

    /// <summary>The Azure RBAC roles that may write the policy at its scope (<c>Microsoft.Authorization/roleManagementPolicies/write</c>), for the what-if footer.</summary>
    public IReadOnlyList<string> RequiredAzureRoles(ResourceInstance? desired)
    {
        var scope = desired?.Properties is { } properties && JsonObjects.GetString(properties, "scope") is { } declared
            ? AzureRoleManagementPolicyRules.NormalizeScope(declared) ?? declared
            : "the declared scope";
        return [$"Owner, User Access Administrator or Role Based Access Control Administrator at '{scope}'"];
    }

    // ---- apply order ----

    /// <summary>
    /// The <c>azureRoleEligibilityScheduleRequests</c> of the same scope and role (applied after the policy, so they are judged by the declared rules); a
    /// policy that governs one is remembered, so a management group policy ARM does not list yet waits for the end of the run instead of failing.
    /// </summary>
    public IEnumerable<ResourceInstance> AppliesBefore(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(declared);
        var requests = AzureRoleManagementPolicyApplyOrder.GovernedRequests(instance, declared);
        if (requests.Count > 0)
        {
            _requestedThisRun.AddOrUpdate(instance, requests);
        }

        return requests;
    }

    // ---- prepare (sugar + live validation) ----

    /// <summary>Checks the declaration, resolves the <c>role</c> sugar to its role definition (at the scope) and normalises the scope and role definition id.</summary>
    public override async Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before preparing.");
        if (Findings(properties) is { Length: > 0 } findings)
        {
            throw Invalid(desired, findings);
        }

        if (Text(properties, "role") is { } role)
        {
            if (Text(properties, "scope") is { } declaredScope && AzureRoleManagementPolicyRules.NormalizeScope(declaredScope) is { } scope)
            {
                string id = AzureRoleManagementPolicyRules.RoleDefinitionGuid(role)?.ToString("D")
                    ?? await ResolveRoleAsync(desired, scope, role, cancellationToken).ConfigureAwait(false);
                Set(properties, "roleDefinitionId", JsonValue.Create(id));
                Remove(properties, "role");
            }
            else if (Unknown(properties, "scope") is { } unknownScope)
            {
                // The role is resolved at its scope, which is only known at deployment: so is the role definition id.
                Set(properties, "roleDefinitionId", unknownScope);
                Remove(properties, "role");
            }
        }
        else if (Unknown(properties, "role") is { } unknownRole)
        {
            Set(properties, "roleDefinitionId", unknownRole);
            Remove(properties, "role");
        }

        Normalize(properties);
    }

    private async Task<string> ResolveRoleAsync(ResourceInstance desired, string scope, string role, CancellationToken cancellationToken)
    {
        var (id, error) = await AzureRoleManagementPolicyLookup.ResolveRoleAsync(ResourceManager, scope, role, cancellationToken).ConfigureAwait(false);
        return id ?? throw Invalid(desired, error!);
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        var scope = ScopeOf(key, null);
        var policy = await AzureRoleManagementPolicyLookup.FindAsync(ResourceManager, scope, cancellationToken).ConfigureAwait(false);
        return policy is null ? null : ToExisting(policy, scope);
    }

    // ---- delete ----

    /// <summary>Never deletes: ARM cannot delete a PIM policy (<see cref="EngineDiagnosticCodes.NotDeletable"/>).</summary>
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

    // ---- orphan report ----

    /// <summary>Policies always exist and are never created, so none can be an orphan: the listing is empty (no call).</summary>
    public OrphanScope OrphanScope => OrphanScope.Collection;

    public Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        return Task.FromResult<IReadOnlyList<OrphanCandidate>>([]);
    }

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType) ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }
}
