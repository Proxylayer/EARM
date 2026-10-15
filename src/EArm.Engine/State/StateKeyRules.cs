using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Core;
using EArm.Core.Types;

namespace EArm.Engine.State;

/// <summary>
/// What a state entry's <c>key</c> and <c>relationships</c> are allowed to look like (review round 2, finding S2-01). The state file
/// is committed and editable by anyone with write access to the repository; the key of an entry decides which live object a
/// state-driven delete re-reads and verifies, so it must be the key the entry's own resource id implies: for a name-keyed type the
/// <c>nameMapsTo</c> property (<c>uniqueName</c>, <c>userPrincipalName</c>, <c>displayName</c>...) with the last name segment as its
/// value, for a composite (GUID-named) type exactly the catalog's <c>keyProperties</c>. A tampered entry (for example a group
/// entry with <c>key: {securityEnabled: "true"}</c> or <c>{displayName: "&lt;someone else's group&gt;"}</c> and the victim's id)
/// is rejected when the state is loaded (<see cref="StateEntryInvalidException"/>) and again before every delete.
/// </summary>
public static partial class StateKeyRules
{
    /// <summary>Extra key properties a provider may add next to the name-mapped one (an application's declared <c>appId</c> adoption key).</summary>
    private static readonly string[] AllowedExtraKeys = ["appId"];

    /// <summary>Why the entry's key is not the key its resource id implies; <c>null</c> when it is (or when the type is not in the catalog, which no provider could act on).</summary>
    /// <param name="allowEmpty">
    /// <c>true</c> at load: an entry without any key (written by a tool that did not record it) is accepted, and <see cref="DerivedKey"/> supplies the key of a name-keyed type;
    /// <c>false</c> before a state-driven write or delete, which must never run on an empty key.
    /// </param>
    public static string? Validate(ResourceTypeCatalog catalog, ResourceIdentifier id, IReadOnlyDictionary<string, string> key, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(key);

        var definition = catalog.Find(id.Type);
        if (definition is null || (allowEmpty && key.Count == 0))
        {
            return null;
        }

        if (definition.NameMapsTo is { } property)
        {
            string? recorded = null;
            foreach (var pair in key)
            {
                if (pair.Key.Equals(property, StringComparison.OrdinalIgnoreCase))
                {
                    recorded = pair.Value;
                    continue;
                }

                if (!AllowedExtraKeys.Any(extra => extra.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)) || !definition.Type.EndsWith("/applications", StringComparison.OrdinalIgnoreCase))
                {
                    return $"its key carries '{pair.Key}', which is not part of the key of {definition.Type} ('{property}')";
                }
            }

            if (recorded is null)
            {
                return $"its key has no '{property}' (the key of {definition.Type} is '{property}' = the last segment of the resource id)";
            }

            if (!string.Equals(recorded.Trim(), id.NameLeaf.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return $"its key says {property} = '{recorded}', but its resource id names '{id.NameLeaf}'";
            }

            return null;
        }

        // Composite (GUID-named) types: the key names are the catalog's key properties (a member may be absent: the oauth2PermissionGrants principalId of an AllPrincipals grant).
        if (definition.KeyProperties.Count == 0)
        {
            return null;
        }

        if (key.Count == 0)
        {
            return $"its key is empty (the key of {definition.Type} is {string.Join(", ", definition.KeyProperties)})";
        }

        foreach (var name in key.Keys)
        {
            if (!definition.KeyProperties.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return $"its key carries '{name}', which is not part of the key of {definition.Type} ({string.Join(", ", definition.KeyProperties)})";
            }
        }

        bool lastMemberOptional = definition.Type.EndsWith("/oauth2PermissionGrants", StringComparison.OrdinalIgnoreCase);
        foreach (var required in definition.KeyProperties)
        {
            bool optional = lastMemberOptional && required.Equals("principalId", StringComparison.OrdinalIgnoreCase);
            if (!optional && !key.Keys.Any(k => k.Equals(required, StringComparison.OrdinalIgnoreCase)))
            {
                return $"its key has no '{required}' (the key of {definition.Type} is {string.Join(", ", definition.KeyProperties)})";
            }
        }

        return null;
    }

    /// <summary>The key a name-keyed type implies for a resource id (<c>nameMapsTo</c> = the last name segment); <c>null</c> for composite types and types outside the catalog.</summary>
    public static IReadOnlyDictionary<string, string>? DerivedKey(ResourceTypeCatalog catalog, ResourceIdentifier id)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(id);
        return catalog.Find(id.Type) is { NameMapsTo: { } property } ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [property] = id.NameLeaf } : null;
    }

    /// <summary>
    /// Why a recorded relationship entry carries an id that cannot be a Graph id (an id ends up in a delete path), or <c>null</c>.
    /// Entries are target ids (strings) or objects (<c>id</c>, <c>principalId</c>, <c>resourceId</c>, <c>appRoleId</c>, <c>roleDefinitionId</c>, <c>directoryScopeId</c>).
    /// </summary>
    public static string? ValidateRelationships(JsonObject? relationships)
    {
        if (relationships is null)
        {
            return null;
        }

        foreach (var (property, value) in relationships)
        {
            var records = value is JsonArray array ? array.ToList() : [value];
            foreach (var record in records)
            {
                string? problem = ValidateRecord(record);
                if (problem is not null)
                {
                    return $"relationships.{property}: {problem}";
                }
            }
        }

        return null;
    }

    private static string? ValidateRecord(JsonNode? record)
    {
        switch (record)
        {
            case null:
                return null;

            case JsonValue value:
                if (value.TryGetValue<string>(out string? text))
                {
                    return StateIdShapes.IsValidGraphId("Microsoft.Entra/users", text) ? null : $"'{text}' is not a GUID";
                }

                return "an entry is neither an id nor an object";

            case JsonObject obj:
                foreach (var (name, node) in obj)
                {
                    if (node is null)
                    {
                        continue;
                    }

                    if (node is not JsonValue member || !member.TryGetValue<string>(out string? memberText))
                    {
                        // Extra data of a record (booleans, numbers) cannot reach a path; nested structures are not produced by any kind.
                        if (node is JsonObject or JsonArray)
                        {
                            return $"'{name}' is a nested structure";
                        }

                        continue;
                    }

                    bool valid = name.ToLowerInvariant() switch
                    {
                        "id" => OpaqueId().IsMatch(memberText),
                        "principalid" or "resourceid" or "approleid" or "roledefinitionid" or "targetid" => StateIdShapes.IsValidGraphId("Microsoft.Entra/users", memberText),
                        "directoryscopeid" => ScopeId().IsMatch(memberText),
                        _ => memberText.Length <= 512 && !memberText.Any(char.IsControl),
                    };
                    if (!valid)
                    {
                        return $"'{name}' = '{Truncate(memberText)}' is not a valid Graph id";
                    }
                }

                return null;

            default:
                return "an entry is neither an id nor an object";
        }
    }

    private static string Truncate(string text)
    {
        return text.Length <= 60 ? text : text[..60] + "...";
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_\-=]{1,200}\z", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueId();

    [GeneratedRegex(@"\A/([A-Za-z0-9_\-]{1,64}(/[A-Za-z0-9_\-]{1,64}){0,3})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ScopeId();
}
