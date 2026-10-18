using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// The directory extensions of <see cref="FakeDirectory"/> applications (Learn: application-post-extensionproperty, extensionproperty): a POST to
/// <c>/applications/{id}/extensionProperties</c> stores the name as <c>extension_&lt;appId without dashes&gt;_&lt;name&gt;</c>, refuses a duplicate, an
/// unknown <c>dataType</c> or <c>targetObjects</c> value, and fills in <c>isMultiValued</c> (false), <c>isSyncedFromOnPremises</c> and <c>appDisplayName</c>.
/// There is no update (PATCH is refused by the generic child route).
/// </summary>
public sealed partial class FakeDirectory
{
    private static readonly HashSet<string> ExtensionDataTypes = new(StringComparer.Ordinal) { "Binary", "Boolean", "DateTime", "Integer", "LargeInteger", "String" };

    private static readonly HashSet<string> ExtensionTargets = new(StringComparer.Ordinal) { "User", "Group", "AdministrativeUnit", "Application", "Device", "Organization" };

    /// <summary>Seeds a directory extension on an application, as Graph stores it (the name is the short one; the prefix is added).</summary>
    public JsonObject AddExtensionProperty(string applicationId, string name, string dataType = "String", bool isMultiValued = false, bool syncedFromOnPremises = false, params string[] targetObjects)
    {
        lock (_gate)
        {
            var app = LocateById(applicationId, out _) ?? throw new ArgumentException($"No application '{applicationId}'.", nameof(applicationId));
            string navPath = "/applications/" + applicationId + "/extensionProperties";
            var body = PrepareExtensionProperty(app, navPath, new JsonObject
            {
                ["name"] = name,
                ["dataType"] = dataType,
                ["isMultiValued"] = isMultiValued,
                ["targetObjects"] = new JsonArray(targetObjects.Length == 0 ? [JsonValue.Create("User")] : targetObjects.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            }, RetryHint.Default);
            body["isSyncedFromOnPremises"] = syncedFromOnPremises;
            return (JsonObject)Create(navPath, body, RetryHint.Default).DeepClone();
        }
    }

    /// <summary>The body of a child POST, prepared the way Graph stores it for the navigations this partial models; other bodies are returned unchanged.</summary>
    private JsonNode? PrepareChildCreate(JsonObject parent, string nav, string navPath, JsonNode? body, RetryHint hint)
    {
        return nav.Equals("extensionProperties", StringComparison.OrdinalIgnoreCase) && body is JsonObject payload
            ? PrepareExtensionProperty(parent, navPath, (JsonObject)payload.DeepClone(), hint)
            : body;
    }

    private JsonObject PrepareExtensionProperty(JsonObject app, string navPath, JsonObject body, RetryHint hint)
    {
        string? name = GetString(body, "name");
        if (string.IsNullOrEmpty(name))
        {
            throw BadRequest("Property 'name' is required for an extension property.", hint);
        }

        if (GetString(body, "dataType") is not { } dataType || !ExtensionDataTypes.Contains(dataType))
        {
            throw BadRequest($"Invalid value '{GetString(body, "dataType")}' for property 'dataType'.", hint);
        }

        if (body["targetObjects"] is not JsonArray targets || targets.Count == 0 || targets.Any(t => t is not JsonValue v || !v.TryGetValue<string>(out var s) || !ExtensionTargets.Contains(s)))
        {
            throw BadRequest("Property 'targetObjects' must list User, Group, AdministrativeUnit, Application, Device or Organization (PascalCase).", hint);
        }

        string appId = GetString(app, "appId") ?? throw BadRequest("The application has no appId.", hint);
        string fullName = "extension_" + appId.Replace("-", string.Empty, StringComparison.Ordinal) + "_" + name;
        if (Collection(navPath).Any(e => ValueEquals(GetString(e, "name"), fullName)))
        {
            throw new GraphException(System.Net.HttpStatusCode.BadRequest, "Request_BadRequest", $"Property name '{fullName}' already exists on the application.", hint);
        }

        body["name"] = fullName;
        body["isMultiValued"] ??= false;
        body["isSyncedFromOnPremises"] = false;
        body["appDisplayName"] = GetString(app, "displayName");
        body["deletedDateTime"] = null;
        return body;
    }
}
