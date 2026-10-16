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
/// <para>
/// Scopes: a run that signs in as a user with the stored sign-in (<see cref="GraphAuthFactory.UsesStoredSignIn"/>) and has the scopes of its verb
/// (<see cref="RunAuthentication.Scopes"/>) requests exactly those delegated scopes: ONE <see cref="TokenScopes"/> instance shared by the Graph client and the
/// preflight (whose <c>scp</c> check compares the token with it). Every other identity (service principal, workload or managed identity) keeps <c>.default</c>.
/// Key Vault always requests the vault's <c>.default</c>. Such a run also gets the consent prompter of <see cref="ConsentPrompting"/> (interactive terminals
/// only): the token provider prompts for a consent a silent request lacks, the preflight for scopes the token lacks.
/// </para>
/// </summary>
internal sealed class EngineServices : IDisposable
{
    private readonly HttpClient _http;
    private readonly IGraphTokenProvider _tokens;
    private readonly ReplicationWait _replicationWait;

    private EngineServices(ConnectionSettings connection, GraphCloud cloud, GraphAuthOptions auth, ILoggerFactory loggerFactory, RunAuthentication run, TextWriter stderr)
    {
        var overrides = connection.Overrides ?? ServiceOverrides.Ambient;
        LoggerFactory = loggerFactory;
        Cloud = cloud;
        Auth = auth;

        _http = overrides?.Handler is { } handler
            ? new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan }
            : HttpGraphClient.CreateHttpClient();
        var authFactory = overrides?.AuthFactory ?? new GraphAuthFactory(overrides?.RecordStore, loggerFactory.CreateLogger<GraphAuthFactory>());
        UsesDelegatedSignIn = authFactory.UsesStoredSignIn(auth);
        HasRunScopes = run.Scopes is not null;

        // Built ONCE: the Graph client attaches it, the preflight checks the token's scp against it. Null = the cloud's .default.
        TokenScopes? graphScopes = UsesDelegatedSignIn && run.Scopes is { } runScopes ? TokenScopes.Explicit(cloud.GraphOrigin, runScopes.GraphScopes) : null;
        var prompter = graphScopes is null ? null : ConsentPrompting.Create(authFactory, auth, stderr, run.DeviceCode);
        _tokens = overrides?.Tokens ?? authFactory.Create(auth, prompter);

        // The preflight prompts through the provider (same gate, credential renewed after consent); a test's fake provider gets the prompter itself.
        var preflightPrompter = _tokens is AzureIdentityTokenProvider provider ? provider.ConsentPrompter : prompter;

        var time = overrides?.Time ?? TimeProvider.System;
        _replicationWait = new ReplicationWait { Delay = overrides?.Delay ?? TaskDelayProvider.Instance };
        RetryPolicy = new RetryPolicy(new RetryOptions(), overrides?.Delay ?? TaskDelayProvider.Instance, loggerFactory.CreateLogger<RetryPolicy>());
        var options = new GraphOptions { Cloud = cloud, MaxParallel = Math.Max(1, connection.Parallelism), Scopes = graphScopes };
        Graph = new HttpGraphClient(_http, _tokens, RetryPolicy, options, loggerFactory.CreateLogger<HttpGraphClient>(), time);
        Preflight = new GraphPreflight(_tokens, Graph, cloud, auth.Mode, loggerFactory.CreateLogger<GraphPreflight>(), graphScopes, preflightPrompter);
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
        return TryCreate(connection, loggerFactory, TextWriter.Null, stderr, OutputFormat.Table, RunAuthentication.None, out services, out exitCode);
    }

    /// <summary>
    /// As above, for one run of an online verb: <paramref name="run"/> gives the delegated scopes a user sign-in requests (and how a consent prompt is shown); with
    /// <c>-o json</c> the reason is ONE JSON document on <paramref name="stdout"/> (<see cref="EArm.Cli.Commands.VerbOutput"/>), not prose on stderr.
    /// </summary>
    public static bool TryCreate(ConnectionSettings connection, ILoggerFactory loggerFactory, TextWriter stdout, TextWriter stderr, OutputFormat output, RunAuthentication run, out EngineServices? services, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        services = null;
        exitCode = ExitCodes.Ok;
        try
        {
            var (cloud, auth) = connection.Resolve();
            services = new EngineServices(connection, cloud, auth, loggerFactory, run, stderr);
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

    /// <summary>The run signs in as a user with the stored sign-in, so it requests delegated scopes (never <c>.default</c>).</summary>
    public bool UsesDelegatedSignIn { get; }

    /// <summary>The run was given the scopes of its verb.</summary>
    public bool HasRunScopes { get; }

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
    public TemplateDeleter CreateTemplateDeleter()
    {
        return new TemplateDeleter(Providers, Graph, LoggerFactory.CreateLogger<TemplateDeleter>());
    }

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
