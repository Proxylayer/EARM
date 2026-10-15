using System;

namespace EArm.Graph;

/// <summary>Microsoft Graph API surface. Template <c>apiVersion</c> <c>2026-10-01-preview</c> maps to <see cref="V1"/>, <c>2026-10-01-preview.beta</c> to <see cref="Beta"/>.</summary>
public enum GraphApi
{
    V1,
    Beta,
}

public static class GraphApiExtensions
{
    /// <summary>The public-cloud Graph origin (<see cref="GraphCloud.Public"/>). Sovereign clouds use <see cref="GraphCloud.GraphOrigin"/>.</summary>
    public const string Host = "https://graph.microsoft.com";

    /// <summary>Absolute public-cloud base URL without a trailing slash, e.g. <c>https://graph.microsoft.com/v1.0</c>.</summary>
    public static string BaseUrl(this GraphApi api)
    {
        return Host + "/" + api.Segment();
    }

    /// <summary>Absolute base URL of <paramref name="cloud"/> without a trailing slash.</summary>
    public static string BaseUrl(this GraphApi api, GraphCloud cloud)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        return cloud.GraphOrigin + "/" + api.Segment();
    }

    /// <summary><c>v1.0</c> or <c>beta</c>.</summary>
    public static string Segment(this GraphApi api)
    {
        return api switch
        {
            GraphApi.V1 => "v1.0",
            GraphApi.Beta => "beta",
            _ => throw new ArgumentOutOfRangeException(nameof(api), api, "Unknown Graph API."),
        };
    }
}
