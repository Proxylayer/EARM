using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>Planning for <see cref="AzureRoleManagementPoliciesProvider"/>: the rule diff against the live policy, the missing policy and the unknown and unsupported changes.</summary>
public sealed partial class AzureRoleManagementPoliciesProvider
{
    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        if (desired.IsIgnored || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        if (!desired.IsSymbolic && !RoleAssignmentsRules.IsGuid(desired.Id.NameLeaf))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name '{desired.Id.NameLeaf}' is not a GUID; name Azure role management policies with a deterministic GUID, for example [guid('azure-pim-policy', parameters('resourceGroupName'), 'Reader')].", InvalidCode);
        }

        if (Findings(properties) is { Length: > 0 } findings)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {findings}.", InvalidCode);
        }

        Normalize(properties);
        if (existing is null)
        {
            return PlanMissing(desired, properties);
        }

        if (DeclaredRules(properties) is not { } declared)
        {
            return Unknown(desired, existing, "the rules are only known at deployment time");
        }

        var liveRules = LiveRules(existing.Properties);
        var missing = declared.Select(JsonObjects.GetId).Where(id => id is not null && !liveRules.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            return Unsupported(
                desired,
                existing,
                $"{desired.Describe()}: the policy {existing.GraphId} has no rule {string.Join(", ", missing)}; its rules are {string.Join(", ", liveRules.Keys.Order(StringComparer.Ordinal))}.",
                InvalidCode);
        }

        foreach (var rule in declared)
        {
            RoleManagementPolicyShape.FillFromLive(rule, liveRules[JsonObjects.GetId(rule)!]);
        }

        Set(properties, "rules", new JsonArray(declared.Select(r => (JsonNode?)RoleManagementPolicyShape.Comparable(r)).ToArray()));
        var aligned = new ExistingResource(existing.GraphId, AlignedLive(existing.Properties, declared), existing.Raw);
        var change = base.Plan(desired, aligned, options);
        return change is { Type: ChangeType.Modify, HasError: false }
            ? change with { Reasons = [.. change.Reasons, "the changed rules are sent in one PATCH of the policy (each rule complete); undeclared rules are left as they are"] }
            : change;
    }

    /// <summary>No live policy: an unknown scope or role is looked up at deployment; otherwise an error, as ARM has a policy for every role at every scope.</summary>
    private ResourceChange PlanMissing(ResourceInstance desired, JsonObject properties)
    {
        if (desired.IsSymbolic || UnknownJson.ContainsUnknown(Get(properties, "scope")) || UnknownJson.ContainsUnknown(Get(properties, "roleDefinitionId"))
            || Get(properties, "role") is not null || ScopeOf(properties) is not { } scope)
        {
            return Unknown(desired, null, "the policy is looked up when the deployment runs (its scope or role is only known then)");
        }

        return Unsupported(
            desired,
            null,
            $"{desired.Describe()}: Azure Resource Manager lists no PIM policy for role {scope.RoleDefinitionId} at {scope.Scope} (GET {AzureRoleManagementPolicyLookup.AssignmentsPath(scope.Scope)}?$filter={AzureRoleManagementPolicyLookup.Filter(scope)} lists no assignment of that exact scope); check that the scope exists and that roleDefinitionId names an Azure role definition. Role management policies are never created.",
            EngineDiagnosticCodes.PimPolicyNotAvailable);
    }


    private ResourceChange Unknown(ResourceInstance desired, ExistingResource? existing, string reason)
    {
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = ChangeType.Modify,
            Mode = ChangeMode.Patch,
            GraphId = existing?.GraphId,
            After = JsonDiff.RenderObject(desired.Properties),
            IsUnknown = true,
            IsSymbolicId = desired.IsSymbolic,
            Reasons = [reason],
        };
    }

    private ResourceChange Unsupported(ResourceInstance desired, ExistingResource? existing, string message, string code)
    {
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = ChangeType.Modify,
            Mode = ChangeMode.Patch,
            GraphId = existing?.GraphId,
            Before = existing is null ? null : RenderBefore(existing.Properties, desired.Properties),
            After = JsonDiff.RenderObject(desired.Properties),
            IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
            IsSymbolicId = desired.IsSymbolic,
            Reasons = [message],
            UnsupportedReason = message,
            UnsupportedCode = code,
        };
    }
}
