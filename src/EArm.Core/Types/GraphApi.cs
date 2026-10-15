using System;

namespace EArm.Core.Types;

/// <summary>The Microsoft Graph endpoint an <c>apiVersion</c> maps to.</summary>
public enum GraphApi
{
    /// <summary><c>https://graph.microsoft.com/v1.0</c> (apiVersion <c>2026-10-01-preview</c>).</summary>
    V1,

    /// <summary><c>https://graph.microsoft.com/beta</c> (apiVersion <c>2026-10-01-preview.beta</c>).</summary>
    Beta,
}

public static class GraphApis
{
    /// <summary>Parses the overlay spelling (<c>v1.0</c> / <c>beta</c>, case-insensitive).</summary>
    public static bool TryParse(string? text, out GraphApi api)
    {
        api = GraphApi.V1;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "v1.0":
            case "v1":
                api = GraphApi.V1;
                return true;
            case "beta":
                api = GraphApi.Beta;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The URL path segment: <c>v1.0</c> or <c>beta</c>.</summary>
    public static string Segment(GraphApi api)
    {
        return api switch
        {
            GraphApi.V1 => "v1.0",
            GraphApi.Beta => "beta",
            _ => throw new ArgumentOutOfRangeException(nameof(api)),
        };
    }
}
