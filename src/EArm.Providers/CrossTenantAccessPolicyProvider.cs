using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/policies/crossTenantAccessPolicy</c>, name <c>default</c>: the default cross-tenant access configuration
/// (crossTenantAccessPolicyConfigurationDefault, https://learn.microsoft.com/en-us/graph/api/resources/crosstenantaccesspolicyconfigurationdefault?view=graph-rest-1.0),
/// the settings every partner without its own configuration inherits. A singleton that always exists (it has no <c>id</c>; <c>default</c> stands in).
/// <para>
/// Requests: <b>Read</b> <c>GET /policies/crossTenantAccessPolicy/default</c>; <b>Create</b> never; <b>Update</b> <c>PATCH …/default</c> (204) with the
/// changed setting objects, each sent whole with its undeclared live members filled in (<see cref="CrossTenantAccessRules"/>) → GET; <b>Delete</b>
/// <c>POST …/default/resetToSystemDefault</c> (204), which restores Microsoft's service defaults (B2B collaboration allowed, B2B direct connect and
/// tenant restrictions blocked, no inbound trust); the delete-risk text says so. <c>isServiceDefault</c> and <c>automaticUserConsentSettings</c> are
/// read-only on the default configuration and cannot be declared.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class CrossTenantAccessPolicyProvider : SingletonPolicyProviderBase, IDeleteWarningSource
{
    public const string ResourceType = "Microsoft.Entra/policies/crossTenantAccessPolicy";

    private static readonly Dictionary<string, string> ReadOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["isServiceDefault"] = "Graph sets it (true until the default configuration is changed, and again after a reset)",
        ["automaticUserConsentSettings"] = "it is read-only on the default configuration (always false); set it on a partner configuration",
    };

    public CrossTenantAccessPolicyProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog, ResourceType)
    {
    }

    public CrossTenantAccessPolicyProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string Noun => "default cross-tenant access configuration";

    protected override IReadOnlyList<string> FillFromLive => CrossTenantAccessRules.SettingObjects;

    protected override bool CanReset => true;

    protected override string RefusalText => string.Empty;

    protected override List<string> Normalize(JsonObject properties)
    {
        return CrossTenantAccessRules.RefuseReadOnly(properties, ReadOnly);
    }

    /// <summary><c>POST /policies/crossTenantAccessPolicy/default/resetToSystemDefault</c> (Learn: 204, no body).</summary>
    protected override async Task ResetAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        await graph.PostAsync(new GraphRequest(CollectionPath + "/resetToSystemDefault", key.Api), null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The delete resets every inherited cross-tenant setting of the tenant.</summary>
    public string? DescribeDeleteRisk(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        return "resets the default cross-tenant access settings to the Microsoft service defaults for every partner without its own configuration (B2B collaboration allowed, B2B direct connect and tenant restrictions blocked, no inbound trust); the configuration itself is never removed";
    }
}
