using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/azureRoleAssignments</c>: a permanent, active Azure RBAC role assignment at an Azure scope (a management group, a subscription, a
/// resource group or a resource), made through Azure Resource Manager (<c>Microsoft.Authorization/roleAssignments</c>, api-version
/// <see cref="AzureRoleAssignmentLookup.ApiVersion"/>): the Azure twin of <see cref="RoleAssignmentsProvider"/>. The resource name is a deterministic GUID (a
/// label); the assignment is found by (<c>scope</c>, <c>roleDefinitionId</c> or the sugar <c>role</c>, <c>principalId</c>) and read live on every run
/// (<see cref="AzureRoleAssignmentLookup"/>).
/// <list type="bullet">
/// <item><b>Plan</b>: an assignment of the principal at exactly the scope, under any name (the portal's, PIM's active assignments, another tool's), is
/// adopted (<see cref="ChangeType.NoChange"/>); an inherited one does not count. A missing one is a Create.</item>
/// <item><b>Create</b>: <c>PUT {scope}/providers/Microsoft.Authorization/roleAssignments/{guid(scope, roleDefinitionId, principalId)}</c> with
/// <c>principalType</c> and <c>description</c> when declared (both create-time only: never compared). <c>RoleAssignmentExists</c> (the grant made under
/// another name since the plan) is adopted after a re-read.</item>
/// <item><b>Delete</b> (<c>earm deployment delete</c>; incremental and complete deployments never delete one): <c>DELETE</c> of the matched assignment,
/// as the Entra twin; one already gone is not an error.</item>
/// </list>
/// Not listed by the orphan report.
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AzureRoleAssignmentsProvider : AzureRoleGrantProvider
{
    public const string ResourceType = AzureRoleAssignmentRules.ResourceType;

    /// <param name="definition">The catalog definition of the type.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="resourceManager">The run's Azure Resource Manager client; <c>null</c> in an offline registry (validate, permissions).</param>
    public AzureRoleAssignmentsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, ArmClient? resourceManager)
        : base(definition, catalog, ResourceType, resourceManager)
    {
    }

    /// <summary>A provider without a Resource Manager client (offline: it validates and plans, and fails any Resource Manager call).</summary>
    public AzureRoleAssignmentsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : this(definition, catalog, null)
    {
    }

    public AzureRoleAssignmentsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog, null)
    {
    }

    public AzureRoleAssignmentsProvider(ResourceTypeCatalog catalog, ArmClient? resourceManager)
        : this(RequireDefinition(catalog), catalog, resourceManager)
    {
    }

    /// <summary>The name of a created assignment is derived from its key, so a repeated create is the same idempotent PUT.</summary>
    public override bool CreateIsRepeatable => true;

    protected override string InvalidCode => EngineDiagnosticCodes.RoleAssignmentInvalid;

    protected override string NameExample => "[guid(subscription().id, 'Reader', parameters('groupName'))]";

    protected override string Noun => "Azure role assignment";

    protected override IReadOnlyList<PimRequestFinding> Check(JsonObject properties)
    {
        return AzureRoleAssignmentRules.Check(properties, IsOpaque);
    }

    /// <summary>The Azure RBAC roles that may assign the role at its scope (<c>Microsoft.Authorization/roleAssignments/write</c>), for the what-if footer.</summary>
    public override IReadOnlyList<string> RequiredAzureRoles(ResourceInstance? desired)
    {
        return [$"Owner, User Access Administrator or Role Based Access Control Administrator at '{DeclaredScope(desired)}' (Azure role assignment)"];
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        var grant = GrantOf(key, null);
        var assignment = await AzureRoleAssignmentLookup.FindAsync(ResourceManager, grant, cancellationToken).ConfigureAwait(false);
        return assignment is null ? null : ToExisting(grant, assignment);
    }

    // ---- plan ----

    protected override IEnumerable<string> CreateReasons(JsonObject properties)
    {
        return ["assigned with PUT {scope}/providers/Microsoft.Authorization/roleAssignments/{guid(scope, roleDefinitionId, principalId)}"];
    }

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before creating.");
        var key = ResolveKey(desired);
        var grant = GrantOf(key, desired.Id);
        var body = AzureRoleAssignmentLookup.CreateBody(grant, AzureRoleAssignmentRules.NormalizePrincipalType(Text(properties, "principalType")), Text(properties, "description"));
        JsonObject? live;
        try
        {
            live = await AzureRoleAssignmentLookup.CreateAsync(ResourceManager, grant, body, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (PimScheduleRequests.IsAlreadyExists(ex))
        {
            // RoleAssignmentExists: the grant was made under another name since this create was planned. Adopt it.
            live = await AzureRoleAssignmentLookup.FindAsync(ResourceManager, grant, cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                throw;
            }
        }

        if (live is null || AzureRoleAssignmentLookup.AssignmentPath(grant, JsonObjects.GetId(live)) is null)
        {
            live = await AzureRoleAssignmentLookup.FindAsync(ResourceManager, grant, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the role assignment PUT returned no assignment of {grant.Scope} and it could not be read back.", desired.Id);
        }

        var existing = ToExisting(grant, live);
        return new AppliedResource(desired.Id, existing.GraphId, existing.Raw, key);
    }

    // ---- delete ----

    /// <summary><c>DELETE</c> of the matched assignment (its path rebuilt in the scope's collection); one already gone is not an error.</summary>
    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        var grant = GrantOf(key, null);
        string path = AzureRoleAssignmentLookup.AssignmentPath(grant, live.GraphId)
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"{TypeName} {key.Describe()}: the assignment '{live.GraphId}' is not in {AzureRoleAssignmentLookup.AssignmentsPath(grant.Scope)}; EARM never deletes an assignment of another scope.");
        try
        {
            await ResourceManager.DeleteAsync(path, AzureRoleAssignmentLookup.ApiVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone (removed outside EARM).
        }
    }

    // ---- helpers ----

    private ExistingResource ToExisting(AzureRoleGrant grant, JsonObject assignment)
    {
        string graphId = AzureRoleAssignmentLookup.AssignmentPath(grant, JsonObjects.GetId(assignment))
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"{TypeName}: the role assignment of {grant.Describe()} is not in {AzureRoleAssignmentLookup.AssignmentsPath(grant.Scope)} (its id is '{JsonObjects.GetId(assignment)}').");
        var properties = AzureRoleGrant.PropertiesOf(assignment);
        var view = new JsonObject
        {
            ["scope"] = grant.Scope,
            ["roleDefinitionId"] = grant.RoleDefinitionId,
            ["principalId"] = grant.PrincipalId,
            ["principalType"] = JsonObjects.GetString(properties, "principalType"),
            ["description"] = JsonObjects.GetString(properties, "description"),
        };
        return new ExistingResource(graphId, view, (JsonObject)JsonValues.Clone(assignment)!);
    }

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType) ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }
}
