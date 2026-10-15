using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The pure rules of custom directory role definitions (plan sections 1.3 and 3.2): the shape of
/// <c>rolePermissions</c>, its canonical form (each entry's action lists are sets: sorted, duplicates collapsed, so
/// a reordered template is not drift), the server-managed properties a template must not declare, and the constant
/// <c>resourceScopes: ["/"]</c> Graph requires on create. No Graph here.
/// </summary>
public static class RoleDefinitionsRules
{
    /// <summary>Properties Graph assigns; a template declaring one is an error (the read strips them, so they could never converge).</summary>
    public static readonly string[] ServerManagedProperties = ["id", "templateId", "version", "isBuiltIn", "inheritsPermissionsFrom"];

    /// <summary>The properties every read selects.</summary>
    public static readonly string[] ManagedProperties =
    [
        "id", "displayName", "description", "isEnabled", "isBuiltIn", "templateId", "version", "resourceScopes", "rolePermissions", "inheritsPermissionsFrom",
    ];

    /// <summary>The only resource scope directory role definitions support.</summary>
    public const string RootScope = "/";

    /// <summary>
    /// Normalises the evaluated properties in place and returns the problems found: server-managed properties are
    /// rejected, <c>isEnabled</c> must be a boolean, <c>rolePermissions</c> must be a non-empty array of objects each
    /// carrying a non-empty <c>allowedResourceActions</c> string array (plus optional <c>excludedResourceActions</c>
    /// and <c>condition</c>), <c>resourceScopes</c> may only be <c>["/"]</c> and is removed (it is written as a
    /// constant on create and never read back). Action lists are put into canonical set form.
    /// </summary>
    public static IReadOnlyList<string> NormalizeDesired(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var problems = new List<string>();

        foreach (string managed in ServerManagedProperties)
        {
            if (JsonObjects.TryGetIgnoreCase(properties, managed, out var node) && !JsonObjects.IsAbsent(node))
            {
                problems.Add($"properties.{managed} is assigned by Graph and cannot be declared");
            }
        }

        if (JsonObjects.TryGetIgnoreCase(properties, "isEnabled", out var enabled) && !JsonObjects.IsAbsent(enabled) && !UnknownJson.IsUnknown(enabled) && !JsonValues.TryGetBool(enabled, out _))
        {
            problems.Add($"properties.isEnabled must be a boolean, got {JsonValues.Describe(enabled)}");
        }

        if (JsonObjects.FindKey(properties, "resourceScopes") is { } scopesKey)
        {
            var scopes = properties[scopesKey];
            if (!JsonObjects.IsAbsent(scopes) && !UnknownJson.IsUnknown(scopes) && !IsRootScopeOnly(scopes))
            {
                problems.Add($"properties.resourceScopes may only be [\"{RootScope}\"] for directory roles (got {JsonValues.Describe(scopes)}); omit it");
            }

            properties.Remove(scopesKey);
        }

        if (JsonObjects.TryGetIgnoreCase(properties, "rolePermissions", out var permissionsNode) && !JsonObjects.IsAbsent(permissionsNode) && !UnknownJson.IsUnknown(permissionsNode))
        {
            if (permissionsNode is not JsonArray permissions)
            {
                problems.Add($"properties.rolePermissions must be an array of {{ allowedResourceActions: [...] }} objects, got {JsonValues.Describe(permissionsNode)}");
            }
            else if (permissions.Count == 0)
            {
                problems.Add("properties.rolePermissions must contain at least one { allowedResourceActions: [...] } entry");
            }
            else
            {
                for (int i = 0; i < permissions.Count; i++)
                {
                    if (permissions[i] is not JsonObject entry)
                    {
                        if (!UnknownJson.IsUnknown(permissions[i]))
                        {
                            problems.Add($"properties.rolePermissions[{i}] must be an object, got {JsonValues.Describe(permissions[i])}");
                        }

                        continue;
                    }

                    foreach (var property in entry.ToList())
                    {
                        if (!property.Key.Equals("allowedResourceActions", StringComparison.OrdinalIgnoreCase)
                            && !property.Key.Equals("excludedResourceActions", StringComparison.OrdinalIgnoreCase)
                            && !property.Key.Equals("condition", StringComparison.OrdinalIgnoreCase))
                        {
                            problems.Add($"properties.rolePermissions[{i}].{property.Key} is not a role permission property; expected allowedResourceActions, excludedResourceActions or condition");
                        }
                    }

                    if (!JsonObjects.TryGetIgnoreCase(entry, "allowedResourceActions", out var allowed) || JsonObjects.IsAbsent(allowed))
                    {
                        problems.Add($"properties.rolePermissions[{i}] needs allowedResourceActions (a non-empty array of resource actions such as microsoft.directory/users/standard/read)");
                    }
                    else if (NormalizeActions(entry, "allowedResourceActions", $"properties.rolePermissions[{i}].allowedResourceActions", problems) is { } count && count == 0)
                    {
                        problems.Add($"properties.rolePermissions[{i}].allowedResourceActions must not be empty");
                    }

                    NormalizeActions(entry, "excludedResourceActions", $"properties.rolePermissions[{i}].excludedResourceActions", problems);
                }
            }
        }

        return problems;
    }

