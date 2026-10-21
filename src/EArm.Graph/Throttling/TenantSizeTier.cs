using System;

namespace EArm.Graph.Throttling;

/// <summary>
/// Microsoft's tenant size tiers of the directory resource-unit budget ("Identity and access service limits",
/// https://learn.microsoft.com/en-us/graph/throttling-limits), per app and tenant.
/// </summary>
internal enum TenantSizeTier
{
    /// <summary>Under 50 users: 3,500 resource units per 10 seconds.</summary>
    Small,

    /// <summary>50 to 500 users: 5,000 resource units per 10 seconds.</summary>
    Medium,

    /// <summary>More than 500 users: 8,000 resource units per 10 seconds.</summary>
    Large,
}

/// <summary>The published numbers of the directory service limits.</summary>
internal static class DirectoryLimits
{
    /// <summary>The refill period of the resource-unit bucket.</summary>
    public static readonly TimeSpan ResourceUnitPeriod = TimeSpan.FromSeconds(10);

    /// <summary>Write requests (POST, PATCH, PUT, DELETE) per app and tenant per <see cref="WritePeriod"/>.</summary>
    public const int Writes = 3_000;

    public static readonly TimeSpan WritePeriod = TimeSpan.FromSeconds(150);

    /// <summary>Conditional Access policies and named locations: one request per this interval, per tenant (all apps together).</summary>
    public static readonly TimeSpan ConditionalAccessInterval = TimeSpan.FromSeconds(1);

    /// <summary>The resource units per <see cref="ResourceUnitPeriod"/> of a tier.</summary>
    public static int ResourceUnits(TenantSizeTier tier)
    {
        return tier switch
        {
            TenantSizeTier.Large => 8_000,
            TenantSizeTier.Medium => 5_000,
            _ => 3_500,
        };
    }

    /// <summary>The tier of a tenant with <paramref name="users"/> users: under 50 small, 50 to 500 medium, more large.</summary>
    public static TenantSizeTier TierOf(long users)
    {
        return users < 50 ? TenantSizeTier.Small : users <= 500 ? TenantSizeTier.Medium : TenantSizeTier.Large;
    }
}
