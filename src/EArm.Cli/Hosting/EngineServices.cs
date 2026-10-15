using System;
using System.IO;
using System.Net.Http;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Core.Types;
using EArm.Engine.Edges;
using EArm.Engine.Execution;
using EArm.Engine.KeyVault;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Graph.Auth;
using EArm.Providers;
using Microsoft.Extensions.Logging;

namespace EArm.Cli.Hosting;

/// <summary>
/// The composition root for the online verbs: <see cref="GraphAuthOptions"/> → <see cref="GraphAuthFactory"/> → <see cref="IGraphTokenProvider"/>
/// → <see cref="HttpGraphClient"/> (over one <see cref="HttpClient"/>, with <see cref="RetryPolicy"/>) → <see cref="GraphPreflight"/> and
/// <see cref="KeyVaultClient"/> → <see cref="ProviderRegistry"/> → planner / executor. Built once per command invocation and disposed with it.
/// The registry is filled by <see cref="ProviderDiscovery"/>: every <c>[EntraProvider]</c> class in EArm.Providers registers itself, so a new provider
/// never edits this file (see docs/providers.md). Tests inject an <see cref="HttpMessageHandler"/> and a fake token provider through
/// <see cref="ServiceOverrides"/>; nothing is read from the environment for that.
/// </summary>
internal sealed class EngineServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly IGraphTokenProvider _tokens;
    private readonly ReplicationWait _replicationWait;

    private EngineServices(ConnectionSettings connection, GraphCloud cloud, GraphAuthOptions auth, ILoggerFactory loggerFactory)
    {
        var overrides = connection.Overrides ?? ServiceOverrides.Ambient;
        LoggerFactory = loggerFactory;
        Cloud = cloud;
        Auth = auth;

        _http = overrides?.Handler is { } handler
            ? new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan }
            : HttpGraphClient.CreateHttpClient();
        var authFactory = overrides?.AuthFactory ?? new GraphAuthFactory(overrides?.RecordStore, loggerFactory.CreateLogger<GraphAuthFactory>());
        _tokens = overrides?.Tokens ?? authFactory.Create(auth);

        var time = overrides?.Time ?? TimeProvider.System;
        _replicationWait = new ReplicationWait { Delay = overrides?.Delay ?? TaskDelayProvider.Instance };
        RetryPolicy = new RetryPolicy(new RetryOptions(), overrides?.Delay ?? TaskDelayProvider.Instance, loggerFactory.CreateLogger<RetryPolicy>());
        var options = new GraphOptions { Cloud = cloud, MaxParallel = Math.Max(1, connection.Parallelism) };
        Graph = new HttpGraphClient(_http, _tokens, RetryPolicy, options, loggerFactory.CreateLogger<HttpGraphClient>(), time);
        Preflight = new GraphPreflight(_tokens, Graph, cloud, auth.Mode, loggerFactory.CreateLogger<GraphPreflight>());
        KeyVault = new KeyVaultClient(_http, _tokens, RetryPolicy, cloud, options.Timeout, loggerFactory.CreateLogger<KeyVaultClient>(), time);
        Providers = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);
        EdgeKinds = ProviderDiscovery.CreateEdgeKinds(ResourceTypeCatalog.Default);
    }

    /// <summary>
    /// Builds the services, or prints why that is impossible and returns false with the exit code to use: a bad option or <c>EARM_*</c> value is a plain
    /// failure (1); a credential setting that would start an external process is an auth failure (3).
    /// </summary>
    public static bool TryCreate(ConnectionSettings connection, ILoggerFactory loggerFactory, TextWriter stderr, out EngineServices? services, out int exitCode)
    {
        return TryCreate(connection, loggerFactory, TextWriter.Null, stderr, OutputFormat.Table, out services, out exitCode);
    }

    /// <summary>As above; with <c>-o json</c> the reason is ONE JSON document on <paramref name="stdout"/> (<see cref="EArm.Cli.Commands.VerbOutput"/>), not prose on stderr.</summary>
    public static bool TryCreate(ConnectionSettings connection, ILoggerFactory loggerFactory, TextWriter stdout, TextWriter stderr, OutputFormat output, out EngineServices? services, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        services = null;
        exitCode = ExitCodes.Ok;
        try
        {
            var (cloud, auth) = connection.Resolve();
            services = new EngineServices(connection, cloud, auth, loggerFactory);
            return true;
        }
        catch (ArgumentException ex)
        {
            exitCode = VerbOutput.Fail(output, stdout, stderr, ExitCodes.Failure, "InvalidArgument", ex.Message);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            exitCode = VerbOutput.Fail(output, stdout, stderr, ExitCodes.AuthOrPreflight, "PreflightFailed", "Preflight failed: " + ex.Message);
            return false;
        }
    }

    public ILoggerFactory LoggerFactory { get; }

    public GraphCloud Cloud { get; }

    public GraphAuthOptions Auth { get; }

    public RetryPolicy RetryPolicy { get; }

    public HttpGraphClient Graph { get; }

    public GraphPreflight Preflight { get; }

    public KeyVaultClient KeyVault { get; }

    /// <summary>Resource providers by type, discovered from EArm.Providers; a template type without one fails with <see cref="UnknownProviderException"/>.</summary>
    public ProviderRegistry Providers { get; }

    /// <summary>Edge-kind handlers of the edge pass: the engine's part A kinds plus every <c>[EntraEdgeKind]</c> handler discovered in EArm.Providers (part B).</summary>
    public EdgeKindRegistry EdgeKinds { get; }

    /// <summary>The planner; no outer retry policy because <see cref="HttpGraphClient"/> retries every call itself. The discovered edge kinds are supplied unless the options name their own.</summary>
    public DeploymentPlanner CreatePlanner(PlannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DeploymentPlanner(Providers, Graph, options.EdgeKinds is null ? options with { EdgeKinds = EdgeKinds } : options, LoggerFactory.CreateLogger<DeploymentPlanner>());
    }

    public DeploymentExecutor CreateExecutor()
    {
        return new DeploymentExecutor(Providers, Graph, LoggerFactory.CreateLogger<DeploymentExecutor>(), _replicationWait);
    }

    /// <summary>Resolves parameter-file Key Vault references with a Key Vault token of the signed-in identity.</summary>
    public KeyVaultSecretResolver CreateKeyVaultResolver()
    {
        return new KeyVaultSecretResolver(KeyVault, LoggerFactory.CreateLogger<KeyVaultSecretResolver>());
    }

    public void Dispose()
    {
        _http.Dispose();
        (_tokens as IDisposable)?.Dispose();
    }
}
