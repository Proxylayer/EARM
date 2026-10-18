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

namespace EArm.Providers;

/// <summary>Rule handling for <see cref="RoleManagementPoliciesProvider"/>: policy scopes, normalization, declared and live rule alignment, and the property accessors.</summary>
public sealed partial class RoleManagementPoliciesProvider
{
    // ---- helpers ----

    /// <summary>The scope a key names.</summary>
    private PolicyScope ScopeOf(ResourceKey key, ResourceIdentifier? id)
    {
        if (!key.TryGet("scopeType", out var scopeType) || !key.TryGet("scopeId", out var scopeId) || !key.TryGet("roleDefinitionId", out var roleDefinitionId)
            || RoleManagementPolicyRules.NormalizeScopeType(scopeType) is not { } kind)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry scopeType, scopeId and roleDefinitionId.", id);
        }

        return Scope(kind, scopeId, roleDefinitionId);
    }

    /// <summary>The scope prepared properties name, or <c>null</c> when part of it is not known.</summary>
    private static PolicyScope? ScopeOf(JsonObject properties)
    {
        return Text(properties, "scopeType") is { } scopeType && RoleManagementPolicyRules.NormalizeScopeType(scopeType) is { } kind
            && Text(properties, "scopeId") is { } scopeId && Text(properties, "roleDefinitionId") is { } roleDefinitionId
            ? Scope(kind, scopeId, roleDefinitionId)
            : null;
    }

    private static PolicyScope Scope(string kind, string scopeId, string roleDefinitionId)
    {
        return kind == RoleManagementPolicyRules.GroupScope
            ? new PolicyScope(kind, RoleAssignmentsRules.IsGuid(scopeId) ? RoleAssignmentsRules.NormalizeId(scopeId) : scopeId.Trim(), GroupEligibilityScheduleRules.NormalizeAccessId(roleDefinitionId) ?? roleDefinitionId.Trim())
            : new PolicyScope(kind, RoleManagementPolicyRules.RootScopeId, RoleAssignmentsRules.IsGuid(roleDefinitionId) ? RoleAssignmentsRules.NormalizeId(roleDefinitionId) : roleDefinitionId.Trim());
    }

    private static ExistingResource ToExisting(JsonObject assignment, PolicyScope scope)
    {
        string policyId = RoleManagementPolicyLookup.PolicyId(assignment)
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"The PIM policy assignment of {scope.Describe()} names no policy.");
        return new ExistingResource(policyId, RoleManagementPolicyLookup.LiveView(assignment, scope), (JsonObject)JsonValues.Clone(assignment)!);
    }

    /// <summary>The scope values and the declared rules in canonical form (Graph casing, ids, enums); opaque values are left alone.</summary>
    private static void Normalize(JsonObject properties)
    {
        if (Text(properties, "scopeType") is { } scopeType && RoleManagementPolicyRules.NormalizeScopeType(scopeType) is { } kind)
        {
            Set(properties, "scopeType", JsonValue.Create(kind));
            if (Text(properties, "scopeId") is { } scopeId && RoleAssignmentsRules.IsGuid(scopeId))
            {
                Set(properties, "scopeId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(scopeId)));
            }

            if (Text(properties, "roleDefinitionId") is { } roleDefinitionId)
            {
                Set(properties, "roleDefinitionId", JsonValue.Create(kind == RoleManagementPolicyRules.GroupScope
                    ? GroupEligibilityScheduleRules.NormalizeAccessId(roleDefinitionId) ?? roleDefinitionId
                    : RoleAssignmentsRules.IsGuid(roleDefinitionId) ? RoleAssignmentsRules.NormalizeId(roleDefinitionId) : roleDefinitionId));
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

    private static void ReplaceRules(JsonObject properties, IEnumerable<JsonObject> rules)
    {
        Set(properties, "rules", new JsonArray(rules.Select(r => (JsonNode?)r).ToArray()));
    }

    /// <summary>True when the (filled, comparable) desired rule differs from the live rule anywhere.</summary>
    private static bool Differs(JsonObject desired, JsonObject live)
    {
        var deltas = JsonDiff.Diff(new JsonObject { ["rule"] = JsonValues.Clone(desired) }, new JsonObject { ["rule"] = JsonValues.Clone(live) });
        return deltas.Any(d => d.HasEffect);
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

    /// <summary>Opaque, or (on an unevaluated declaration) a template expression.</summary>
    private static bool IsOpaqueOrExpression(JsonNode? node)
    {
        return IsOpaque(node) || (node is JsonValue value && value.TryGetValue(out string? text) && ExpressionSyntax.IsExpression(text));
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
