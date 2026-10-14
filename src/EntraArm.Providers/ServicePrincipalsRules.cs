using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Secrets;
using EntraArm.Engine.Providers;
using EntraArm.Expressions.Values;

namespace EntraArm.Providers;

/// <summary>
/// The pure rules of the service principals provider (plan sections 1.3 and 3.2), kept free of Graph I/O so they can
/// be tested on their own:
/// <list type="bullet">
/// <item>the resource name is the application (client) id and must be a GUID once evaluated;</item>
/// <item>a declared <c>properties.appId</c> must equal the name (GUID-aware); an equal value is re-spelled to the name so the create body's name injection accepts it;</item>
/// <item>the server-assigned properties (<c>id</c>, <c>servicePrincipalType</c>, and <c>appId</c> on a PATCH) never reach a request body;</item>
/// <item>the create body is ordered <c>appId</c> first, then the overlay defaults, then the declared properties (deterministic bodies for logs and tests);</item>
/// <item>the live diff view gets the immutable passthrough properties (<c>appId</c>, <c>servicePrincipalType</c>) copied back from the raw object when the template declares them, so a declared equal value is not drift and a different one is an immutable-change error;</item>
/// <item>the permission rule: <c>Application.ReadWrite.OwnedBy</c> suffices only when the principal's application is a resource of the same template.</item>
/// </list>
/// </summary>
public static class ServicePrincipalsRules
{
    /// <summary>Properties Graph assigns; never written (<c>appId</c> is written on create only, as the key).</summary>
    public static readonly IReadOnlyList<string> ServerAssignedProperties = ["id", "servicePrincipalType"];

    /// <summary>Immutable properties copied from the raw live object into the diff view when the template declares them.</summary>
    public static readonly IReadOnlyList<string> PassthroughProperties = ["appId", "servicePrincipalType"];

    // ---- name ----

    /// <summary>True when <paramref name="name"/> is a GUID (the application id the principal is created for).</summary>
    public static bool IsAppId(string? name)
    {
        return name is not null && Guid.TryParse(name, out _);
    }

    /// <summary>
    /// Mirrors <see cref="ProviderBase.InjectName"/> at plan time: a declared <c>properties.appId</c> must equal the
    /// resource name. An equal GUID spelled differently (case, braces) is rewritten to the name in place so the
    /// create body's exact comparison accepts it; a different value yields the error message. Secret or
    /// deploy-time-unknown values are left alone.
    /// </summary>
    public static string? ReconcileDeclaredAppId(JsonObject properties, string name, string describe)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(describe);

        if (JsonObjects.FindKey(properties, "appId") is not { } key)
        {
            return null;
        }

        var declared = properties[key];
        if (JsonObjects.IsAbsent(declared) || SecretJson.IsSecret(declared) || UnknownJson.IsUnknown(declared))
        {
            return null;
        }

        if (JsonValues.TryGetString(declared, out var text) && JsonObjects.KeyEquals(text, name))
        {
            if (!string.Equals(text, name, StringComparison.Ordinal))
            {
                properties[key] = name;
            }

            return null;
        }

        return $"{describe}: properties.appId ({JsonValues.Render(declared)}) conflicts with the resource name '{name}'; the name is the application id the principal is created for, so either omit the property or make it equal ({DiagnosticCodes.NameMappedPropertyConflict}).";
    }

    // ---- bodies ----

    /// <summary>Removes <c>id</c> and <c>servicePrincipalType</c> from a request body, plus <c>appId</c> unless <paramref name="keepAppId"/> (a create needs it).</summary>
    public static void RemoveServerAssigned(JsonObject body, bool keepAppId)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (string property in ServerAssignedProperties)
        {
            if (JsonObjects.FindKey(body, property) is { } key)
            {
                body.Remove(key);
            }
        }

        if (!keepAppId && JsonObjects.FindKey(body, "appId") is { } appIdKey)
        {
            body.Remove(appIdKey);
        }
    }

    /// <summary>
    /// A copy of <paramref name="body"/> with <paramref name="leading"/> properties first (in that order, when
    /// present) and the remaining properties in their original order: <c>appId</c>, the overlay defaults, then the
    /// declared properties.
    /// </summary>
    public static JsonObject OrderCreateBody(JsonObject body, IEnumerable<string> leading)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(leading);

        var ordered = new JsonObject();
        foreach (string name in leading)
        {
            if (JsonObjects.FindKey(body, name) is { } key && !ordered.ContainsKey(key))
            {
                ordered[key] = JsonValues.Clone(body[key]);
            }
        }

        foreach (var pair in body)
        {
            if (!ordered.ContainsKey(pair.Key))
            {
                ordered[pair.Key] = JsonValues.Clone(pair.Value);
            }
        }

        return ordered;
    }

    // ---- live side ----

    /// <summary>
    /// The diff view of the live object (a copy of the stripped properties) with the immutable passthrough
    /// properties the template declares (<c>appId</c>, <c>servicePrincipalType</c>) copied back from the raw object,
    /// so they take part in the diff: an equal declared value is not drift, a different one is an immutable change.
    /// </summary>
    public static JsonObject NormalizeLive(ExistingResource existing, JsonObject desired)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(desired);
        var live = (JsonObject)JsonValues.Clone(existing.Properties)!;
        foreach (string passthrough in PassthroughProperties)
        {
            if (JsonObjects.FindKey(desired, passthrough) is not null && JsonObjects.TryGetIgnoreCase(existing.Raw, passthrough, out var raw) && !JsonObjects.IsAbsent(raw))
            {
                live[JsonObjects.FindKey(live, passthrough) ?? passthrough] = JsonValues.Clone(raw);
            }
        }

        return live;
    }

    // ---- permissions ----

    /// <summary>
    /// True when any of <paramref name="templateTargets"/> (the instance's dependencies and the targets of its name
    /// expression) is a <c>Microsoft.Entra/applications</c> resource: the principal is created for an application
    /// this deployment manages, which <c>Application.ReadWrite.OwnedBy</c> covers. Otherwise the application is
    /// pre-existing or first-party and <c>Application.ReadWrite.All</c> is needed (plan section 3.4).
    /// </summary>
    public static bool ReferencesTemplateApplication(IEnumerable<ResourceIdentifier> templateTargets)
    {
        ArgumentNullException.ThrowIfNull(templateTargets);
        return templateTargets.Any(id => ResourceIdentifier.TypeEquals(id.Type, ApplicationsProvider.ResourceType));
    }
}
