using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// Keeps live settings a declaration leaves out. Learn documents the policy PATCHes as "properties not in the body keep their values" without saying
/// whether a supplied nested object replaces or merges (tenantappmanagementpolicy-update, crosstenantaccesspolicyconfigurationdefault-update, the
/// partner update), and the Entra guidance for the tenant app management policy warns that a restriction left out of a patched collection is
/// disabled. So before the diff, every member of the live object that a declared object omits is copied in (recursively through nested objects; arrays
/// are taken as declared): the PATCH then carries the whole object, the live values included, and nothing is cleared by omission. A declared
/// <c>null</c> or a different value always wins.
/// </summary>
public static class TenantPolicyShape
{
    /// <summary>Fills the undeclared members of the declared top-level objects named in <paramref name="objects"/> from <paramref name="live"/>.</summary>
    public static void FillUndeclared(JsonObject properties, JsonObject live, IEnumerable<string> objects)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(objects);
        foreach (string name in objects)
        {
            if (JsonObjects.TryGetIgnoreCase(properties, name, out var declared)
                && declared is JsonObject declaredObject
                && JsonObjects.TryGetIgnoreCase(live, name, out var liveNode)
                && liveNode is JsonObject liveObject)
            {
                Fill(declaredObject, liveObject);
            }
        }
    }

    private static void Fill(JsonObject declared, JsonObject live)
    {
        foreach (var member in live.ToList())
        {
            if (member.Key.Contains("@odata.", StringComparison.Ordinal) && !member.Key.Equals("@odata.type", StringComparison.Ordinal))
            {
                continue;
            }

            if (JsonObjects.FindKey(declared, member.Key) is not { } key)
            {
                declared[member.Key] = JsonValues.Clone(member.Value);
            }
            else if (declared[key] is JsonObject inner && !TenantPolicyValues.IsOpaque(inner) && member.Value is JsonObject liveInner)
            {
                Fill(inner, liveInner);
            }
        }
    }
}
