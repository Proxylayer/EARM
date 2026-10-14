using System;

namespace EntraArm.Az;

/// <summary>Microsoft Graph API surface. Template <c>apiVersion</c> <c>2026-10-01-preview</c> maps to <see cref="V1"/>, <c>2026-10-01-preview.beta</c> to <see cref="Beta"/>.</summary>
public enum GraphApi
{
    V1,
    Beta,
}

public static class GraphApiExtensions
{
    public const string Host = "https://graph.microsoft.com";

    /// <summary>Absolute base URL without a trailing slash, e.g. <c>https://graph.microsoft.com/v1.0</c>.</summary>
    public static string BaseUrl(this GraphApi api)
    {
        return Host + "/" + api.Segment();
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
