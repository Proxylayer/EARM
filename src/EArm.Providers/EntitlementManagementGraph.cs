using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// The Graph reads the entitlement management providers share (<c>/identityGovernance/entitlementManagement</c>, Learn:
/// entitlementmanagement-list-catalogs, entitlementmanagement-list-accesspackages, accesspackagecatalog-list-resources,
/// accesspackagecatalog-list-resourceroles): the displayName lookups with their <c>AmbiguousMatch</c> guard, the catalog of a package and
/// the resources and roles of a catalog.
/// </summary>
internal static class EntitlementManagementGraph
{
    public const string Root = "/identityGovernance/entitlementManagement";

    public const string CatalogsPath = Root + "/catalogs";

    public const string AccessPackagesPath = Root + "/accessPackages";

    public const string ResourceRequestsPath = Root + "/resourceRequests";

    public const string AssignmentPoliciesPath = Root + "/assignmentPolicies";

    /// <summary>The only origin system EARM manages.</summary>
    public const string GroupOrigin = "AadGroup";

    /// <summary>
    /// <c>GET {collection}?$filter=displayName eq '…'</c>, every page, re-matched client-side (case-insensitive). Several hits are
    /// <c>AmbiguousMatch</c> naming the ids; none is <c>null</c>.
    /// </summary>
    public static async Task<JsonObject?> FindSingleByDisplayNameAsync(IGraphClient graph, string collectionPath, string displayName, string what, string resourceType, GraphApi api, string? expand, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(collectionPath, api).WithFilter($"displayName eq '{ProviderSupport.EscapeLiteral(displayName)}'");
        if (expand is not null)
        {
            request = request.WithExpand(expand);
        }

        var hits = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject found && string.Equals(JsonObjects.GetString(found, "displayName"), displayName, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(found);
            }
        }

        if (hits.Count > 1)
        {
            string ids = string.Join(", ", hits.Select(h => JsonObjects.GetId(h) ?? "?"));
            throw new ProviderException(
                ProviderException.AmbiguousMatch,
                $"{hits.Count} {what}s have displayName '{displayName}' (ids: {ids}); rename or delete the others so the name identifies one object.",
                ResourceIdentifier.Create(resourceType, displayName));
        }

        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>The access package by id with its catalog expanded, or <c>null</c> when it does not exist.</summary>
    public static async Task<JsonObject?> GetPackageAsync(IGraphClient graph, string packageId, GraphApi api, RetryHint hint, string expand, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(AccessPackagesPath + "/" + GraphPath.Segment(packageId), api, hint: hint).WithExpand(expand);
        return await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
    }

    /// <summary>The <c>catalog.id</c> of an expanded package object, or <c>null</c>.</summary>
    public static string? CatalogIdOf(JsonObject package)
    {
        return package["catalog"] is JsonObject catalog ? JsonObjects.GetId(catalog) : null;
    }

    /// <summary>The catalog's resource for a group (<c>GET catalogs/{id}/resources?$filter=originId eq '…'</c>), or <c>null</c> (also when the catalog does not exist).</summary>
    public static async Task<JsonObject?> FindCatalogResourceAsync(IGraphClient graph, string catalogId, string originId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(CatalogsPath + "/" + GraphPath.Segment(catalogId) + "/resources", api, hint: hint)
            .WithFilter($"originId eq '{ProviderSupport.EscapeLiteral(originId)}'");
        try
        {
            await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (item is JsonObject resource && string.Equals(JsonObjects.GetString(resource, "originId"), originId, StringComparison.OrdinalIgnoreCase))
                {
                    return resource;
                }
            }
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // The catalog is gone (or not created yet): no resource.
        }

        return null;
    }

    /// <summary>The roles the catalog offers for one of its resources (<c>GET catalogs/{id}/resourceRoles?$filter=(originSystem eq '…' and resource/id eq '…')</c>).</summary>
    public static async Task<IReadOnlyList<JsonObject>> ListResourceRolesAsync(IGraphClient graph, string catalogId, string resourceId, string originSystem, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(CatalogsPath + "/" + GraphPath.Segment(catalogId) + "/resourceRoles", api, hint: hint)
            .WithFilter($"(originSystem eq '{ProviderSupport.EscapeLiteral(originSystem)}' and resource/id eq '{ProviderSupport.EscapeLiteral(resourceId)}')")
            .WithExpand("resource");
        var roles = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject role)
            {
                roles.Add(role);
            }
        }

        return roles;
    }

    /// <summary>
    /// True when Graph's refusal is about existing assignments (Learn: accesspackage-delete, "You cannot delete an access package if it has any
    /// accessPackageAssignment"; entitlement-management-troubleshoot: the policy delete fails with an active assignments error). The error code is not
    /// documented, so the message is matched.
    /// </summary>
    public static bool IsAssignmentsRefusal(GraphException exception)
    {
        return exception.Status == HttpStatusCode.BadRequest
            && (exception.GraphMessage.Contains("assignment", StringComparison.OrdinalIgnoreCase) || exception.Code.Contains("Assignment", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Two Graph object ids (or other key values) are the same.</summary>
    public static bool SameId(string? left, string? right)
    {
        return left is not null && right is not null && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
