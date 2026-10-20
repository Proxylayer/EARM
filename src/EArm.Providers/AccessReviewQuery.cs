using System;

namespace EArm.Providers;

/// <summary>
/// When a declared access review query (scope, instance enumeration scope, reviewer) and the one Graph serves back name the same thing. Graph stores
/// some queries rewritten: it prefixes <c>/v1.0</c> (<c>/groups/{id}/owners</c> comes back as <c>/v1.0/groups/{id}/owners</c>) and appends the user
/// type cast to a members query (<c>/groups/{id}/transitiveMembers</c> comes back as <c>/v1.0/groups/{id}/transitiveMembers/microsoft.graph.user</c>).
/// The rules are strict, and used for comparison only (what is sent to Graph is the declared text):
/// <list type="number">
/// <item>A leading <c>/v1.0</c> segment is ignored on either side; <c>/beta</c> is not (it is a different API).</item>
/// <item>A trailing <c>/microsoft.graph.user</c> equals its absence only directly after a <c>/members</c> or <c>/transitiveMembers</c> segment, and only
/// when the other side has no type cast there (a different cast, such as <c>/microsoft.graph.group</c>, is a difference).</item>
/// <item>Everything else compares case-insensitively and exactly; the rules above apply to the path, never to a <c>?</c> query string.</item>
/// </list>
/// </summary>
internal static class AccessReviewQuery
{
    private const string VersionPrefix = "/v1.0";

    private const string UserCast = "/microsoft.graph.user";

    /// <summary>Whether <paramref name="declared"/> and <paramref name="live"/> are the same query under the rules of the type.</summary>
    public static bool AreEquivalent(string? declared, string? live)
    {
        if (declared is null || live is null)
        {
            return declared is null && live is null;
        }

        if (string.Equals(declared, live, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var (leftPath, leftQuery) = Split(WithoutVersion(declared));
        var (rightPath, rightQuery) = Split(WithoutVersion(live));
        if (!string.Equals(leftQuery, rightQuery, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase)
            || IsUserCastOf(leftPath, rightPath)
            || IsUserCastOf(rightPath, leftPath);
    }

    /// <summary>
    /// <paramref name="live"/> with Graph's rewrites put back into the form <paramref name="declared"/> uses (the <c>/v1.0</c> prefix and the user cast
    /// added or removed as the declared text has them), keeping the live text otherwise, so a difference the rules do not excuse (such as letter case)
    /// still shows. <paramref name="live"/> unchanged when the two are not equivalent.
    /// </summary>
    public static string InDeclaredForm(string declared, string live)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(live);
        if (!AreEquivalent(declared, live))
        {
            return live;
        }

        string declaredRest = WithoutVersion(declared);
        string liveRest = WithoutVersion(live);
        var (declaredPath, _) = Split(declaredRest);
        var (livePath, liveQuery) = Split(liveRest);
        if (IsUserCastOf(livePath, declaredPath))
        {
            livePath = livePath[..^UserCast.Length];
        }
        else if (IsUserCastOf(declaredPath, livePath))
        {
            livePath += declaredPath[^UserCast.Length..];
        }

        string prefix = declaredRest.Length == declared.Length ? string.Empty : declared[..VersionPrefix.Length];
        return prefix + livePath + liveQuery;
    }

    /// <summary>The text without a leading <c>/v1.0</c> segment (followed by <c>/</c>, <c>?</c> or nothing).</summary>
    private static string WithoutVersion(string query)
    {
        if (query.StartsWith(VersionPrefix, StringComparison.OrdinalIgnoreCase)
            && (query.Length == VersionPrefix.Length || query[VersionPrefix.Length] is '/' or '?'))
        {
            return query[VersionPrefix.Length..];
        }

        return query;
    }

    /// <summary>The path and the query string (from the first <c>?</c>, inclusive; empty when none).</summary>
    private static (string Path, string Query) Split(string query)
    {
        int mark = query.IndexOf('?', StringComparison.Ordinal);
        return mark < 0 ? (query, string.Empty) : (query[..mark], query[mark..]);
    }

    /// <summary>Whether <paramref name="cast"/> is <paramref name="plain"/> plus <c>/microsoft.graph.user</c>, with <paramref name="plain"/> ending in a members segment.</summary>
    private static bool IsUserCastOf(string cast, string plain)
    {
        return cast.EndsWith(UserCast, StringComparison.OrdinalIgnoreCase)
            && string.Equals(cast[..^UserCast.Length], plain, StringComparison.OrdinalIgnoreCase)
            && EndsWithMembersSegment(plain);
    }

    private static bool EndsWithMembersSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        string last = slash < 0 ? path : path[(slash + 1)..];
        return slash >= 0
            && (string.Equals(last, "members", StringComparison.OrdinalIgnoreCase) || string.Equals(last, "transitiveMembers", StringComparison.OrdinalIgnoreCase));
    }
}
