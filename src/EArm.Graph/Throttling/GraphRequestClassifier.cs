using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;

namespace EArm.Graph.Throttling;

/// <summary>
/// Classifies a Graph request into its throttling <see cref="GraphServiceArea"/> and estimates its directory resource-unit cost from Microsoft's
/// published cost table ("Identity and access service limits", https://learn.microsoft.com/en-us/graph/throttling-limits). Pure: the same method and
/// path always give the same cost. The estimate is reconciled with the <c>x-ms-resource-unit</c> response header after the call.
/// <para>
/// The path may carry the API version (<c>/v1.0</c>, <c>/beta</c>: both share the limits and are stripped), a query string and percent-encoding;
/// key segments such as <c>applications(appId='...')</c> count as the collection plus a key.
/// </para>
/// </summary>
internal static class GraphRequestClassifier
{
    private const string Key = "{key}";

    /// <summary>Top-level segments of the directory ("Identity and access") service.</summary>
    private static readonly FrozenSet<string> DirectoryRoots = new[]
    {
        "users", "groups", "applications", "serviceprincipals", "oauth2permissiongrants", "directory", "directoryobjects", "administrativeunits",
        "subscribedskus", "organization", "me", "devices", "contacts", "domains", "directoryroles", "directoryroletemplates",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The <c>policies/*</c> that are directory objects (the rest of <c>policies/*</c> has no published rate).</summary>
    private static readonly FrozenSet<string> DirectoryPolicies = new[]
    {
        "tokenlifetimepolicies", "claimsmappingpolicies", "appmanagementpolicies", "defaultappmanagementpolicy", "crosstenantaccesspolicy",
        "homerealmdiscoverypolicies", "tokenissuancepolicies", "activitybasedtimeoutpolicies",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The membership navigations of a user (or <c>me</c>) that cost 2 resource units.</summary>
    private static readonly FrozenSet<string> UserMembership = new[]
    {
        "memberof", "ownedobjects", "transitivememberof", "licensedetails",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The cost of one request sent with <paramref name="method"/> to <paramref name="pathAndQuery"/> (relative to the host or the API base).</summary>
    public static GraphRequestCost Classify(string method, string pathAndQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var write = HttpGraphClient.IsWrite(method);
        var separator = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = separator < 0 ? pathAndQuery : pathAndQuery[..separator];
        var query = separator < 0 ? string.Empty : pathAndQuery[(separator + 1)..];
        var segments = Segments(path);
        var area = AreaOf(segments);
        if (area != GraphServiceArea.Directory)
        {
            return new GraphRequestCost(area, 0, write);
        }

        var units = BaseUnits(method, segments, write) + QueryAdjustment(query);
        return new GraphRequestCost(area, Math.Max(1, units), write);
    }

    /// <summary>The area of a request, from its normalised segments.</summary>
    internal static GraphServiceArea AreaOf(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            return GraphServiceArea.Other;
        }

        var root = segments[0];
        if (root == "$batch")
        {
            return GraphServiceArea.None;
        }

        if (DirectoryRoots.Contains(root))
        {
            return GraphServiceArea.Directory;
        }

        return root switch
        {
            "identity" when segments.Count > 2 && segments[1] == "conditionalaccess" && segments[2] is "policies" or "namedlocations" => GraphServiceArea.ConditionalAccess,
            "policies" when segments.Count > 1 && DirectoryPolicies.Contains(segments[1]) => GraphServiceArea.Directory,
            "policies" => GraphServiceArea.Policies,
            "identitygovernance" => GraphServiceArea.IdentityGovernance,
            "rolemanagement" => GraphServiceArea.RoleManagement,
            _ => GraphServiceArea.Other,
        };
    }

    /// <summary>
    /// The path as lower-case segments: percent-decoded, without the API version, a key in parentheses split into its own <c>{key}</c> segment, and
    /// trailing <c>$count</c> / <c>$ref</c> / <c>$value</c> and <c>microsoft.graph.*</c> type casts removed (they address the same resource).
    /// </summary>
    internal static List<string> Segments(string path)
    {
        var segments = new List<string>();
        foreach (var raw in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = SafeUnescape(raw).ToLowerInvariant();
            if (segments.Count == 0 && segment is "v1.0" or "beta")
            {
                continue;
            }

            var paren = segment.IndexOf('(', StringComparison.Ordinal);
            if (paren > 0)
            {
                segments.Add(segment[..paren]);
                segments.Add(Key);
            }
            else
            {
                segments.Add(segment);
            }
        }

        while (segments.Count > 1 && (segments[^1] is "$count" or "$ref" or "$value" || segments[^1].StartsWith("microsoft.graph.", StringComparison.Ordinal)))
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return segments;
    }

    /// <summary>The published base cost: 1 by default (a write costs 1 resource unit plus one write), more for the listed reads.</summary>
    private static int BaseUnits(string method, List<string> s, bool write)
    {
        if (write)
        {
            return method.Equals("POST", StringComparison.OrdinalIgnoreCase) && s.Count == 2 && s[0] == "directoryobjects" && s[1] == "getbyids" ? 5 : 1;
        }

        var root = s[0];
        return s.Count switch
        {
            1 when root is "applications" or "users" or "oauth2permissiongrants" => 2,
            1 when root is "subscribedskus" => 3,
            2 when root is "applications" or "users" or "oauth2permissiongrants" => 2,
            2 when root is "subscribedskus" => 3,
            2 when root is "me" && UserMembership.Contains(s[1]) => 2,
            3 when root is "applications" && s[2] == "extensionproperties" => 2,
            3 when root is "groups" && s[2] == "members" => 3,
            3 when root is "groups" && s[2] == "transitivemembers" => 5,
            3 when root is "users" && UserMembership.Contains(s[2]) => 2,
            3 when root is "serviceprincipals" && s[2] == "approleassignments" => 2,
            4 when root is "applications" && s[2] == "extensionproperties" => 2,
            _ => 1,
        };
    }

    /// <summary>The published modifiers: <c>$select</c> −1, <c>$expand</c> +1, <c>$top</c> below 20 −1.</summary>
    private static int QueryAdjustment(string query)
    {
        var adjustment = 0;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var name = SafeUnescape(equals < 0 ? part : part[..equals]).ToLowerInvariant();
            var value = equals < 0 ? string.Empty : SafeUnescape(part[(equals + 1)..]);
            switch (name)
            {
                case "$select":
                    adjustment--;
                    break;
                case "$expand":
                    adjustment++;
                    break;
                case "$top" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var top) && top < 20:
                    adjustment--;
                    break;
            }
        }

        return adjustment;
    }

    private static string SafeUnescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }
}
