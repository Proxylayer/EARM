using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/policies/tokenLifetimePolicies</c>: access token lifetime policies (https://learn.microsoft.com/en-us/graph/api/resources/tokenlifetimepolicy?view=graph-rest-1.0).
/// The document is <c>{"TokenLifetimePolicy":{"Version":1,"AccessTokenLifetime":"hh:mm:ss"}}</c>: the lifetime is 10 minutes to 1 day (default 1 hour),
/// and the refresh and session token lifetimes are no longer supported, so declaring one is an error. <c>isOrganizationDefault: true</c> makes the
/// policy the tenant default (only one can be). Assigned with the <c>tokenLifetimePolicies</c> edge of applications and service principals.
/// See <see cref="StsPolicyProviderBase"/> for the requests.
/// </summary>
[EntraProvider(ResourceType)]
public sealed class TokenLifetimePoliciesProvider : StsPolicyProviderBase
{
    public const string ResourceType = "Microsoft.Entra/policies/tokenLifetimePolicies";

    /// <summary>The refresh and session token settings Microsoft retired (Learn: "Refresh and session token lifetimes ... aren't supported").</summary>
    private static readonly string[] RetiredSettings = ["MaxInactiveTime", "MaxAgeSingleFactor", "MaxAgeMultiFactor", "MaxAgeSessionSingleFactor", "MaxAgeSessionMultiFactor"];

    private static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(1);

    public TokenLifetimePoliciesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog, ResourceType)
    {
    }

    public TokenLifetimePoliciesProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog, ResourceType), catalog)
    {
    }

    protected override string Noun => "token lifetime policy";

    protected override string RootElement => "TokenLifetimePolicy";

    protected override bool AllowsOrganizationDefault => true;

    protected override string AssignmentProperty => "tokenLifetimePolicies";

    protected override IEnumerable<string> CheckRoot(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (string retired in RetiredSettings.Where(s => JsonObjects.TryGetIgnoreCase(root, s, out _)))
        {
            yield return $"sets {retired}, a refresh / session token lifetime Microsoft no longer supports; only AccessTokenLifetime can be configured";
        }

        if (JsonObjects.TryGetIgnoreCase(root, "AccessTokenLifetime", out var lifetime))
        {
            if (!JsonValues.TryGetString(lifetime, out var text) || !TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value))
            {
                yield return "AccessTokenLifetime must be a time span such as \"02:00:00\" (hh:mm:ss) or \"1.00:00:00\"";
            }
            else if (value < MinimumLifetime || value > MaximumLifetime)
            {
                yield return $"AccessTokenLifetime '{text}' is outside the allowed range of 10 minutes to 1 day";
            }
        }
    }
}
