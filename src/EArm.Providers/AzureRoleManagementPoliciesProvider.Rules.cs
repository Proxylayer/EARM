using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>Rule handling for <see cref="AzureRoleManagementPoliciesProvider"/>: the offline findings, policy scopes, normalisation, declared and live rule alignment, the PATCH body, and the property accessors.</summary>
public sealed partial class AzureRoleManagementPoliciesProvider
{
    // ---- helpers ----

    /// <summary>The offline findings of a declaration, joined, plus a scope ARM cannot be sent (<see cref="ArmClient.IsValidResourcePath"/>); empty when there are none.</summary>
    private static string Findings(JsonObject properties)
    {
        var messages = AzureRoleManagementPolicyRules.Check(properties, IsOpaque).Select(f => f.Message).ToList();
        if (messages.Count == 0 && Text(properties, "scope") is { } scope && AzureRoleManagementPolicyRules.NormalizeScope(scope) is { } normalized && !ArmClient.IsValidResourcePath(normalized))
        {
            messages.Add($"properties.scope '{scope}' has characters Azure Resource Manager paths cannot carry unescaped (letters, digits and - . _ ~ ! $ & ' ( ) * + , ; = : @ only)");
        }

        return string.Join("; ", messages);
    }

    /// <summary>The scope a key names.</summary>
    private AzurePolicyScope ScopeOf(ResourceKey key, ResourceIdentifier? id)
    {
        if (!key.TryGet("scope", out var scope) || !key.TryGet("roleDefinitionId", out var roleDefinitionId)
            || AzureRoleManagementPolicyRules.NormalizeScope(scope) is not { } normalized
            || AzureRoleManagementPolicyRules.QualifiedRoleDefinitionId(normalized, roleDefinitionId) is not { } role)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry an Azure scope and a role definition id.", id);
        }

        return new AzurePolicyScope(normalized, role);
    }

    /// <summary>The scope prepared properties name, or <c>null</c> when part of it is not known.</summary>
    private static AzurePolicyScope? ScopeOf(JsonObject properties)
    {
        return AzureRoleManagementPolicyRules.NormalizeScope(Text(properties, "scope")) is { } scope
            && AzureRoleManagementPolicyRules.QualifiedRoleDefinitionId(scope, Text(properties, "roleDefinitionId")) is { } role
            ? new AzurePolicyScope(scope, role)
            : null;
    }

    private static ExistingResource ToExisting(AzurePolicy policy, AzurePolicyScope scope)
    {
        return new ExistingResource(policy.PolicyPath, AzureRoleManagementPolicyLookup.LiveView(policy, scope), (JsonObject)JsonValues.Clone(policy.Policy)!);
    }

    /// <summary>The scope, the role definition id (subscription-qualified) and the declared rules in canonical form; opaque values are left alone.</summary>
    private static void Normalize(JsonObject properties)
    {
        if (Text(properties, "scope") is { } declaredScope && AzureRoleManagementPolicyRules.NormalizeScope(declaredScope) is { } scope)
        {
            Set(properties, "scope", JsonValue.Create(scope));
            if (AzureRoleManagementPolicyRules.QualifiedRoleDefinitionId(scope, Text(properties, "roleDefinitionId")) is { } role)
            {
                Set(properties, "roleDefinitionId", JsonValue.Create(role));
            }
        }

        if (Get(properties, "rules") is JsonArray rules && !IsOpaque(rules))
        {
            RoleManagementPolicyRules.NormalizeRules(rules, IsOpaque);
        }
    }

    /// <summary>The declared rules (objects with a known id), or <c>null</c> when the array or a rule id is only known at deployment.</summary>
    private static List<JsonObject>? DeclaredRules(JsonObject properties)
    {
        if (Get(properties, "rules") is not JsonArray rules || UnknownJson.ContainsUnknown(rules))
        {
            return null;
        }

        var declared = rules.OfType<JsonObject>().ToList();
        return declared.Count == rules.Count && declared.All(r => JsonObjects.GetId(r) is not null) ? declared : null;
    }

    /// <summary>The live rules by id.</summary>
    private static Dictionary<string, JsonObject> LiveRules(JsonObject? live)
    {
        var rules = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (live is not null && JsonObjects.TryGetIgnoreCase(live, "rules", out var node) && node is JsonArray array)
        {
            foreach (var rule in array.OfType<JsonObject>())
            {
                if (JsonObjects.GetId(rule) is { } id)
                {
                    rules[id] = rule;
                }
            }
        }

        return rules;
    }

    /// <summary>The live view reduced to the declared rules (live-only rules are not managed), in the declared order.</summary>
    private static JsonObject AlignedLive(JsonObject? live, IReadOnlyList<JsonObject> declared)
    {
        var aligned = live is null ? new JsonObject() : (JsonObject)JsonValues.Clone(live)!;
        var liveRules = LiveRules(live);
        aligned["rules"] = new JsonArray(declared.Select(r => (JsonNode?)JsonValues.Clone(liveRules[JsonObjects.GetId(r)!])).ToArray());
        return aligned;
    }

    /// <summary>True when the (filled, comparable) desired rule differs from the live rule anywhere.</summary>
    private static bool Differs(JsonObject desired, JsonObject live)
    {
        var deltas = JsonDiff.Diff(new JsonObject { ["rule"] = JsonValues.Clone(desired) }, new JsonObject { ["rule"] = JsonValues.Clone(live) });
        return deltas.Any(d => d.HasEffect);
    }

    /// <summary>A rule without its null-valued object members (recursively; an explicit <c>[]</c> is kept, it clears a list): the members ARM returned as null are not sent.</summary>
    private static JsonObject WithoutNulls(JsonObject rule)
    {
        foreach (var key in rule.Where(p => JsonObjects.IsAbsent(p.Value)).Select(p => p.Key).ToList())
        {
            rule.Remove(key);
        }

        foreach (var child in rule.Select(p => p.Value).ToList())
        {
            switch (child)
            {
                case JsonObject obj:
                    WithoutNulls(obj);
                    break;
                case JsonArray array:
                    foreach (var item in array.OfType<JsonObject>())
                    {
                        WithoutNulls(item);
                    }

                    break;
            }
        }

        return rule;
    }

    private static ProviderException Invalid(ResourceInstance desired, string message)
    {
        return new ProviderException(InvalidCode, $"{desired.Describe()}: {message}", desired.Id);
    }

    /// <summary>A deploy-time-unknown or secret value cannot be judged at plan time.</summary>
    private static bool IsOpaque(JsonNode? node)
    {
        return UnknownJson.IsUnknown(node) || SecretJson.IsSecret(node, out _);
    }

    private static JsonNode? Get(JsonObject properties, string name)
    {
        return JsonObjects.TryGetIgnoreCase(properties, name, out var value) && !JsonObjects.IsAbsent(value) ? value : null;
    }

    private static string? Text(JsonObject properties, string name)
    {
        return Get(properties, name) is { } node && !IsOpaque(node) && JsonValues.TryGetString(node, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    private static JsonNode? Unknown(JsonObject properties, string name)
    {
        return Get(properties, name) is { } node && UnknownJson.IsUnknown(node) ? JsonValues.Clone(node) : null;
    }

    private static void Set(JsonObject properties, string name, JsonNode? value)
    {
        properties[JsonObjects.FindKey(properties, name) ?? name] = value;
    }

    private static void Remove(JsonObject properties, string name)
    {
        if (JsonObjects.FindKey(properties, name) is { } key)
        {
            properties.Remove(key);
        }
    }
}
