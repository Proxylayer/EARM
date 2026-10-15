using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Engine.Edges;
using EArm.Engine.Providers;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// The Graph reads app role assignments need besides <see cref="ServicePrincipalLookup.FindByAppIdAsync"/>: a
/// resource service principal by object id, the kind and collection of a principal, and the assignments a principal
/// holds (<c>appRoleAssignments</c>). Never cached (roles mirror the application and lag a PATCH by seconds).
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
    /// The collection (<c>users</c>, <c>groups</c> or <c>servicePrincipals</c>) a principal belongs to, from
    /// <c>GET /directoryObjects/{id}?$select=id</c> and its <c>@odata.type</c>; <c>null</c> when the object does not exist
    /// (404 under the default hint) or is of another type.
    /// </summary>
    public static async Task<string?> PrincipalCollectionAsync(IGraphClient graph, string principalId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        var request = new GraphRequest("/directoryObjects/" + GraphPath.Segment(principalId), api, hint: hint).WithSelect("id");
        var node = await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
        string? collection = EdgeGraphContext.CollectionOfODataType(JsonObjects.GetString(node, "@odata.type"));
        return collection is "users" or "groups" or "servicePrincipals" ? collection : null;
    }

    /// <summary>
    /// Finds the assignment of <paramref name="principalId"/> to <paramref name="appRoleId"/> of <paramref name="resourceId"/> from the
    /// principal side: <c>GET /{users|groups|servicePrincipals}/{principalId}/appRoleAssignments?$top=999</c> (every page). A principal holds
    /// few assignments, whereas the resource side (<c>appRoleAssignedTo</c>) of a widely used service principal such as Microsoft Graph is
    /// tenant-sized. <c>null</c> when no assignment matches, or when the principal does not exist (or is not replicated yet under the default hint).
    /// </summary>
    public static async Task<JsonObject?> FindAssignmentAsync(IGraphClient graph, string resourceId, string principalId, string appRoleId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        try
        {
            string? collection = await PrincipalCollectionAsync(graph, principalId, api, hint, cancellationToken).ConfigureAwait(false);
            if (collection is null)
            {
                return null;
            }

            var request = new GraphRequest("/" + collection + "/" + GraphPath.Segment(principalId) + "/appRoleAssignments", api, hint: hint).WithTop(999);
            await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (item is JsonObject assignment
                    && AppRoleAssignmentsRules.Matches(assignment, principalId, appRoleId)
                    && JsonObjects.KeyEquals(JsonObjects.GetString(assignment, "resourceId"), resourceId))
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
