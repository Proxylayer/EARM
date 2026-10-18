using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// The cross-tenant access policy (https://learn.microsoft.com/en-us/graph/api/resources/crosstenantaccesspolicy-overview?view=graph-rest-1.0): the
/// default configuration (GET, merge PATCH 204, <c>resetToSystemDefault</c> 204; served with Microsoft's service defaults and <c>isServiceDefault</c>,
/// which a PATCH clears and a reset sets again) and the partner configurations keyed by <c>tenantId</c> (list, POST 201 with the object, GET, merge
/// PATCH 204, DELETE 204; a partner with an identity synchronization policy cannot be deleted, a POST for an existing partner is a 409). Neither has an
/// <c>id</c>.
/// </summary>
public sealed partial class FakeDirectory
{
    public const string CrossTenantDefaultPath = "/policies/crossTenantAccessPolicy/default";

    public const string CrossTenantPartnersPath = "/policies/crossTenantAccessPolicy/partners";

    private JsonObject? _crossTenantDefault;

    private readonly HashSet<string> _partnersWithIdentitySync = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The live default configuration (a copy).</summary>
    public JsonObject CrossTenantDefault
    {
        get
        {
            lock (_gate)
            {
                return (JsonObject)CrossTenantDefaultState().DeepClone();
            }
        }
    }

    /// <summary>Makes a partner's delete fail as if it had a user synchronization policy.</summary>
    public void AddPartnerIdentitySynchronization(string tenantId)
    {
        lock (_gate)
        {
            _partnersWithIdentitySync.Add(tenantId);
        }
    }

    /// <summary>Seeds a partner configuration.</summary>
    public JsonObject AddCrossTenantPartner(string tenantId, Action<JsonObject>? configure = null)
    {
        lock (_gate)
        {
            var partner = NewPartner(tenantId);
            configure?.Invoke(partner);
            Collection(CrossTenantPartnersPath).Add(partner);
            return (JsonObject)partner.DeepClone();
        }
    }

    private bool TryHandleCrossTenantAccess(string method, string[] segments, IReadOnlyDictionary<string, string>? query, JsonNode? body, RetryHint hint, int pageSize, out (HttpStatusCode, JsonNode?) result)
    {
        result = default;
        if (!StartsWith(segments, "policies", "crossTenantAccessPolicy") || segments.Length < 3)
        {
            return false;
        }

        if (segments[2].Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            result = HandleCrossTenantDefault(method, segments, body, hint);
            return true;
        }

        if (!segments[2].Equals("partners", StringComparison.OrdinalIgnoreCase) || segments.Length > 4)
        {
            return false;
        }

        var partners = Collection(CrossTenantPartnersPath);
        if (segments.Length == 3)
        {
            result = method switch
            {
                "GET" => (HttpStatusCode.OK, Page(partners, CrossTenantPartnersPath.TrimStart('/'), query, pageSize)),
                "POST" => (HttpStatusCode.Created, CreatePartner(partners, body, hint)),
                _ => throw MethodNotAllowed($"{method} is not supported on '{CrossTenantPartnersPath}'.", hint),
            };
            return true;
        }

        string tenantId = segments[3];
        var partner = partners.FirstOrDefault(p => ValueEquals(GetString(p, "tenantId"), tenantId)) ?? throw NotFound(CrossTenantPartnersPath + "/" + tenantId, hint);
        switch (method)
        {
            case "GET":
                result = (HttpStatusCode.OK, partner.DeepClone());
                return true;
            case "PATCH":
                if (body is JsonObject payload && GetString(payload, "tenantId") is { } changed && !ValueEquals(changed, tenantId))
                {
                    throw BadRequest("tenantId cannot be changed.", hint);
                }

                Merge(partner, body);
                result = (HttpStatusCode.NoContent, null);
                return true;
            case "DELETE":
                if (_partnersWithIdentitySync.Contains(tenantId))
                {
                    throw BadRequest("The partner has an identity synchronization policy; delete it first.", hint);
                }

                partners.Remove(partner);
                result = (HttpStatusCode.NoContent, null);
                return true;
            default:
                throw MethodNotAllowed($"{method} is not supported on a partner configuration.", hint);
        }
    }

