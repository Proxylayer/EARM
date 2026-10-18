using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The pure rules of <see cref="GroupLifecyclePoliciesProvider"/>: the shape checks of <c>groupLifetimeInDays</c> and <c>managedGroupTypes</c>
/// (<c>All</c>, <c>Selected</c>, <c>None</c>, matched case-insensitively and sent in Graph's spelling), and the comparison of
/// <c>alternateNotificationEmails</c> (Graph keeps one semicolon-separated string; EARM compares the addresses as a case-insensitive set, so
/// <c>"a@x.com; b@x.com"</c> equals <c>"b@x.com;A@x.com"</c>). Source: https://learn.microsoft.com/en-us/graph/api/resources/grouplifecyclepolicy?view=graph-rest-1.0.
/// </summary>
public static class GroupLifecyclePoliciesRules
{
    public const string LifetimeProperty = "groupLifetimeInDays";

    public const string ManagedGroupTypesProperty = "managedGroupTypes";

    public const string EmailsProperty = "alternateNotificationEmails";

    /// <summary>The <c>managedGroupTypes</c> value under which groups are added one by one (<c>addGroup</c> / <c>removeGroup</c>).</summary>
    public const string Selected = "Selected";

    public static readonly IReadOnlyList<string> ManagedGroupTypes = ["All", Selected, "None"];

    /// <summary>Properties Graph assigns; never sent in a body.</summary>
    public static readonly string[] ServerAssignedProperties = ["id"];

    /// <summary>Checks the declared shapes and normalises the values in place; the problems found (none when valid).</summary>
    public static List<string> NormalizeDesired(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var problems = new List<string>();
        TenantPolicyValues.CanonicalizeEnum(properties, ManagedGroupTypesProperty, ManagedGroupTypes, problems);
        if (TenantPolicyValues.TryGetDeclared(properties, LifetimeProperty, out _, out var lifetime) && !TenantPolicyValues.IsIntegerInRange(lifetime, 1, int.MaxValue))
        {
            problems.Add($"properties.{LifetimeProperty} must be a positive whole number of days (Microsoft Entra offers 180, 365 or a custom value of 30 days or more)");
        }

        if (TenantPolicyValues.TryGetDeclared(properties, EmailsProperty, out var emailsKey, out var emails) && emails is not null)
        {
            if (!JsonValues.TryGetString(emails, out var text))
            {
                problems.Add($"properties.{EmailsProperty} must be a string of semicolon-separated addresses");
            }
            else
            {
                properties[emailsKey] = string.Join(";", SplitEmails(text));
            }
        }

        return problems;
    }

    /// <summary>When the declared and live notification addresses are the same set, the declared value adopts the live string; the enum adopts the live spelling.</summary>
    public static void AdoptLive(JsonObject properties, JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(live);
        TenantPolicyValues.AdoptLiveSpelling(properties, live, ManagedGroupTypesProperty);
        if (TenantPolicyValues.TryGetDeclared(properties, EmailsProperty, out var key, out var declared)
            && JsonValues.TryGetString(declared, out var declaredText)
            && JsonObjects.GetString(live, EmailsProperty) is { } liveText
            && EmailSet(declaredText).SetEquals(EmailSet(liveText)))
        {
            properties[key] = liveText;
        }
    }

    /// <summary>The addresses of a semicolon-separated list, trimmed, empties dropped, in the declared order.</summary>
    public static IReadOnlyList<string> SplitEmails(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static HashSet<string> EmailSet(string text)
    {
        return new HashSet<string>(SplitEmails(text), StringComparer.OrdinalIgnoreCase);
    }
}
