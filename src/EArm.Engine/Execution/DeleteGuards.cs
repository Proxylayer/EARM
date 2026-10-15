using System;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;

namespace EArm.Engine.Execution;

/// <summary>
/// The objects EARM never deletes, whatever the template declares: the signed-in principal, and a service principal owned by another organization
/// (a Microsoft first-party or multi-tenant app). Applied to every delete there is: the old object of a recreate (what-if and executor) and
/// <c>deployment delete</c>.
/// </summary>
public static class DeleteGuards
{
    /// <summary>Why <paramref name="live"/> must not be deleted (<see cref="EngineDiagnosticCodes.ProtectedObject"/>), or <c>null</c> when it may be.</summary>
    /// <param name="resourceId">The resource id the object was found for (for the message).</param>
    /// <param name="type">The resource type.</param>
    /// <param name="live">The live object, as the provider read it by key.</param>
    /// <param name="signedInPrincipalId">The object id of the signed-in principal, when known.</param>
    /// <param name="tenantId">The tenant id, to recognise service principals of other organizations.</param>
    public static string? Refusal(string resourceId, string type, ExistingResource live, string? signedInPrincipalId, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(live);
        if (signedInPrincipalId is { } signedIn && string.Equals(signedIn, live.GraphId, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{resourceId}' is the signed-in principal (Graph id {live.GraphId}); EARM never deletes it.";
        }

        if (IsFirstPartyServicePrincipal(type, live.Raw, tenantId))
        {
            return $"'{resourceId}' (Graph id {live.GraphId}) is a service principal owned by another organization (a Microsoft first-party or multi-tenant app); EARM never deletes it.";
        }

        return null;
    }

    /// <summary>True for a servicePrincipals object whose <c>appOwnerOrganizationId</c> is another tenant's (first-party and multi-tenant apps).</summary>
    public static bool IsFirstPartyServicePrincipal(string type, JsonObject? raw, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.EndsWith("/servicePrincipals", StringComparison.OrdinalIgnoreCase) || raw is null || string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        string? owner = JsonObjects.GetString(raw, "appOwnerOrganizationId");
        return !string.IsNullOrWhiteSpace(owner) && !string.Equals(owner, tenantId, StringComparison.OrdinalIgnoreCase);
    }
}
