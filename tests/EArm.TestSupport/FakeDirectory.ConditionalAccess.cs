using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>The Conditional Access rules of <see cref="FakeDirectory"/>: built-in strengths, delete refusals of referenced objects and the propagation lag.</summary>
public sealed partial class FakeDirectory
{
    // ---- conditional access rules (authentication contexts and strengths) ----

    private const string AuthenticationContextsPath = "/identity/conditionalAccess/authenticationContextClassReferences";

    private const string AuthenticationStrengthsPath = "/policies/authenticationStrengthPolicies";

    private const string ConditionalAccessPoliciesPath = "/identity/conditionalAccess/policies";

    private const string NamedLocationsPath = "/identity/conditionalAccess/namedLocations";

    /// <summary>Graph never modifies or deletes a built-in authentication strength (<c>policyType: builtIn</c>).</summary>
    private static void RejectBuiltInStrengthWrite(string collectionPath, JsonObject entity, RetryHint hint)
    {
        if (collectionPath.Equals(AuthenticationStrengthsPath, StringComparison.OrdinalIgnoreCase) && ValueEquals(GetString(entity, "policyType"), "builtIn"))
        {
            throw BadRequest("Built-in authentication strength policies cannot be modified or deleted.", hint);
        }
    }

    /// <summary>
    /// The Conditional Access delete rules Graph enforces: an authentication context must be unavailable
    /// (<c>isAvailable: false</c>) and unreferenced by any policy's <c>includeAuthenticationContextClassReferences</c>;
    /// an authentication strength must be unreferenced by any policy's <c>grantControls.authenticationStrength</c>;
    /// a named location marked trusted (<c>isTrusted: true</c>) is refused with Graph's error <c>1177</c>.
    /// </summary>
    private void RejectConditionalAccessDelete(string collectionPath, JsonObject entity, RetryHint hint)
    {
        string id = GetString(entity, "id")!;
        if (collectionPath.Equals(NamedLocationsPath, StringComparison.OrdinalIgnoreCase))
        {
            if ((entity["isTrusted"] is JsonValue trusted && trusted.GetValueKind() == JsonValueKind.True) || Linger(_lingeringTrust, id))
            {
                // The real error: 400, code 1177.
                throw new GraphException(HttpStatusCode.BadRequest, "1177", $"Named location with id {id} cannot be deleted because it is marked as a Trusted location. You have to unmark this named location before deletion.", hint);
            }

            if (Linger(_lingeringReferences, id))
            {
                throw BadRequest("The named location is referenced by one or more conditional access policies.", hint);
            }
        }
        else if (collectionPath.Equals(AuthenticationContextsPath, StringComparison.OrdinalIgnoreCase))
        {
            if (Linger(_lingeringReferences, id))
            {
                throw BadRequest("The authentication context class reference is referenced by one or more conditional access policies.", hint);
            }

            if (entity["isAvailable"] is not JsonValue available || available.GetValueKind() != JsonValueKind.False)
            {
                throw BadRequest("The authentication context class reference must be unavailable (isAvailable: false) before it can be deleted.", hint);
            }

            foreach (var policy in Collection(ConditionalAccessPoliciesPath))
            {
                if ((policy["conditions"] as JsonObject)?["applications"] is JsonObject applications
                    && applications["includeAuthenticationContextClassReferences"] is JsonArray contexts
                    && contexts.Any(c => c is JsonValue v && v.TryGetValue<string>(out var text) && ValueEquals(text, id)))
                {
                    throw BadRequest("The authentication context class reference is referenced by one or more conditional access policies.", hint);
                }
            }
        }
        else if (collectionPath.Equals(AuthenticationStrengthsPath, StringComparison.OrdinalIgnoreCase))
        {
            if (Linger(_lingeringReferences, id))
            {
                throw BadRequest("The authentication strength policy is referenced by one or more conditional access policies.", hint);
            }

            foreach (var policy in Collection(ConditionalAccessPoliciesPath))
            {
                if ((policy["grantControls"] as JsonObject)?["authenticationStrength"] is JsonObject strength && ValueEquals(GetString(strength, "id"), id))
                {
                    throw BadRequest("The authentication strength policy is referenced by one or more conditional access policies.", hint);
                }
            }
        }
    }

    /// <summary>Consumes one refused attempt of a lingering propagation lag; true while the lag lasts.</summary>
    private static bool Linger(Dictionary<string, int> lingering, string id)
    {
        if (!lingering.TryGetValue(id, out int remaining) || remaining <= 0)
        {
            return false;
        }

        lingering[id] = remaining - 1;
        return true;
    }

    /// <summary>With <see cref="ConditionalAccessLagAttempts"/>: the objects a policy being deleted references stay "referenced" for that many delete attempts.</summary>
    private void RecordReferencesLingering(string collectionPath, JsonObject entity)
    {
        if (ConditionalAccessLagAttempts <= 0 || !collectionPath.Equals(ConditionalAccessPoliciesPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var referenced = new List<string?>();
        if ((entity["grantControls"] as JsonObject)?["authenticationStrength"] is JsonObject strength)
        {
            referenced.Add(GetString(strength, "id"));
        }

        var conditions = entity["conditions"] as JsonObject;
        foreach (var array in new[]
        {
            (conditions?["applications"] as JsonObject)?["includeAuthenticationContextClassReferences"] as JsonArray,
            (conditions?["locations"] as JsonObject)?["includeLocations"] as JsonArray,
            (conditions?["locations"] as JsonObject)?["excludeLocations"] as JsonArray,
        })
        {
            referenced.AddRange(array?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var text) ? text : null) ?? []);
        }

        foreach (var id in referenced.OfType<string>())
        {
            _lingeringReferences[id] = ConditionalAccessLagAttempts;
        }
    }
}
