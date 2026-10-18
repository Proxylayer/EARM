using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// The kinds of catalog resource besides groups (Learn: entitlementmanagement-post-resourcerequests, accesspackagecatalog-list-resourceroles):
/// <list type="bullet">
/// <item><c>AadApplication</c>: <c>originId</c> is a service principal of the fake directory (404 otherwise; 403 unless <see cref="CallerMayAddApplications"/>);
/// the roles are its enabled app roles (role <c>originId</c> = the app role's id), or <c>Default Access</c> with the zero id when it has none.</item>
/// <item><c>SharePointOnline</c>: <c>originId</c> is the URL of a site seeded with <see cref="AddSharePointSite"/>, matched case-insensitively and without a
/// trailing slash, and kept as the request spelled it (404 otherwise; 403 unless <see cref="CallerMayAddSites"/>); the roles are the site's groups
/// <c>{title} Owners</c> (3), <c>{title} Members</c> (4) and <c>{title} Visitors</c> (5).</item>
/// </list>
/// </summary>
public sealed partial class FakeEntitlementManagement
{
    private readonly Dictionary<string, string> _sites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the caller may add an application (owner, Application Administrator, Cloud Application Administrator or Identity Governance Administrator).</summary>
    public bool CallerMayAddApplications { get; set; } = true;

    /// <summary>Whether the caller may add a SharePoint Online site (SharePoint Administrator or an administrator of the site).</summary>
    public bool CallerMayAddSites { get; set; } = true;

    /// <summary>Seeds a SharePoint Online site with its three default groups (Owners 3, Members 4, Visitors 5).</summary>
    public void AddSharePointSite(string url, string title)
    {
        _sites[SiteKey(url)] = title;
    }

    /// <summary>Seeds a resource already in a catalog, spelled as given (for example a site added in the portal with a trailing slash in its URL).</summary>
    public JsonObject AddCatalogResource(string catalogId, string originId, string originSystem, string displayName)
    {
        var resource = new JsonObject
        {
            ["id"] = NewId(), ["catalogId"] = catalogId, ["originId"] = originId, ["originSystem"] = originSystem, ["displayName"] = displayName,
        };
        _resources.Add(resource);
        return resource;
    }

    /// <summary>The object a resource request adds, as Graph finds it in its origin system; throws as Graph refuses it.</summary>
    private JsonObject ResolveOrigin(string originSystem, string originId, Func<string, JsonObject?> findGroup, RetryHint hint)
    {
        switch (originSystem)
        {
            case "AadGroup":
                var group = findGroup(originId) ?? throw FakeDirectory.NotFound("group " + originId, hint);
                if (group["isAssignableToRole"] is JsonValue flag && flag.GetValue<bool>() && !CallerMayAddRoleAssignableGroups)
                {
                    throw new GraphException(HttpStatusCode.Forbidden, "Forbidden", "The caller is not allowed to add a role-assignable group.", hint);
                }

                return group;
            case "AadApplication":
                var principal = _findServicePrincipal(originId) ?? throw FakeDirectory.NotFound("servicePrincipal " + originId, hint);
                return CallerMayAddApplications
                    ? principal
                    : throw new GraphException(HttpStatusCode.Forbidden, "Forbidden", "The caller is not allowed to update the app role assignments of the application.", hint);
            case "SharePointOnline":
                string title = _sites.TryGetValue(SiteKey(originId), out var found) ? found : throw FakeDirectory.NotFound("site " + originId, hint);
                return CallerMayAddSites
                    ? new JsonObject { ["displayName"] = title, ["description"] = title + " site" }
                    : throw new GraphException(HttpStatusCode.Forbidden, "Forbidden", "The caller is not a SharePoint Administrator or an administrator of the site.", hint);
            default:
                throw FakeDirectory.BadRequest("Unsupported originSystem " + originSystem, hint);
        }
    }

    /// <summary>The roles the catalog offers for an application or a site resource.</summary>
    private IEnumerable<JsonObject> OriginRolesOf(JsonObject resource)
    {
        string originSystem = Str(resource, "originSystem")!;
        string originId = Str(resource, "originId")!;
        var roles = new List<(string Name, string OriginId)>();
        if (originSystem == "AadApplication")
        {
            var appRoles = (_findServicePrincipal(originId)?["appRoles"] as JsonArray)?.OfType<JsonObject>()
                .Where(r => r["isEnabled"] is not JsonValue enabled || enabled.GetValue<bool>()).ToList() ?? [];
            roles.AddRange(appRoles.Select(r => (Str(r, "displayName") ?? Str(r, "value") ?? "?", Str(r, "id")!)));
            if (roles.Count == 0)
            {
                roles.Add(("Default Access", Guid.Empty.ToString()));
            }
        }
        else if (_sites.TryGetValue(SiteKey(originId), out var title))
        {
            roles.Add((title + " Owners", "3"));
            roles.Add((title + " Members", "4"));
            roles.Add((title + " Visitors", "5"));
        }

        string resourceId = Str(resource, "id")!;
        return roles.Select(r => new JsonObject
        {
            ["id"] = RoleId(resourceId, r.OriginId), ["displayName"] = r.Name, ["originId"] = r.OriginId, ["originSystem"] = originSystem,
            ["resource"] = new JsonObject { ["id"] = resourceId, ["originId"] = originId, ["originSystem"] = originSystem },
        });
    }

    private static string SiteKey(string url)
    {
        return url.Trim().TrimEnd('/');
    }

    /// <summary>A stable id for a catalog role (Graph gives each role of a resource its own id).</summary>
    private static string RoleId(string resourceId, string roleOriginId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(resourceId + "|" + roleOriginId));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}
