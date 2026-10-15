using System;
using EArm.Az;
using EArm.Core.Types;
using EArm.Engine.Edges;
using EArm.Engine.Execution;
using EArm.Engine.KeyVault;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers;
using Microsoft.Extensions.Logging;

namespace EArm.Cli.Hosting;

/// <summary>
/// The composition root for the online verbs (TODO-0015): <see cref="AzOptions"/> → <see cref="PwshLocator"/> →
/// <see cref="PwshAzRunner"/> → <see cref="RetryPolicy"/> → <see cref="AzGraphClient"/> → <see cref="AzPreflight"/> →
/// <see cref="ProviderRegistry"/> → planner / executor. Built once per command invocation and disposed with it.
/// The registry is filled by <see cref="ProviderDiscovery"/>: every <c>[EntraProvider]</c> class in EArm.Providers
/// registers itself, so a new provider never edits this file (see docs/providers.md).
/// </summary>
internal sealed class EngineServices : IDisposable
{
    public EngineServices(AzOptions azOptions, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(azOptions);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        AzOptions = azOptions;
        LoggerFactory = loggerFactory;
        Locator = new PwshLocator(azOptions, loggerFactory.CreateLogger<PwshLocator>());
        Runner = new PwshAzRunner(azOptions, Locator, loggerFactory.CreateLogger<PwshAzRunner>());
        RetryPolicy = new RetryPolicy(loggerFactory.CreateLogger<RetryPolicy>());
        Graph = new AzGraphClient(Runner, RetryPolicy, loggerFactory.CreateLogger<AzGraphClient>());
        Preflight = new AzPreflight(Runner, Graph, loggerFactory.CreateLogger<AzPreflight>());
        Providers = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);
        EdgeKinds = ProviderDiscovery.CreateEdgeKinds(ResourceTypeCatalog.Default);
    }

    public AzOptions AzOptions { get; }

    public ILoggerFactory LoggerFactory { get; }

    public PwshLocator Locator { get; }

    public PwshAzRunner Runner { get; }

    public RetryPolicy RetryPolicy { get; }

    public AzGraphClient Graph { get; }

    public AzPreflight Preflight { get; }

    /// <summary>Resource providers by type, discovered from EArm.Providers; a template type without one fails with <see cref="UnknownProviderException"/>.</summary>
    public ProviderRegistry Providers { get; }

    /// <summary>Edge-kind handlers of the edge pass: the engine's part A kinds plus every <c>[EntraEdgeKind]</c> handler discovered in EArm.Providers (part B).</summary>
    public EdgeKindRegistry EdgeKinds { get; }

    /// <summary>The planner; no outer retry policy because <see cref="AzGraphClient"/> retries every call itself. The discovered edge kinds are supplied unless the options name their own.</summary>
    public DeploymentPlanner CreatePlanner(PlannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DeploymentPlanner(Providers, Graph, options.EdgeKinds is null ? options with { EdgeKinds = EdgeKinds } : options, LoggerFactory.CreateLogger<DeploymentPlanner>());
    }

    public DeploymentExecutor CreateExecutor()
    {
        return new DeploymentExecutor(Providers, Graph, LoggerFactory.CreateLogger<DeploymentExecutor>());
    }

    /// <summary>Resolves parameter-file Key Vault references through the Azure CLI session.</summary>
    public KeyVaultSecretResolver CreateKeyVaultResolver()
    {
        return new KeyVaultSecretResolver(Runner, LoggerFactory.CreateLogger<KeyVaultSecretResolver>());
    }

    public void Dispose()
    {
        Runner.Dispose();
    }
}
