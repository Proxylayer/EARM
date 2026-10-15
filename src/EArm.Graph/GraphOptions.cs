using System;

namespace EArm.Graph;

/// <summary>Settings of <see cref="HttpGraphClient"/>.</summary>
public sealed class GraphOptions
{
    /// <summary>The cloud (Graph host) the client talks to. Default <see cref="GraphCloud.Public"/>.</summary>
    public GraphCloud Cloud { get; set; } = GraphCloud.Public;

    /// <summary>Per-call timeout (one HTTP attempt, response body included). Default 120 s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Maximum number of concurrent HTTP calls. Default 4.</summary>
    public int MaxParallel { get; set; } = 4;

    /// <summary>The largest response body the client reads, in bytes, counted AFTER decompression (a gzip bomb hits it too). Default 16 MiB.</summary>
    public long MaxResponseBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>The largest request body the client sends, in bytes (Graph itself refuses more than about 4 MB). Default 4 MiB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>The most pages <see cref="IGraphClient.GetAllPagesAsync"/> follows for one collection. Default 2,000.</summary>
    public int MaxPages { get; set; } = 2_000;

    /// <summary>The most items <see cref="IGraphClient.GetAllPagesAsync"/> yields for one collection. Default 500,000.</summary>
    public int MaxItems { get; set; } = 500_000;

    /// <summary>The version in the <c>User-Agent</c> header (<c>EARM/&lt;version&gt;</c>).</summary>
    public string UserAgentVersion { get; set; } = GraphVersion.Current;
}

/// <summary>The product version stamped into the User-Agent.</summary>
internal static class GraphVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var version = typeof(GraphVersion).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