    private (HttpStatusCode, JsonNode?) HandleCrossTenantDefault(string method, string[] segments, JsonNode? body, RetryHint hint)
    {
        if (segments.Length == 4 && segments[3].Equals("resetToSystemDefault", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            _crossTenantDefault = null;
            return (HttpStatusCode.NoContent, null);
        }

        if (segments.Length != 3)
        {
            throw NotFound("/" + string.Join('/', segments), hint);
        }

        var state = CrossTenantDefaultState();
        switch (method)
        {
            case "GET":
                return (HttpStatusCode.OK, state.DeepClone());
            case "PATCH":
                if (body is JsonObject payload && payload.Any(p => p.Key.Equals("isServiceDefault", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("automaticUserConsentSettings", StringComparison.OrdinalIgnoreCase)))
                {
                    throw BadRequest("isServiceDefault and automaticUserConsentSettings are read-only on the default configuration.", hint);
                }

                Merge(state, body);
                state["isServiceDefault"] = false;
                return (HttpStatusCode.NoContent, null);
            default:
                throw MethodNotAllowed($"{method} is not supported on '{CrossTenantDefaultPath}'.", hint);
        }
    }

    private static JsonObject CreatePartner(List<JsonObject> partners, JsonNode? body, RetryHint hint)
    {
        var created = Merge(new JsonObject(), body);
        string tenantId = GetString(created, "tenantId") ?? throw BadRequest("tenantId is required.", hint);
        if (partners.Any(p => ValueEquals(GetString(p, "tenantId"), tenantId)))
        {
            throw new GraphException(HttpStatusCode.Conflict, "Request_MultipleObjectsWithSameKeyValue", $"A partner configuration for tenant {tenantId} already exists.", hint);
        }

        var partner = NewPartner(tenantId);
        Merge(partner, created);
        partners.Add(partner);
        return (JsonObject)partner.DeepClone();
    }

    private static JsonObject NewPartner(string tenantId)
    {
        return new JsonObject
        {
            ["tenantId"] = tenantId,
            ["isServiceProvider"] = null,
            ["isInMultiTenantOrganization"] = false,
            ["inboundTrust"] = null,
            ["b2bCollaborationOutbound"] = null,
            ["b2bCollaborationInbound"] = null,
            ["b2bDirectConnectOutbound"] = null,
            ["b2bDirectConnectInbound"] = null,
            ["tenantRestrictions"] = null,
            ["automaticUserConsentSettings"] = new JsonObject { ["inboundAllowed"] = null, ["outboundAllowed"] = null },
        };
    }

    private JsonObject CrossTenantDefaultState()
    {
        return _crossTenantDefault ??= new JsonObject
        {
            ["isServiceDefault"] = true,
            ["inboundTrust"] = new JsonObject { ["isMfaAccepted"] = false, ["isCompliantDeviceAccepted"] = false, ["isHybridAzureADJoinedDeviceAccepted"] = false },
            ["b2bCollaborationOutbound"] = Setting("allowed"),
            ["b2bCollaborationInbound"] = Setting("allowed"),
            ["b2bDirectConnectOutbound"] = Setting("blocked"),
            ["b2bDirectConnectInbound"] = Setting("blocked"),
            ["tenantRestrictions"] = Setting("blocked"),
            ["automaticUserConsentSettings"] = new JsonObject { ["inboundAllowed"] = false, ["outboundAllowed"] = false },
            ["invitationRedemptionIdentityProviderConfiguration"] = new JsonObject
            {
                ["primaryIdentityProviderPrecedenceOrder"] = new JsonArray("externalFederation", "azureActiveDirectory", "socialIdentityProviders"),
                ["fallbackIdentityProvider"] = "defaultConfiguredIdp",
            },
        };

        static JsonObject Setting(string accessType)
        {
            return new JsonObject
            {
                ["usersAndGroups"] = new JsonObject { ["accessType"] = accessType, ["targets"] = new JsonArray(new JsonObject { ["target"] = "AllUsers", ["targetType"] = "user" }) },
                ["applications"] = new JsonObject { ["accessType"] = accessType, ["targets"] = new JsonArray(new JsonObject { ["target"] = "AllApplications", ["targetType"] = "application" }) },
            };
        }
    }
}
