using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// The kinds of resource EARM adds to a catalog and grants through an access package, by <c>originSystem</c> (Learn: entitlementmanagement-post-resourcerequests,
/// accesspackage-post-resourcerolescopes, accesspackagecatalog-list-resourceroles):
/// <list type="bullet">
/// <item><c>AadGroup</c> (the default): <c>originId</c> is the group's object id; the roles are <c>Member</c>, <c>Owner</c> and, for a group PIM for Groups
/// manages, <c>Eligible Member</c> / <c>Eligible Owner</c>.</item>
/// <item><c>AadApplication</c>: <c>originId</c> is the service principal's object id; the roles are the app roles (role <c>originId</c> = the app role's id,
/// <c>Default Access</c> with the zero id for an app without app roles).</item>
/// <item><c>SharePointOnline</c>: <c>originId</c> is the site URL (compared case-insensitively, without a trailing slash); the roles are the site's groups
/// (display name such as <c>Contoso Members</c>, role <c>originId</c> = the group's number in the site).</item>
/// </list>
/// </summary>
internal static class AccessPackageResourceOrigin
{
    public const string Group = "AadGroup";

    public const string Application = "AadApplication";

    public const string SharePointOnline = "SharePointOnline";

    /// <summary>The origin systems EARM manages, in their Graph casing.</summary>
    public static IReadOnlyList<string> Supported { get; } = [Group, Application, SharePointOnline];

    /// <summary>The Graph casing of a supported origin system (<c>null</c> or empty is <see cref="Group"/>), or <c>null</c> when EARM does not manage it.</summary>
    public static string? Canonical(string? originSystem)
    {
        if (string.IsNullOrWhiteSpace(originSystem))
        {
            return Group;
        }

        return Supported.FirstOrDefault(s => s.Equals(originSystem.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The origin system of a properties object (<c>originSystem</c>, <see cref="Group"/> when absent), in Graph casing when supported.</summary>
    public static string Of(JsonObject properties, string name = "originSystem")
    {
        string? declared = JsonObjects.GetString(properties, name);
        return Canonical(declared) ?? declared!.Trim();
    }

    /// <summary>
    /// The origin id as Graph is asked for it: trimmed, and a site URL without its trailing slash. The key of a catalog resource holds no origin system,
    /// so a site is recognised by its id: an absolute http(s) URL (group and service principal ids are GUIDs).
    /// </summary>
    public static string NormalizeId(string originId)
    {
        string trimmed = originId.Trim();
        return IsUrl(trimmed) ? trimmed.TrimEnd('/') : trimmed;
    }

    /// <summary>Two origin ids name the same resource: case-insensitive, and a site URL regardless of a trailing slash.</summary>
    public static bool SameId(string? left, string? right)
    {
        return left is not null && right is not null && string.Equals(NormalizeId(left), NormalizeId(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The origin id is a site URL (<c>http://</c> or <c>https://</c>).</summary>
    public static bool IsUrl(string originId)
    {
        string trimmed = originId.TrimStart();
        return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the resource is called in messages: group, application or SharePoint Online site.</summary>
    public static string Noun(string originSystem)
    {
        return originSystem switch
        {
            _ when originSystem.Equals(Application, StringComparison.OrdinalIgnoreCase) => "application",
            _ when IsSite(originSystem) => "SharePoint Online site",
            _ => "group",
        };
    }

    public static bool IsGroup(string originSystem)
    {
        return originSystem.Equals(Group, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSite(string originSystem)
    {
        return originSystem.Equals(SharePointOnline, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The catalog's role that a declared <c>role</c> names: by display name (case-insensitive, trimmed), else by the role's <c>originId</c> (an app role's
    /// id, a site group's number). The display name wins, so a role whose name looks like another role's id still resolves to itself.
    /// </summary>
    public static JsonObject? FindRole(IReadOnlyList<JsonObject> roles, string role)
    {
        string wanted = role.Trim();
        return roles.FirstOrDefault(r => string.Equals(JsonObjects.GetString(r, "displayName")?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            ?? roles.FirstOrDefault(r => string.Equals(JsonObjects.GetString(r, "originId")?.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A live role (of a role scope) is the declared <c>role</c>: by display name, else by its <c>originId</c>.</summary>
    public static bool IsRole(JsonObject liveRole, string role)
    {
        string wanted = role.Trim();
        return string.Equals(JsonObjects.GetString(liveRole, "displayName")?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(JsonObjects.GetString(liveRole, "originId")?.Trim(), wanted, StringComparison.OrdinalIgnoreCase);
    }
}
