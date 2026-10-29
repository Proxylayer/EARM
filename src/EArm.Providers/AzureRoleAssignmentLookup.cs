using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Expressions.Functions;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// The Azure Resource Manager side of <see cref="AzureRoleAssignmentsProvider"/> (api-version <see cref="ApiVersion"/>). An assignment is found with
/// <c>GET {scope}/providers/Microsoft.Authorization/roleAssignments</c> (every page: <c>nextLink</c> is followed; no <c>$filter</c>, the list is matched
/// client-side), keeping only the assignment whose <c>properties.scope</c> IS the declared scope and whose role and principal are the grant's, whatever its
/// name (one made in the portal, by PIM or by another tool is adopted). A missing one is created with <c>PUT {scope}/providers/Microsoft.Authorization/roleAssignments/{name}</c>,
/// the name a deterministic GUID of the grant (<see cref="AssignmentName"/>), and removed with <c>DELETE</c> of the matched assignment. Never cached.
/// </summary>
public static class AzureRoleAssignmentLookup
{
    /// <summary>The api-version of <c>roleAssignments</c>.</summary>
    public const string ApiVersion = "2022-04-01";

    /// <summary><c>{scope}/providers/Microsoft.Authorization/roleAssignments</c>.</summary>
    public static string AssignmentsPath(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return scope + AzureScopeRules.AuthorizationProvider + "/roleAssignments";
    }

    /// <summary>
    /// The name a created assignment gets: ARM's <c>guid()</c> of the scope, the role definition id and the principal id
    /// (<c>guid('{scope}', '{roleDefinitionId}', '{principalId}')</c> with the canonical values), so a repeated create is the same PUT.
    /// </summary>
    public static string AssignmentName(AzureRoleGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return Hashing.GuidV5(Hashing.GuidNamespace, string.Join("-", grant.Scope, grant.RoleDefinitionId, grant.PrincipalId)).ToString("D");
    }

    /// <summary>The assignment of <paramref name="grant"/> AT its scope, under any name, or <c>null</c> (also when the scope does not exist: ARM answers 404).</summary>
    public static async Task<JsonObject?> FindAsync(ArmClient arm, AzureRoleGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(grant);
        try
        {
            await foreach (var assignment in arm.ListAsync(AssignmentsPath(grant.Scope), ApiVersion, null, cancellationToken).ConfigureAwait(false))
            {
                if (grant.Matches(assignment))
                {
                    return assignment;
                }
            }
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // A scope that does not exist (a resource group not created yet) has no assignment.
            return null;
        }

        return null;
    }

    /// <summary>The path of a listed assignment, rebuilt in the scope's assignment collection; <c>null</c> when its id is elsewhere.</summary>
    public static string? AssignmentPath(AzureRoleGrant grant, string? assignmentId)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return AzureRoleGrant.PathIn(AssignmentsPath(grant.Scope), assignmentId);
    }

    /// <summary>The body of the create: <c>{ "properties": { roleDefinitionId, principalId, principalType?, description? } }</c>.</summary>
    public static JsonObject CreateBody(AzureRoleGrant grant, string? principalType, string? description)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var properties = new JsonObject
        {
            ["roleDefinitionId"] = grant.RoleDefinitionId,
            ["principalId"] = grant.PrincipalId,
        };
        if (principalType is not null)
        {
            properties["principalType"] = principalType;
        }

        if (description is not null)
        {
            properties["description"] = description;
        }

        return new JsonObject { ["properties"] = properties };
    }

    /// <summary><c>PUT {AssignmentsPath(scope)}/{AssignmentName(grant)}</c>; ARM's answer is the assignment (<c>null</c> for an empty body).</summary>
    public static Task<JsonObject?> CreateAsync(ArmClient arm, AzureRoleGrant grant, JsonObject body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(body);
        return arm.PutAsync(AssignmentsPath(grant.Scope) + "/" + AssignmentName(grant), ApiVersion, body, cancellationToken);
    }
}
