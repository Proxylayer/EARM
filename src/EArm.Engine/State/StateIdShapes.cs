using System;
using System.Text.RegularExpressions;

namespace EArm.Engine.State;

/// <summary>
/// The shapes a Graph object id recorded in the state may have (review finding S-01c). A state file is committed and
/// can be edited; an id such as <c>me</c>, <c>../users/x</c> or a query fragment must never reach a Graph path. Every
/// directory object id is a GUID; the exceptions are the opaque ids Graph generates for role assignments, app role
/// assignments and OAuth2 permission grants (base64url-like tokens) and the <c>c1</c>..<c>c99</c> ids of authentication contexts.
/// </summary>
public static partial class StateIdShapes
{
    /// <summary>True when <paramref name="id"/> is an acceptable Graph id for an object of <paramref name="type"/>.</summary>
    public static bool IsValidGraphId(string type, string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        if (EndsWith(type, "/authenticationContextClassReferences"))
        {
            return AuthenticationContextId().IsMatch(id);
        }

        if (EndsWith(type, "/roleAssignments") || EndsWith(type, "/appRoleAssignments") || EndsWith(type, "/oauth2PermissionGrants"))
        {
            return OpaqueId().IsMatch(id);
        }

        return id.Length == 36 && Guid.TryParseExact(id, "D", out _);
    }

    /// <summary>A short description of what is accepted for the type, for error messages.</summary>
    public static string Describe(string type)
    {
        if (EndsWith(type, "/authenticationContextClassReferences"))
        {
            return "c1..c99";
        }

        if (EndsWith(type, "/roleAssignments") || EndsWith(type, "/appRoleAssignments") || EndsWith(type, "/oauth2PermissionGrants"))
        {
            return "letters, digits, '-' and '_' (an opaque Graph id)";
        }

        return "a GUID";
    }

    private static bool EndsWith(string type, string suffix)
    {
        return type.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\Ac([1-9]|[1-9][0-9])\z", RegexOptions.CultureInvariant)]
    private static partial Regex AuthenticationContextId();

    [GeneratedRegex(@"\A[A-Za-z0-9_\-=]{1,200}\z", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueId();
}
