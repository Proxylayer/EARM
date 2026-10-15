using System;
using AzGraphApi = EArm.Graph.GraphApi;
using CoreGraphApi = EArm.Core.Types.GraphApi;

namespace EArm.Engine.Providers;

/// <summary>
/// Converts between the catalog's notion of a Graph endpoint (<see cref="EArm.Core.Types.GraphApi"/>, read from the
/// overlay) and the transport's (<see cref="EArm.Graph.GraphApi"/>, used to build requests). The two enums exist
/// because EArm.Graph has no reference to EArm.Core.
/// </summary>
public static class GraphApiMapping
{
    public static AzGraphApi ToAz(CoreGraphApi api)
    {
        return api switch
        {
            CoreGraphApi.V1 => AzGraphApi.V1,
            CoreGraphApi.Beta => AzGraphApi.Beta,
            _ => throw new ArgumentOutOfRangeException(nameof(api), api, "Unknown Graph API."),
        };
    }

    public static CoreGraphApi ToCore(AzGraphApi api)
    {
        return api switch
        {
            AzGraphApi.V1 => CoreGraphApi.V1,
            AzGraphApi.Beta => CoreGraphApi.Beta,
            _ => throw new ArgumentOutOfRangeException(nameof(api), api, "Unknown Graph API."),
        };
    }
}