    /// <summary>The live diff view: the same canonical action sets as a normalised desired object.</summary>
    public static JsonObject NormalizeLive(JsonObject stripped)
    {
        ArgumentNullException.ThrowIfNull(stripped);
        var copy = (JsonObject)JsonValues.Clone(stripped)!;
        if (JsonObjects.TryGetIgnoreCase(copy, "rolePermissions", out var node) && node is JsonArray permissions)
        {
            foreach (var entry in permissions.OfType<JsonObject>())
            {
                NormalizeActions(entry, "allowedResourceActions", null, null);
                NormalizeActions(entry, "excludedResourceActions", null, null);
                if (JsonObjects.FindKey(entry, "condition") is { } conditionKey && JsonObjects.IsAbsent(entry[conditionKey]))
                {
                    entry.Remove(conditionKey);
                }

                if (JsonObjects.FindKey(entry, "excludedResourceActions") is { } excludedKey && entry[excludedKey] is JsonArray { Count: 0 })
                {
                    entry.Remove(excludedKey);
                }
            }
        }

        return copy;
    }

    /// <summary>True when the live object is a built-in role (<c>isBuiltIn: true</c>), which Graph never lets a caller manage.</summary>
    public static bool IsBuiltIn(JsonObject? live)
    {
        return JsonObjects.TryGetIgnoreCase(live, "isBuiltIn", out var node) && JsonValues.TryGetBool(node, out bool builtIn) && builtIn;
    }

    /// <summary>The message for a built-in role matched by name: it cannot be managed, only referenced.</summary>
    public static string BuiltInMessage(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        return $"'{displayName}' is a built-in directory role and cannot be managed as a Microsoft.Entra/roleDefinitions resource; reference it with roleTemplate('{displayName.Replace("'", "''", StringComparison.Ordinal)}') instead, or choose another displayName for the custom role.";
    }

    /// <summary>Removes the server-managed properties from a request body (defensive: the plan already rejects them).</summary>
    public static void RemoveServerManaged(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (string property in ServerManagedProperties)
        {
            if (JsonObjects.FindKey(body, property) is { } key)
            {
                body.Remove(key);
            }
        }
    }

    /// <summary>Puts the constant <c>resourceScopes: ["/"]</c> into a create body.</summary>
    public static void ApplyRootScope(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        body[JsonObjects.FindKey(body, "resourceScopes") ?? "resourceScopes"] = new JsonArray(RootScope);
    }

    private static bool IsRootScopeOnly(JsonNode? node)
    {
        return node is JsonArray { Count: 1 } array && JsonValues.TryGetString(array[0], out var scope) && scope.Trim() == RootScope;
    }

    /// <summary>
    /// Sorts and de-duplicates a string array property (ordinal, case-insensitive: Graph treats resource actions
    /// case-insensitively). Returns the member count, or <c>null</c> when the property is absent or not a plain
    /// string array (a problem is reported when <paramref name="problems"/> is given; unknown leaves pass).
    /// </summary>
    private static int? NormalizeActions(JsonObject entry, string property, string? path, List<string>? problems)
    {
        if (!JsonObjects.TryGetIgnoreCase(entry, property, out var node) || JsonObjects.IsAbsent(node) || UnknownJson.IsUnknown(node))
        {
            return null;
        }

        if (node is not JsonArray array)
        {
            problems?.Add($"{path} must be an array of strings, got {JsonValues.Describe(node)}");
            return null;
        }

        var values = new List<string>();
        foreach (var item in array)
        {
            if (UnknownJson.IsUnknown(item))
            {
                // A deploy-time value: leave the array as declared.
                return array.Count;
            }

            if (!JsonValues.TryGetString(item, out var text) || string.IsNullOrWhiteSpace(text))
            {
                problems?.Add($"{path} must contain non-empty strings, got {JsonValues.Describe(item)}");
                return null;
            }

            if (!values.Contains(text.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                values.Add(text.Trim());
            }
        }

        values.Sort(StringComparer.OrdinalIgnoreCase);
        var sorted = new JsonArray();
        foreach (string value in values)
        {
            sorted.Add(value);
        }

        entry[JsonObjects.FindKey(entry, property)!] = sorted;
        return values.Count;
    }
}
