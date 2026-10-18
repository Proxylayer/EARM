using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// The shared rules of the cross-tenant access configurations (https://learn.microsoft.com/en-us/graph/api/resources/crosstenantaccesspolicy-overview?view=graph-rest-1.0):
/// the setting objects whose undeclared members keep their live values (<see cref="TenantPolicyShape"/>; Learn does not say whether a PATCH replaces or
/// merges a nested setting, so EARM always sends the whole object), and the properties Graph manages that a template may not declare.
/// </summary>
public static class CrossTenantAccessRules
{
    /// <summary>The setting objects of the default and partner configurations (Learn: crosstenantaccesspolicyconfigurationdefault, -partner).</summary>
    public static readonly IReadOnlyList<string> SettingObjects =
    [
        "b2bCollaborationInbound",
        "b2bCollaborationOutbound",
        "b2bDirectConnectInbound",
        "b2bDirectConnectOutbound",
        "inboundTrust",
        "tenantRestrictions",
        "automaticUserConsentSettings",
        "invitationRedemptionIdentityProviderConfiguration",
        "m365CollaborationInbound",
        "m365CollaborationOutbound",
        "appServiceConnectInbound",
    ];

    /// <summary>The problems of declaring any of <paramref name="readOnly"/> (with the reason Graph manages it).</summary>
    public static List<string> RefuseReadOnly(JsonObject properties, IReadOnlyDictionary<string, string> readOnly)
    {
        return readOnly
            .Where(p => JsonObjects.TryGetIgnoreCase(properties, p.Key, out _))
            .Select(p => $"properties.{p.Key} cannot be declared: {p.Value}")
            .ToList();
    }
}
