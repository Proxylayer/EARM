using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Types;

namespace EArm.Providers;

/// <summary>
/// The STS policies (<see cref="TokenLifetimePoliciesProvider"/>, <see cref="ClaimsMappingPoliciesProvider"/>) on the display-name policy base
/// (<see cref="DisplayNamePolicyProviderBase"/> describes the requests): <c>definition</c> is normalised and compared by JSON content
/// (<see cref="StsPolicyRules"/>), and <c>isOrganizationDefault</c> is checked per policy.
/// </summary>
public abstract class StsPolicyProviderBase : DisplayNamePolicyProviderBase
{
    protected StsPolicyProviderBase(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, string resourceType)
        : base(definition, catalog, resourceType)
    {
    }

    /// <summary>The root element of the policy document (<c>TokenLifetimePolicy</c>).</summary>
    protected abstract string RootElement { get; }

    /// <summary>Whether <c>isOrganizationDefault: true</c> is meaningful for this policy.</summary>
    protected abstract bool AllowsOrganizationDefault { get; }

    /// <summary>The policy-specific checks of the root element's object.</summary>
    protected virtual IEnumerable<string> CheckRoot(JsonObject root)
    {
        return [];
    }

    protected override List<string> Normalize(JsonObject properties)
    {
        return StsPolicyRules.NormalizeDesired(properties, RootElement, AllowsOrganizationDefault, CheckRoot);
    }

    protected override void AdoptLive(JsonObject properties, JsonObject live)
    {
        StsPolicyRules.AdoptLiveDefinition(properties, live);
    }
}
