using System;
using System.Net;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// The tenant default app management policy (https://learn.microsoft.com/en-us/graph/api/resources/tenantappmanagementpolicy?view=graph-rest-1.0):
/// a singleton that always exists, served with the documented default state (the GET example of tenantappmanagementpolicy-get: disabled, empty
/// restrictions); GET and a merge PATCH (204) only: Graph documents no POST, PUT or DELETE.
/// </summary>
public sealed partial class FakeDirectory
{
    public const string DefaultAppManagementPolicyPath = "/policies/defaultAppManagementPolicy";

    private JsonObject? _defaultAppManagementPolicy;

    /// <summary>The live default app management policy (a copy).</summary>
    public JsonObject DefaultAppManagementPolicy
    {
        get
        {
            lock (_gate)
            {
                return (JsonObject)DefaultAppManagementPolicyState().DeepClone();
            }
        }
    }

    /// <summary>Replaces members of the default app management policy (seeding).</summary>
    public void SetDefaultAppManagementPolicy(Action<JsonObject> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        lock (_gate)
        {
            configure(DefaultAppManagementPolicyState());
        }
    }

    private bool TryHandleDefaultAppManagementPolicy(string method, string[] segments, JsonNode? body, RetryHint hint, out (HttpStatusCode, JsonNode?) result)
    {
        result = default;
        if (segments.Length != 2 || !StartsWith(segments, "policies", "defaultAppManagementPolicy"))
        {
            return false;
        }

        var policy = DefaultAppManagementPolicyState();
        switch (method)
        {
            case "GET":
                result = (HttpStatusCode.OK, policy.DeepClone());
                return true;
            case "PATCH":
                Merge(policy, body);
                policy["id"] = "00000000-0000-0000-0000-000000000000";
                result = (HttpStatusCode.NoContent, null);
                return true;
            default:
                throw MethodNotAllowed($"{method} is not supported on '{DefaultAppManagementPolicyPath}': the tenant default app management policy only has Get and Update.", hint);
        }
    }

    private JsonObject DefaultAppManagementPolicyState()
    {
        return _defaultAppManagementPolicy ??= new JsonObject
        {
            ["id"] = "00000000-0000-0000-0000-000000000000",
            ["displayName"] = "Default app management tenant policy",
            ["description"] = "Default tenant policy that enforces app management restrictions on applications and service principals.",
            ["isEnabled"] = false,
            ["applicationRestrictions"] = new JsonObject { ["passwordCredentials"] = new JsonArray(), ["keyCredentials"] = new JsonArray() },
            ["servicePrincipalRestrictions"] = new JsonObject { ["passwordCredentials"] = new JsonArray(), ["keyCredentials"] = new JsonArray() },
        };
    }
}
