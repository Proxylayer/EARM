using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Engine.Providers;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// The Graph reads app role assignments need besides <see cref="ServicePrincipalLookup.FindByAppIdAsync"/>: a
/// resource service principal by object id, the kind of a principal, and the assignments of a resource
/// (<c>appRoleAssignedTo</c>). Never cached (roles mirror the application and lag a PATCH by seconds).
/// </summary>
public static class AppRoleAssignmentsLookup
{
    /// <summary><c>GET /servicePrincipals/{id}?$select=…</c>; <c>null</c> when none exists (404 under the default hint).</summary>
    public static async Task<ServicePrincipalSummary?> FindByIdAsync(IGraphClient graph, string resourceId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        var request = new GraphRequest("/servicePrincipals/" + GraphPath.Segment(resourceId), api, hint: hint).WithSelect(ServicePrincipalLookup.SelectedProperties);
        return await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) is JsonObject live ? ServicePrincipalLookup.Summarize(live) : null;
    }

    /// <summary>
    /// The <c>allowedMemberTypes</c> kind of a principal (<c>User</c> for users and groups, <c>Application</c> for
    /// service principals): <c>GET /directoryObjects/{id}?$select=id</c> and its <c>@odata.type</c>. <c>null</c> when
    /// the object does not exist (404 under the default hint) or is of another type.
    /// </summary>
    public static async Task<string?> MemberTypeOfPrincipalAsync(IGraphClient graph, string principalId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        var request = new GraphRequest("/directoryObjects/" + GraphPath.Segment(principalId), api, hint: hint).WithSelect("id");
        var node = await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
        return AppRoleAssignmentsRules.MemberTypeOf(JsonObjects.GetString(node, "@odata.type"));
    }

    /// <summary>The <c>appRoleAssignedTo</c> collection of a resource service principal.</summary>
    public static string AssignedToPath(string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        return "/servicePrincipals/" + GraphPath.Segment(resourceId) + "/appRoleAssignedTo";
    }

    /// <summary>
    /// Lists <c>GET /servicePrincipals/{resourceId}/appRoleAssignedTo?$top=999</c> (every page) and returns the
    /// assignment of <paramref name="principalId"/> to <paramref name="appRoleId"/>, or <c>null</c>. A 404 (the
    /// resource does not exist, or is not replicated yet under the default hint) is <c>null</c> as well.
    /// </summary>
    public static async Task<JsonObject?> FindAssignmentAsync(IGraphClient graph, string resourceId, string principalId, string appRoleId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var request = new GraphRequest(AssignedToPath(resourceId), api, hint: hint).WithTop(999);
        try
        {
            await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (item is JsonObject assignment && AppRoleAssignmentsRules.Matches(assignment, principalId, appRoleId))
                {
                    return assignment;
                }
            }
        }
        catch (GraphException ex) when (ex.IsNotFound && !ex.IsReplication)
        {
            return null;
        }

        return null;
    }
}
