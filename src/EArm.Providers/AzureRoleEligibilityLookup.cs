using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// The Azure Resource Manager side of <see cref="AzureRoleEligibilityScheduleRequestsProvider"/>. An eligibility is found with
/// <c>GET {scope}/providers/Microsoft.Authorization/roleEligibilitySchedules?api-version=2020-10-01</c> (every page, no <c>$filter</c>: the list is matched
/// client-side), keeping only the Direct schedule whose <c>properties.scope</c> IS the declared scope and whose role and principal are the grant's (an
/// eligibility inherited from a parent scope, or a group membership's, never counts). Requests go to
/// <c>PUT {scope}/providers/Microsoft.Authorization/roleEligibilityScheduleRequests/{new GUID}?api-version=2022-04-01-preview</c> with requestType
/// <c>AdminAssign</c> or <c>AdminRemove</c>. Never cached.
/// </summary>
public static class AzureRoleEligibilityLookup
{
    /// <summary>The api-version of the <c>roleEligibilitySchedules</c> read.</summary>
    public const string SchedulesApiVersion = "2020-10-01";

    /// <summary>The api-version of the <c>roleEligibilityScheduleRequests</c> write.</summary>
    public const string RequestsApiVersion = "2022-04-01-preview";

    public const string AdminAssign = "AdminAssign";

    public const string AdminRemove = "AdminRemove";

    /// <summary><c>{scope}/providers/Microsoft.Authorization/roleEligibilitySchedules</c>.</summary>
    public static string SchedulesPath(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return scope + AzureScopeRules.AuthorizationProvider + "/roleEligibilitySchedules";
    }

    /// <summary><c>{scope}/providers/Microsoft.Authorization/roleEligibilityScheduleRequests</c>.</summary>
    public static string RequestsPath(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return scope + AzureScopeRules.AuthorizationProvider + "/roleEligibilityScheduleRequests";
    }

    /// <summary>
    /// The Direct eligibility schedule of <paramref name="grant"/> AT its scope, or <c>null</c> (also when the scope does not exist: ARM answers the list with
    /// 404).
    /// </summary>
    public static async Task<JsonObject?> FindAsync(ArmClient arm, AzureRoleGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(grant);
        try
        {
            await foreach (var schedule in arm.ListAsync(SchedulesPath(grant.Scope), SchedulesApiVersion, null, cancellationToken).ConfigureAwait(false))
            {
                if (IsDirect(schedule) && grant.Matches(schedule))
                {
                    return schedule;
                }
            }
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // A scope that does not exist (a resource group not created yet) has no eligibility.
            return null;
        }

        return null;
    }

    /// <summary>True for a schedule of the principal itself (<c>memberType</c> Direct, or none), not one it holds through a group (Group) or a parent scope (Inherited).</summary>
    public static bool IsDirect(JsonObject schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        string? memberType = JsonObjects.GetString(AzureRoleGrant.PropertiesOf(schedule), "memberType");
        return memberType is null || string.Equals(memberType, "Direct", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path of a listed schedule, rebuilt in the scope's schedule collection; <c>null</c> when its id is elsewhere.</summary>
    public static string? SchedulePath(AzureRoleGrant grant, JsonObject schedule)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(schedule);
        return AzureRoleGrant.PathIn(SchedulesPath(grant.Scope), JsonObjects.GetId(schedule));
    }

    /// <summary>The body of a request: <c>{ "properties": { principalId, roleDefinitionId, requestType, justification, scheduleInfo? } }</c>.</summary>
    public static JsonObject RequestBody(string requestType, AzureRoleGrant grant, string justification, JsonObject? scheduleInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestType);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(justification);
        var properties = new JsonObject
        {
            ["principalId"] = grant.PrincipalId,
            ["roleDefinitionId"] = grant.RoleDefinitionId,
            ["requestType"] = requestType,
            ["justification"] = justification,
        };
        if (scheduleInfo is not null)
        {
            properties["scheduleInfo"] = scheduleInfo;
        }

        return new JsonObject { ["properties"] = properties };
    }

    /// <summary>
    /// Sends a request: <c>PUT {RequestsPath(scope)}/{new GUID}</c>. Returns ARM's answer, the request (<c>properties.status</c>,
    /// <c>properties.targetRoleEligibilityScheduleId</c>), or <c>null</c> for an empty body.
    /// </summary>
    public static Task<JsonObject?> SubmitAsync(ArmClient arm, AzureRoleGrant grant, JsonObject body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(body);
        return arm.PutAsync(RequestsPath(grant.Scope) + "/" + Guid.NewGuid().ToString("D"), RequestsApiVersion, body, cancellationToken);
    }
}
