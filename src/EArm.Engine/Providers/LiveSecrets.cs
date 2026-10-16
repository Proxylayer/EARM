using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Secrets;
using EArm.Expressions.Values;

namespace EArm.Engine.Providers;

/// <summary>
/// The contract between a provider whose live object carries a <b>one-time</b> secret and the executor's output
/// evaluation. Graph returns the <c>secretText</c> of a password credential exactly once,
/// from <c>addPassword</c>; it cannot be read back. So:
/// <list type="bullet">
/// <item>in the run that creates (or rotates) the credential the provider publishes the secret under
/// <see cref="SecretTextProperty"/> as a <see cref="SecretJson"/> leaf, with <see cref="AvailabilityMarker"/> =
/// <c>true</c>; <c>reference(...).secretText</c> is then a secure-tainted value;</item>
/// <item>on every later run (<c>NoChange</c>) it publishes <c>null</c> under the same name with
/// <see cref="AvailabilityMarker"/> = <c>false</c>, so <c>reference(...).secretText</c> evaluates to <c>null</c>
/// instead of failing the deployment, and the executor reports every output whose value is (or contains) that
/// <c>null</c> with the <see cref="EngineDiagnosticCodes.SecretNotAvailableOnRerun"/> warning, as <c>null</c>, and
/// leaves it out of the secure outputs file.</item>
/// </list>
/// A user's initial password (<c>passwordProfile.password</c>) follows the same contract with its own marker,
/// <see cref="InitialPasswordAvailabilityMarker"/>: published in the creating run, <c>null</c> with the marker <c>false</c> on every
/// other run (Graph never returns a password).
/// The markers are never persisted (they live in the published live object only).
/// </summary>
public static class LiveSecrets
{
    /// <summary>The property of the live object that carries the one-time secret (a password credential's <c>secretText</c>).</summary>
    public const string SecretTextProperty = "secretText";

    /// <summary>Boolean marker next to <see cref="SecretTextProperty"/>: <c>false</c> when the secret is not available in this run.</summary>
    public const string AvailabilityMarker = "secretTextAvailable";

    /// <summary>Boolean marker on a published user: <c>false</c> when its initial password (<c>passwordProfile.password</c>) is not available in this run.</summary>
    public const string InitialPasswordAvailabilityMarker = "initialPasswordAvailable";

    /// <summary>True when <paramref name="live"/> is a published object whose secret is not available in this run.</summary>
    public static bool IsSecretTextUnavailable(JsonNode? live)
    {
        return IsMarkedUnavailable(live, AvailabilityMarker);
    }

    /// <summary>True when <paramref name="live"/> is a published user whose initial password is not available in this run.</summary>
    public static bool IsInitialPasswordUnavailable(JsonNode? live)
    {
        return IsMarkedUnavailable(live, InitialPasswordAvailabilityMarker);
    }

    /// <summary>True when <paramref name="live"/> carries any one-time secret (<c>secretText</c>, an initial password) that is not available in this run.</summary>
    public static bool IsOneTimeSecretUnavailable(JsonNode? live)
    {
        return IsSecretTextUnavailable(live) || IsInitialPasswordUnavailable(live);
    }

    private static bool IsMarkedUnavailable(JsonNode? live, string markerName)
    {
        return live is JsonObject obj
            && JsonObjects.TryGetIgnoreCase(obj, markerName, out var marker)
            && marker is JsonValue value
            && !SecretJson.IsSecret(value)
            && !UnknownJson.IsUnknown(value)
            && value.TryGetValue<bool>(out bool available)
            && !available;
    }

    /// <summary>True when <paramref name="value"/> is <c>null</c> or any leaf under it is a JSON <c>null</c>.</summary>
    public static bool ContainsNull(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return true;
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (ContainsNull(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (ContainsNull(item))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValue leaf:
                return !SecretJson.IsSecret(leaf) && !UnknownJson.IsUnknown(leaf) && leaf.GetValueKind() == JsonValueKind.Null;
            default:
                return false;
        }
    }
}
