using System.Collections.Generic;
using EArm.Core.Types;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/policies/defaultAppManagementPolicy</c>: the tenant default app management policy (tenantAppManagementPolicy,
/// https://learn.microsoft.com/en-us/graph/api/resources/tenantappmanagementpolicy?view=graph-rest-1.0), "a single object that always exists and is
/// disabled by default". The resource name is <c>default</c>.
/// <para>
/// Requests: <b>Read</b> <c>GET /policies/defaultAppManagementPolicy</c>; <b>Create</b> never (a missing object is an error); <b>Update</b>
/// <c>PATCH /policies/defaultAppManagementPolicy</c> (204) with the changed top-level properties → GET; <b>Delete</b>: Graph documents neither a delete nor
/// a reset (the methods are Get and Update only), so the object is kept (<see cref="SingletonPolicyProviderBase.NotDeletable"/>, not a failure), like
/// an authentication method without a reset; switch it off with <c>isEnabled: false</c>.
/// </para>
/// <para>
/// <c>applicationRestrictions</c> and <c>servicePrincipalRestrictions</c> hold <c>passwordCredentials</c> / <c>keyCredentials</c> arrays compared as sets keyed by
/// <c>restrictionType</c>. A declared restrictions object keeps the live members it leaves out (Graph's guidance: a restriction missing from a patched
/// collection is disabled, so include the ones already present); a declared array is sent whole, so declare every restriction of an array you manage.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class DefaultAppManagementPolicyProvider : SingletonPolicyProviderBase
{
    public const string ResourceType = "Microsoft.Entra/policies/defaultAppManagementPolicy";

    public DefaultAppManagementPolicyProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog, ResourceType)
    {
    }

    public DefaultAppManagementPolicyProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string Noun => "default app management policy";

    protected override IReadOnlyList<string> FillFromLive => ["applicationRestrictions", "servicePrincipalRestrictions"];

    protected override string RefusalText => "the tenant default app management policy is a singleton that Graph can neither delete nor reset, so it is kept; set isEnabled: false to switch its restrictions off";
}
