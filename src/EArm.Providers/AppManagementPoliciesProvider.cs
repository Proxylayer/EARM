using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Types;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/policies/appManagementPolicies</c>: custom app management policies (https://learn.microsoft.com/en-us/graph/api/resources/appmanagementpolicy?view=graph-rest-1.0),
/// keyed by <c>displayName</c> on the display-name policy base (<see cref="DisplayNamePolicyProviderBase"/> describes the requests). <c>restrictions</c> holds
/// <c>passwordCredentials</c> and <c>keyCredentials</c> restriction arrays (compared as sets keyed by <c>restrictionType</c>) and, for a custom policy,
/// <c>applicationRestrictions</c>; restrictions a custom policy leaves out fall back to the tenant default policy
/// (<see cref="DefaultAppManagementPolicyProvider"/>). Undeclared members of a declared <c>restrictions</c> object keep their live values
/// (<see cref="TenantPolicyShape"/>). Assigned with the <c>appManagementPolicies</c> edge of applications and service principals (one per object).
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AppManagementPoliciesProvider : DisplayNamePolicyProviderBase
{
    public const string ResourceType = "Microsoft.Entra/policies/appManagementPolicies";

    public AppManagementPoliciesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog, ResourceType)
    {
    }

    public AppManagementPoliciesProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string Noun => "app management policy";

    protected override string AssignmentProperty => "appManagementPolicies";

    protected override List<string> Normalize(JsonObject properties)
    {
        return [];
    }

    protected override void AdoptLive(JsonObject properties, JsonObject live)
    {
        TenantPolicyShape.FillUndeclared(properties, live, ["restrictions"]);
    }
}
