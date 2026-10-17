using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace EArm.TestSupport;

/// <summary>The actions of <see cref="FakeDirectory"/>: <c>addPassword</c>, <c>removePassword</c> and <c>assignLicense</c>.</summary>
public sealed partial class FakeDirectory
{
    // ---- actions ----

    private static JsonObject AddPassword(JsonObject entity, JsonNode? body)
    {
        var request = (body as JsonObject)?["passwordCredential"] as JsonObject ?? new JsonObject();

        // Everything the caller sent is stored (Graph keeps displayName/start/end; the fake keeps extras so a
        // round-trip diff of the echo provider is a no-op), plus the server-assigned fields.
        var credential = (JsonObject)request.DeepClone();
        credential["keyId"] = Guid.NewGuid().ToString("D");
        credential["startDateTime"] ??= JsonValue.Create(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        credential["endDateTime"] ??= JsonValue.Create(DateTimeOffset.UtcNow.AddDays(180).ToString("O", CultureInfo.InvariantCulture));
        credential["hint"] = "fak";
        credential["customKeyIdentifier"] = null;

        if (entity["passwordCredentials"] is not JsonArray credentials)
        {
            credentials = [];
            entity["passwordCredentials"] = credentials;
        }

        credentials.Add(credential.DeepClone());
        var response = (JsonObject)credential.DeepClone();
        response["secretText"] = "fake-secret-" + Guid.NewGuid().ToString("N");
        return response;
    }

    private static void RemovePassword(JsonObject entity, JsonNode? body)
    {
        string? keyId = (body as JsonObject)?["keyId"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (keyId is null || entity["passwordCredentials"] is not JsonArray credentials)
        {
            return;
        }

        for (int i = credentials.Count - 1; i >= 0; i--)
        {
            if (credentials[i] is JsonObject credential && ValueEquals(GetString(credential, "keyId"), keyId))
            {
                credentials.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// <c>POST …/assignLicense</c>. For users (<paramref name="trackStates"/>) the fake also keeps
    /// <c>licenseAssignmentStates</c> like Graph does: every direct add gets an entry with <c>assignedByGroup: null</c>
    /// and a direct remove drops that entry only, so a seeded group-inherited state (<c>assignedByGroup</c> set)
    /// survives and lets the users provider tell direct from inherited assignments.
    /// </summary>
    private static void AssignLicense(JsonObject entity, JsonNode? body, bool trackStates = false)
    {
        if (body is not JsonObject payload)
        {
            return;
        }

        if (entity["assignedLicenses"] is not JsonArray licenses)
        {
            licenses = [];
            entity["assignedLicenses"] = licenses;
        }

        JsonArray? states = null;
        if (trackStates)
        {
            if (entity["licenseAssignmentStates"] is not JsonArray existingStates)
            {
                existingStates = [];
                entity["licenseAssignmentStates"] = existingStates;
            }

            states = existingStates;
        }

        if (payload["removeLicenses"] is JsonArray removals)
        {
            foreach (var removal in removals)
            {
                string? skuId = removal is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                for (int i = licenses.Count - 1; i >= 0; i--)
                {
                    if (licenses[i] is JsonObject license && ValueEquals(GetString(license, "skuId"), skuId))
                    {
                        licenses.RemoveAt(i);
                    }
                }

                if (states is not null)
                {
                    for (int i = states.Count - 1; i >= 0; i--)
                    {
                        if (states[i] is JsonObject state && ValueEquals(GetString(state, "skuId"), skuId) && GetString(state, "assignedByGroup") is null)
                        {
                            states.RemoveAt(i);
                        }
                    }
                }
            }
        }

        if (payload["addLicenses"] is JsonArray additions)
        {
            foreach (var addition in additions)
            {
                if (addition is JsonObject license)
                {
                    licenses.Add(license.DeepClone());
                    if (states is not null)
                    {
                        states.Add(new JsonObject
                        {
                            ["skuId"] = GetString(license, "skuId"),
                            ["disabledPlans"] = license["disabledPlans"]?.DeepClone() ?? new JsonArray(),
                            ["assignedByGroup"] = null,
                            ["state"] = "Active",
                            ["error"] = "None",
                        });
                    }
                }
            }
        }
    }
}
