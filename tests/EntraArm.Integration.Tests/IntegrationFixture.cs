using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core.Types;
using EntraArm.Engine.Edges;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using EntraArm.Providers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EntraArm.Integration.Tests;

/// <summary>
/// The shared environment of the real-tenant tests (one xUnit collection, <see cref="RealTenantCollection"/>).
/// <para>
/// Construction never touches the network: when <c>ENTRAARM_IT_TENANT</c> is unset the fixture only records why the
/// tests skip. When it is set, <see cref="InitializeAsync"/> builds the stack the CLI uses
/// (<see cref="PwshLocator"/> → <see cref="PwshAzRunner"/> → <see cref="AzGraphClient"/> + <see cref="RetryPolicy"/>,
/// <see cref="ProviderDiscovery"/>, a <see cref="FileStateStore"/> in a temp directory; compare
/// <c>src/EntraArm.Cli/Hosting/EngineServices.cs</c>) and runs the real <see cref="AzPreflight"/>; a missing session or
/// a tenant other than <c>ENTRAARM_IT_TENANT</c> turns every test into a skip with that reason.
/// </para>
/// <para>
/// Cleanup guard: <see cref="DisposeAsync"/> (1) deletes the objects of every deployment state in the run's state
/// directory with <see cref="DeploymentDeleter.DeleteAllAsync"/>, (2) sweeps leftovers whose name starts with the run
/// prefix (<see cref="TenantCleanup"/>) and purges their soft-deleted copies, (3) writes a log next to the state
/// directory and fails loudly when a live object with the prefix could not be removed. Objects without the prefix are
/// never touched.
/// </para>
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> _trackedPaths = new();
    private readonly ConcurrentDictionary<string, byte> _reservedContextIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly CapturingLoggerProvider _logCapture = new();
    private ILoggerFactory? _loggerFactory;
    private PwshAzRunner? _runner;
    private AzGraphClient? _graph;
    private FileStateStore? _store;
    private string _unavailableReason = string.Empty;

    public IntegrationFixture()
    {
        Settings = IntegrationSettings.FromEnvironment();
        string runDirectory = Path.Combine(Path.GetTempPath(), "entraarm-it", Settings.Prefix);
        StateDirectory = Path.Combine(runDirectory, "state");
        ScratchDirectory = Path.Combine(runDirectory, "scratch");
        _unavailableReason = Settings.IsConfigured
            ? string.Empty
            : $"{IntegrationSettings.TenantVariable} is not set; the real-tenant tests are skipped. See tests/EntraArm.Integration.Tests/README.md.";
    }

    internal IntegrationSettings Settings { get; }

    /// <summary>Name prefix of every object this run creates.</summary>
    public string Prefix => Settings.Prefix;

    /// <summary>The run's private state directory (never the template directory, never <c>ENTRAARM_STATE_DIR</c>).</summary>
    public string StateDirectory { get; }

    /// <summary>Where the tests write secure-output files and template variants; kept apart from <see cref="StateDirectory"/> because every <c>*.json</c> there is taken for a deployment state.</summary>
    public string ScratchDirectory { get; }

    /// <summary>True when the tenant is configured and the Azure CLI session is usable for it.</summary>
    public bool IsAvailable => _unavailableReason.Length == 0;

    /// <summary>Why the tests skip, empty when <see cref="IsAvailable"/>.</summary>
    public string UnavailableReason => _unavailableReason;

    /// <summary>What the preflight learned (tenant, signed-in user); null until a session was verified.</summary>
    public AzPreflightResult? Session { get; private set; }

    /// <summary>The production Graph client (az rest through pwsh, with the retry policy).</summary>
    internal AzGraphClient Graph => _graph ?? throw NotAvailable();

    internal ProviderRegistry Providers { get; private set; } = ProviderRegistry.Empty;

    internal EdgeKindRegistry EdgeKinds { get; private set; } = EdgeKindRegistry.Default;

    internal FileStateStore Store => _store ?? throw NotAvailable();

    /// <summary>Live objects the last sweep could not remove (empty when the cleanup was complete).</summary>
    internal IReadOnlyList<string> Leftovers => _leftovers;

    internal CliDriver Cli { get; private set; } = new CliDriver(Path.GetTempPath(), null);

    /// <summary>What the cleanup guard did (filled by <see cref="DisposeAsync"/> or <see cref="SweepNowAsync"/>).</summary>
    public IReadOnlyList<string> CleanupLog { get; private set; } = [];

    // ---- skip guards ----

    /// <summary>Skips the calling test unless the tenant is usable and every requirement is met.</summary>
    internal void Require(Needs needs = Needs.Tenant)
    {
        Assert.SkipUnless(IsAvailable, _unavailableReason);
        Assert.SkipUnless(!needs.HasFlag(Needs.Domain) || Settings.Domain is not null, $"{IntegrationSettings.DomainVariable} is not set (a verified domain for user principal names is required).");
        Assert.SkipUnless(!needs.HasFlag(Needs.ConditionalAccess) || Settings.AllowConditionalAccess, $"{IntegrationSettings.AllowCaVariable}=1 is required to write Conditional Access objects.");
        Assert.SkipUnless(!needs.HasFlag(Needs.Roles) || Settings.AllowRoles, $"{IntegrationSettings.AllowRolesVariable}=1 is required to write directory roles, administrative units, app role assignments and consent grants.");
    }

    /// <summary>The verified domain; only valid after <see cref="Require"/> with <see cref="Needs.Domain"/>.</summary>
    public string Domain => Settings.Domain ?? throw new InvalidOperationException("Call Require(Needs.Domain) first.");

    // ---- naming, templates, tracking ----

    /// <summary>A name that starts with the run prefix: <c>&lt;prefix&gt;-&lt;suffix&gt;</c>.</summary>
    public string Name(string suffix)
    {
        return Prefix + "-" + suffix;
    }

    /// <summary>A user principal name under the run prefix and the verified domain.</summary>
    public string Upn(string localSuffix)
    {
        return Name(localSuffix) + "@" + Domain;
    }

    /// <summary>The path of a template file copied next to the test binaries (<c>Templates/&lt;file&gt;</c>).</summary>
    public static string TemplatePath(string file)
    {
        return Path.Combine(AppContext.BaseDirectory, "Templates", file);
    }

    /// <summary>A deployment whose state name carries the prefix, with the <c>prefix</c> template parameter set.</summary>
    internal DeploymentRun NewRun(string shortName, string templateFile)
    {
        return new DeploymentRun(Name(shortName), TemplatePath(templateFile), ["-p", "prefix=" + Prefix]);
    }

    /// <summary>Registers an entity path (<c>/applications/{id}</c>) created outside a deployment; the guard deletes it (and purges it) at teardown when its name still starts with the prefix.</summary>
    public void Track(string entityPath)
    {
        _trackedPaths.Enqueue(entityPath);
    }

    /// <summary>
    /// Reserves a free authentication context id (<c>c99</c> downwards). A context template writes with an upsert, so the
    /// id must be one that does not exist: a foreign context is never overwritten.
    /// </summary>
    internal async Task<string> AllocateContextIdAsync(CancellationToken cancellationToken)
    {
        for (int number = 99; number >= 50; number--)
        {
            string id = "c" + number;
            if (_reservedContextIds.ContainsKey(id))
            {
                continue;
            }

            var existing = await Graph.GetAsync(new GraphRequest("/identity/conditionalAccess/authenticationContextClassReferences/" + id), cancellationToken);
            if (existing is null && _reservedContextIds.TryAdd(id, 0))
            {
                return id;
            }
        }

        throw new InvalidOperationException("No free authentication context id between c50 and c99.");
    }

    // ---- lifecycle ----

    public async ValueTask InitializeAsync()
    {
        if (!Settings.IsConfigured)
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(ScratchDirectory);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
            builder.AddProvider(_logCapture);
        });

        var azOptions = new AzOptions { AzCommand = Settings.AzCommand };
        var locator = new PwshLocator(azOptions, _loggerFactory.CreateLogger<PwshLocator>());
        _runner = new PwshAzRunner(azOptions, locator, _loggerFactory.CreateLogger<PwshAzRunner>());
        var retryPolicy = new RetryPolicy(_loggerFactory.CreateLogger<RetryPolicy>());
        _graph = new AzGraphClient(_runner, retryPolicy, _loggerFactory.CreateLogger<AzGraphClient>());
        var preflight = new AzPreflight(_runner, _graph, _loggerFactory.CreateLogger<AzPreflight>());
        Providers = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);
        EdgeKinds = ProviderDiscovery.CreateEdgeKinds(ResourceTypeCatalog.Default);
        _store = new FileStateStore(StateDirectory, logger: _loggerFactory.CreateLogger<FileStateStore>());
        Cli = new CliDriver(StateDirectory, Settings.AzCommand);

        try
        {
            Session = await preflight.RunAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is AzCliException or ToolchainException or GraphException)
        {
            _unavailableReason = $"The Azure CLI preflight failed, so the real-tenant tests are skipped: {ex.Message}";
            return;
        }

        if (!string.Equals(Session.TenantId, Settings.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            _unavailableReason = $"The Azure CLI is logged in to tenant {Session.TenantId}, but {IntegrationSettings.TenantVariable} is {Settings.TenantId}; run 'az login --allow-no-subscriptions --tenant {Settings.TenantId}'. The real-tenant tests are skipped.";
            Session = null;
        }
    }

    /// <summary>Teardown: deployments first, then the prefix sweep; throws when a live object with the prefix remains.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Session is not null && _graph is not null)
            {
                await SweepNowAsync(CancellationToken.None);
                if (_leftovers.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Cleanup left {_leftovers.Count} object(s) behind in tenant {Settings.TenantId} (prefix '{Prefix}'); remove them by hand (see README.md, 'Manual cleanup'):{Environment.NewLine}  - {string.Join(Environment.NewLine + "  - ", _leftovers)}");
                }
            }
        }
        finally
        {
            _runner?.Dispose();
            _loggerFactory?.Dispose();
            _logCapture.Dispose();
            TryDeleteStateDirectory();
        }
    }

    private List<string> _leftovers = [];

    /// <summary>The cleanup guard: deletes every state-managed object, sweeps the prefix and purges soft-deleted copies. Safe to call repeatedly.</summary>
    internal async Task SweepNowAsync(CancellationToken cancellationToken)
    {
        if (Session is null || _graph is null || _store is null)
        {
            return;
        }

        var log = new List<string>();
        _leftovers = [];

        // 1. Everything a deployment recorded, in reverse dependency order, with the production deleter.
        IReadOnlyList<string> names = [];
        try
        {
            names = await _store.ListAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Add($"the state directory could not be listed: {ex.Message}; the prefix sweep still runs");
        }

        foreach (string name in names)
        {
            try
            {
                var state = await _store.LoadAsync(name, cancellationToken);
                if (state is null)
                {
                    continue;
                }

                var logger = _loggerFactory!.CreateLogger("EntraArm.IntegrationTests.Cleanup");
                var result = await DeploymentDeleter.DeleteAllAsync(state, _store, Providers, _graph, null, true, logger, cancellationToken);
                log.Add($"deployment '{name}': {result.DeletedCount} deleted, {result.AlreadyGoneCount} already gone, {result.FailedCount} failed, {result.SkippedCount} skipped");
                foreach (var outcome in result.Outcomes.Where(o => !o.Succeeded))
                {
                    log.Add($"  {outcome.Status} {outcome.Entry.Id.Id}: {outcome.Error}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Add($"deployment '{name}': the delete pass failed: {ex.GetType().Name}: {ex.Message}; the prefix sweep still runs");
            }
        }

        // 2. The prefix sweep (also removes what a failed delete left behind, and everything tests created directly).
        var cleanup = new TenantCleanup(_graph, Prefix);
        try
        {
            await cleanup.SweepAsync(_trackedPaths.ToArray(), cancellationToken);
        }
        catch (Exception ex) when (ex is GraphException or AzCliException or ToolchainException)
        {
            log.Add($"prefix sweep aborted: {ex.GetType().Name}: {ex.Message}");
            _leftovers.Add("the prefix sweep was aborted before it finished: " + ex.Message);
        }

        log.AddRange(cleanup.Entries);
        _leftovers.AddRange(cleanup.Leftovers);
        log.AddRange(_logCapture.Lines.Select(line => "stack: " + line));
        CleanupLog = log;
        WriteCleanupLog(log);
    }

    private void WriteCleanupLog(IReadOnlyList<string> log)
    {
        string text = string.Join(Environment.NewLine, log.Prepend($"EntraARM integration cleanup, prefix '{Prefix}', tenant {Settings.TenantId}, {DateTimeOffset.UtcNow:O}"));
        try
        {
            string directory = Path.GetDirectoryName(StateDirectory)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "cleanup.log"), text, new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // The log is a convenience; the diagnostic message below still carries it.
        }

        TestContext.Current.SendDiagnosticMessage(text);
    }

    private void TryDeleteStateDirectory()
    {
        // Keep the directory (and cleanup.log) when something is left behind: its states are the record of what to remove.
        if (_leftovers.Count > 0)
        {
            return;
        }

        foreach (string directory in new[] { StateDirectory, ScratchDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private InvalidOperationException NotAvailable()
    {
        return new InvalidOperationException("The real-tenant stack is not available: " + (_unavailableReason.Length == 0 ? "the fixture was not initialised." : _unavailableReason));
    }
}

/// <summary>
/// One collection for every real-tenant test class: the classes share the fixture (one az session, one cleanup guard)
/// and run sequentially, which the in-process CLI requires (its global options are static).
/// </summary>
[CollectionDefinition(Name)]
public sealed class RealTenantCollection : ICollectionFixture<IntegrationFixture>
{
    public const string Name = "RealTenant";
}
