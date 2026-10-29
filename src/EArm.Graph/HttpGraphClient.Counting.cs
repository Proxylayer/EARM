namespace EArm.Graph;

public sealed partial class HttpGraphClient
{
    /// <summary>The HTTP attempts this client has made, the most in flight at once and their scheduler wait (<see cref="GraphOptions.RequestCounter"/>).</summary>
    public GraphRequestCounter Requests => _options.RequestCounter;
}
